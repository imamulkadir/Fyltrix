using System.Diagnostics;
using System.Text.Json;
using Fyltrix.Launcher;

namespace Fyltrix.Updates;

public enum HandoverFailure { None, InvalidStage, InvalidLayout, NotNewer, NoRunningPayload, InvalidProcess, CouldNotPrepare, CloseCancelled, StillRunning, Untrusted, PackageRejected }
public sealed record ProcessIdentity(int Id, long StartUtcTicks);
public sealed record HandoverRequest(int Schema, string Token, string LayoutRoot, string CurrentVersion, string TargetVersion, string AssetName, ProcessIdentity[] Processes);
public sealed class HandoverPreparation
{
    public string RequestPath { get; }
    public HandoverRequest Request { get; }
    internal StagedUpdate Stage { get; }
    internal HandoverPreparation(string path, HandoverRequest request, StagedUpdate stage) { RequestPath = path; Request = request; Stage = stage; }
}
public sealed record PreparationResult(HandoverFailure Failure, HandoverPreparation? Preparation = null);
public sealed record HandoverResult(HandoverFailure Failure, ReadyHandover? Ready = null);

/// <summary>Ownership of a read-only verified package handle. No files in the installation are changed.</summary>
public sealed class ReadyHandover : IDisposable
{
    public VerifiedRelease Verified { get; }
    public HandoverRequest Request { get; }
    public Stream Package { get; }
    internal ReadyHandover(VerifiedRelease verified, HandoverRequest request, Stream stream) { Verified = verified; Request = request; Package = stream; }
    public void Dispose() => Package.Dispose();
}

/// <summary>Stage 5 boundary: prepare, honor normal close, wait, reauthenticate, return a read lease. Never apply an update.</summary>
public static class UpdateHandover
{
    public const string RequestName = "handover.json";
    internal const string ApprovalName = "close-approved";

    public static async Task<StagedUpdate?> LoadStageAsync(string folder, IReadOnlyList<string>? explicitTestKeys = null, CancellationToken token = default)
    {
        try
        {
            folder = Path.GetFullPath(folder); StagingArea.ValidateRoot(Path.GetDirectoryName(folder)!);
            string id = System.Text.Encoding.ASCII.GetString(await StagingArea.ReadSmallAsync(Path.Combine(folder, StagingArea.Marker), 32, token).ConfigureAwait(false));
            if (!Guid.TryParseExact(id, "N", out _) || Path.GetFileName(folder) != "stage-" + id) return null;
            var release = ReleaseInfo.Parse(await StagingArea.ReadSmallAsync(Path.Combine(folder, "release.json"), ReleasePolicy.MaxMetadataBytes, token).ConfigureAwait(false));
            if (release == null) return null;
            var manifest = await StagingArea.ReadSmallAsync(Path.Combine(folder, ReleasePolicy.ManifestName), ReleasePolicy.MaxManifestBytes, token).ConfigureAwait(false);
            var sig = await StagingArea.ReadSmallAsync(Path.Combine(folder, ReleasePolicy.SignatureName), ReleasePolicy.MaxSignatureBytes, token).ConfigureAwait(false);
            var verified = SignedManifest.Verify(release, manifest, sig, explicitTestKeys);
            if (verified.Verified == null) return null;
            // A successful staging directory contains exactly one supported package, not a guessed choice.
            var files = verified.Verified.Files.Where(f => File.Exists(Path.Combine(folder, f.Name))).ToArray();
            if (files.Length != 1) return null;
            var stage = new StagedUpdate(folder, files[0].Name, verified.Verified, id);
            StagingArea.RejectLinks(stage.PackagePath);
            return await PackageVerifier.VerifyAsync(stage.Verified, stage.AssetName, stage.PackagePath, token).ConfigureAwait(false) == VerificationFailure.None ? stage : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public static async Task<PreparationResult> PrepareAsync(StagedUpdate stage, string layoutRoot, CancellationToken token = default)
    {
        try
        {
            if (!await StoredEvidenceMatches(stage, token).ConfigureAwait(false)) return new(HandoverFailure.InvalidStage);
            layoutRoot = StagingArea.CanonicalPath(layoutRoot);
            var payload = ValidateLayout(layoutRoot);
            if (payload == null) return new(HandoverFailure.InvalidLayout);
            if (stage.Verified.Release.Version.CompareTo(ReleaseVersion.Parse(payload.Version)) <= 0) return new(HandoverFailure.NotNewer);
            var processes = Capture(layoutRoot);
            if (!processes.Any(p => p.Path.Equals(payload.Executable, StringComparison.OrdinalIgnoreCase))) return new(HandoverFailure.NoRunningPayload);
            if (await PackageVerifier.VerifyAsync(stage.Verified, stage.AssetName, stage.PackagePath, token).ConfigureAwait(false) != VerificationFailure.None)
                return new(HandoverFailure.PackageRejected);
            var request = new HandoverRequest(1, stage.Token, layoutRoot, payload.Version, stage.Verified.Release.Version.Text,
                stage.AssetName, processes.Select(p => p.Identity).ToArray());
            string path = Path.Combine(stage.Directory, RequestName);
            await StagingArea.WriteNewAsync(path, JsonSerializer.SerializeToUtf8Bytes(request), token).ConfigureAwait(false);
            return new(HandoverFailure.None, new(path, request, stage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(HandoverFailure.CouldNotPrepare); }
    }

    // Caller runs on its UI synchronization context. Never use Application.Shutdown or kill a process.
    public static async Task<HandoverFailure> RequestCloseAsync(HandoverPreparation prepared, Func<bool> normalClose, CancellationToken token = default)
    {
        bool accepted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!await StoredEvidenceMatches(prepared.Stage, token)
                || !await RequestMatches(prepared.RequestPath, prepared.Request, token)) return HandoverFailure.InvalidStage;
            if (await PackageVerifier.VerifyAsync(prepared.Stage.Verified, prepared.Stage.AssetName, prepared.Stage.PackagePath, token) != VerificationFailure.None)
                return HandoverFailure.PackageRejected;
            token.ThrowIfCancellationRequested();
            if (!normalClose()) return HandoverFailure.CloseCancelled;
            token.ThrowIfCancellationRequested();
            // Close() has returned from Closed. Finish this tiny approval write synchronously before App.Run can end.
            StagingArea.RejectLinks(prepared.Stage.Directory);
            string pending = Path.Combine(prepared.Stage.Directory, "approval-" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                using (var approval = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    approval.Write(System.Text.Encoding.ASCII.GetBytes(prepared.Request.Token));
                    approval.Flush(flushToDisk: true);
                }
                File.Move(pending, Path.Combine(prepared.Stage.Directory, ApprovalName));
            }
            finally
            {
                try { File.Delete(pending); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A partial approval is never authorization. */ }
            }
            accepted = true;
            return HandoverFailure.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return HandoverFailure.CouldNotPrepare; }
        finally
        {
            if (!accepted)
            {
                // Remove only this unchanged, testable request. Keep the verified download for an explicit retry.
                try { if (await RequestMatches(prepared.RequestPath, prepared.Request, CancellationToken.None)) File.Delete(prepared.RequestPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Unapproved requests can never become ready. */ }
            }
        }
    }

    public static async Task<HandoverResult> WaitForReadyAsync(string requestPath, TimeSpan? waitTimeout = null,
        CancellationToken token = default, IReadOnlyList<string>? explicitTestKeys = null)
    {
        var timeout = waitTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        FileStream? lease = null;
        var handles = new List<Process>();
        try
        {
            requestPath = Path.GetFullPath(requestPath);
            string folder = Path.GetDirectoryName(requestPath)!;
            if (Path.GetFileName(requestPath) != RequestName) return new(HandoverFailure.InvalidStage);
            var data = await StagingArea.ReadSmallAsync(requestPath, 16 * 1024, token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 4 });
            if (!JsonRules.Unique(json.RootElement) || !JsonRules.Exact(json.RootElement,
                "Schema", "Token", "LayoutRoot", "CurrentVersion", "TargetVersion", "AssetName", "Processes")) return new(HandoverFailure.InvalidStage);
            var identities = json.RootElement.GetProperty("Processes");
            if (identities.ValueKind != JsonValueKind.Array || identities.EnumerateArray().Any(p => !JsonRules.Exact(p, "Id", "StartUtcTicks")))
                return new(HandoverFailure.InvalidStage);
            var request = JsonSerializer.Deserialize<HandoverRequest>(data);
            if (request == null || request.Schema != 1 || !Guid.TryParseExact(request.Token, "N", out _)
                || request.Processes is not { Length: > 0 and <= 32 } || request.Processes.Any(p => p == null || p.Id <= 0 || p.StartUtcTicks <= 0)
                || request.Processes.Select(p => p.Id).Distinct().Count() != request.Processes.Length
                || !Path.IsPathFullyQualified(request.LayoutRoot) || ReleaseVersion.Parse(request.CurrentVersion) == null
                || ReleaseVersion.Parse(request.TargetVersion) is not { IsPrerelease: false } target
                || target.CompareTo(ReleaseVersion.Parse(request.CurrentVersion)) <= 0
                || !ReleasePolicy.IsPackage(request.AssetName, request.TargetVersion)) return new(HandoverFailure.InvalidStage);
            StagingArea.ValidateRoot(Path.GetDirectoryName(folder)!);
            if (Path.GetFileName(folder) != "stage-" + request.Token
                || System.Text.Encoding.ASCII.GetString(await StagingArea.ReadSmallAsync(Path.Combine(folder, StagingArea.Marker), 32, token).ConfigureAwait(false)) != request.Token)
                return new(HandoverFailure.InvalidStage);
            var release = ReleaseInfo.Parse(await StagingArea.ReadSmallAsync(Path.Combine(folder, "release.json"), ReleasePolicy.MaxMetadataBytes, token).ConfigureAwait(false));
            if (release?.Version.Text != request.TargetVersion) return new(HandoverFailure.InvalidStage);
            var manifest = await StagingArea.ReadSmallAsync(Path.Combine(folder, ReleasePolicy.ManifestName), ReleasePolicy.MaxManifestBytes, token).ConfigureAwait(false);
            var signature = await StagingArea.ReadSmallAsync(Path.Combine(folder, ReleasePolicy.SignatureName), ReleasePolicy.MaxSignatureBytes, token).ConfigureAwait(false);
            var verified = SignedManifest.Verify(release, manifest, signature, explicitTestKeys);
            if (verified.Verified == null) return new(HandoverFailure.Untrusted);
            string package = Path.Combine(folder, request.AssetName); StagingArea.RejectLinks(package);
            lease = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            if (await PackageVerifier.VerifyStreamAsync(verified.Verified, request.AssetName, lease, token).ConfigureAwait(false) != VerificationFailure.None)
                return new(HandoverFailure.PackageRejected);
            if (!LayoutMatches(request)) return new(HandoverFailure.InvalidLayout);
            foreach (var identity in request.Processes)
            {
                Process process;
                try { process = Process.GetProcessById(identity.Id); }
                catch (ArgumentException) { continue; } // Already exited; the final layout-wide scan is still required.
                handles.Add(process);
                if (!process.HasExited && (process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks
                    || !IsLayoutExecutable(process.MainModule!.FileName, request.LayoutRoot))) return new(HandoverFailure.InvalidProcess);
            }
            using var clock = CancellationTokenSource.CreateLinkedTokenSource(token); clock.CancelAfter(timeout);
            try { await Task.WhenAll(handles.Select(p => p.WaitForExitAsync(clock.Token))).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(HandoverFailure.StillRunning); }
            token.ThrowIfCancellationRequested();
            if (!await RequestMatches(requestPath, request, token).ConfigureAwait(false)) return new(HandoverFailure.InvalidStage);
            if (!File.Exists(Path.Combine(folder, ApprovalName))) return new(HandoverFailure.CloseCancelled);
            if (System.Text.Encoding.ASCII.GetString(await StagingArea.ReadSmallAsync(Path.Combine(folder, ApprovalName), 32, token).ConfigureAwait(false)) != request.Token)
                return new(HandoverFailure.CloseCancelled);
            if (!LayoutMatches(request)) return new(HandoverFailure.InvalidLayout);
            if (Capture(request.LayoutRoot).Count != 0) return new(HandoverFailure.StillRunning);
            lease.Position = 0;
            var ready = new ReadyHandover(verified.Verified, request, lease); lease = null;
            return new(HandoverFailure.None, ready);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { return new(HandoverFailure.InvalidStage); }
        finally { lease?.Dispose(); foreach (var process in handles) process.Dispose(); }
    }

    private static Payload? ValidateLayout(string root)
    {
        StagingArea.RejectLinks(root);
        StagingArea.RejectLinks(Path.Combine(root, "Fyltrix.exe"));
        if (!Path.IsPathFullyQualified(root)) return null;
        for (var parent = new DirectoryInfo(root); parent != null; parent = parent.Parent)
            if (Directory.Exists(Path.Combine(parent.FullName, ".git")) || File.Exists(Path.Combine(parent.FullName, ".git"))
                || File.Exists(Path.Combine(parent.FullName, "publish.ps1"))) return null;
        foreach (var kind in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string profile = StagingArea.CanonicalPath(Path.Combine(Environment.GetFolderPath(kind), "FileLens"));
            if (StagingArea.Within(root, profile) || StagingArea.Within(profile, root)) return null;
        }
        var resolved = ReleaseLayout.Resolve(root);
        return resolved is { UsedPrevious: false } && File.Exists(Path.Combine(root, "Fyltrix.exe")) ? resolved : null;
    }
    private static bool LayoutMatches(HandoverRequest request) => ValidateLayout(request.LayoutRoot)?.Version == request.CurrentVersion;
    private static bool IsLayoutExecutable(string executable, string root)
    {
        string path = Path.GetFullPath(executable);
        if (path.Equals(Path.Combine(root, "Fyltrix.exe"), StringComparison.OrdinalIgnoreCase)) return true;
        string directory = Path.GetDirectoryName(path)!;
        return Path.GetFileName(path).Equals("Fyltrix.exe", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(directory)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true
            && Path.GetFileName(directory).StartsWith("app-", StringComparison.Ordinal)
            && ReleaseVersion.Parse(Path.GetFileName(directory)[4..]) != null;
    }
    private static List<(ProcessIdentity Identity, string Path)> Capture(string root)
    {
        var result = new List<(ProcessIdentity, string)>();
        foreach (var process in Process.GetProcessesByName("Fyltrix"))
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    string path = process.MainModule!.FileName;
                    if (IsLayoutExecutable(path, root)) result.Add((new(process.Id, process.StartTime.ToUniversalTime().Ticks), path));
                }
                catch (InvalidOperationException) { /* A process exited during capture. */ }
            }
        if (result.Count > 32) throw new IOException("Too many instances of this layout.");
        return result;
    }
    private static async Task<bool> StoredEvidenceMatches(StagedUpdate stage, CancellationToken token)
    {
        try
        {
            StagingArea.ValidateRoot(Path.GetDirectoryName(stage.Directory)!);
            if (Path.GetFileName(stage.Directory) != "stage-" + stage.Token) return false;
            return (await StagingArea.ReadSmallAsync(Path.Combine(stage.Directory, StagingArea.Marker), 32, token).ConfigureAwait(false)).AsSpan().SequenceEqual(System.Text.Encoding.ASCII.GetBytes(stage.Token))
                && (await StagingArea.ReadSmallAsync(Path.Combine(stage.Directory, ReleasePolicy.ManifestName), ReleasePolicy.MaxManifestBytes, token).ConfigureAwait(false)).AsSpan().SequenceEqual(stage.Verified.ManifestBytes.Span)
                && (await StagingArea.ReadSmallAsync(Path.Combine(stage.Directory, ReleasePolicy.SignatureName), ReleasePolicy.MaxSignatureBytes, token).ConfigureAwait(false)).AsSpan().SequenceEqual(stage.Verified.SignatureBytes.Span)
                && (await StagingArea.ReadSmallAsync(Path.Combine(stage.Directory, "release.json"), ReleasePolicy.MaxMetadataBytes, token).ConfigureAwait(false)).AsSpan().SequenceEqual(stage.Verified.Release.SerializeEvidence());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    private static async Task<bool> RequestMatches(string path, HandoverRequest request, CancellationToken token)
    {
        try { return (await StagingArea.ReadSmallAsync(path, 16 * 1024, token).ConfigureAwait(false)).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(request)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
