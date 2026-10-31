using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Fyltrix.Launcher;

namespace Fyltrix.Updates;

public enum ApplyStatus { Committed, RolledBack, Refused, RecoveryRequired }
public sealed record ApplyResult(ApplyStatus Status, string Reason);
internal enum ApplyPoint { JournalWritten, PayloadExtracted, TrialPassed, PointerSwitched, Committed }
internal sealed class ApplyOptions
{
    internal string Profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileLens");
    internal TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    internal bool Sandbox;
    internal Action<ApplyPoint>? Checkpoint;
    internal Func<ProcessStartInfo, Process> Start = info => Process.Start(info) ?? throw new IOException("Could not launch application.");
    internal Action<string>? Restart;
}

/// <summary>Consumes a verified handover. Keeps old code and live profile intact; switches only after isolated health succeeds.</summary>
public static class UpdateApplier
{
    public static Task<ApplyResult> ApplyAsync(ReadyHandover ready, string requestPath, CancellationToken token = default) =>
        ApplyCoreAsync(ready, requestPath, new ApplyOptions(), token);

    internal static async Task<ApplyResult> ApplyCoreAsync(ReadyHandover ready, string requestPath, ApplyOptions options, CancellationToken token)
    {
        string root = StagingArea.CanonicalPath(ready.Request.LayoutRoot), stage = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
        string transaction = Path.Combine(stage, "transaction");
        string journal = Path.Combine(root, UpdateProtocol.JournalFile), pointer = Path.Combine(root, "current.json");
        string target = Path.Combine(root, "app-" + ready.Request.TargetVersion), pending = Path.Combine(root, "pending-" + ready.Request.Token);
        TransactionRecord? record = null; bool committed = false;
        FileStream? gate = null; ProfileSnapshot? snapshot = null;
        ApplyResult result;
        try
        {
            StagingArea.RejectLinks(root);
            if (!UpdateProtocol.Supported(root) || ready.Request.AssetName != ReleasePolicy.ZipName(ready.Request.TargetVersion)) return new(ApplyStatus.Refused, "unsupported-layout-or-package");
            if (options.Sandbox && UpdateTransaction.Owner(root)?.Mode != "Sandbox") return new(ApplyStatus.Refused, "sandbox-ownership-required");
            gate = new FileStream(Path.Combine(root, UpdateProtocol.GateFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(journal) || Directory.Exists(target) || Directory.Exists(pending) || Directory.Exists(transaction)) return new(ApplyStatus.Refused, "existing-update-state");
            if (!NoOtherProcesses(root, options.Sandbox)) return new(ApplyStatus.Refused, "application-still-running");
            var old = ReleaseLayout.Resolve(root);
            if (old == null || old.UsedPrevious || old.Version != ready.Request.CurrentVersion) return new(ApplyStatus.Refused, "layout-changed");
            byte[] oldPointer = await StagingArea.ReadSmallAsync(pointer, ReleaseLayout.MaxPointerBytes, token).ConfigureAwait(false);
            ready.Package.Position = 0;
            if (await PackageVerifier.VerifyStreamAsync(ready.Verified, ready.Request.AssetName, ready.Package, token).ConfigureAwait(false) != VerificationFailure.None)
                return new(ApplyStatus.Refused, "package-changed");
            using var zip = new ZipArchive(ready.Package, ZipArchiveMode.Read, true);
            var protocol = zip.GetEntry(UpdateProtocol.CapabilityFile);
            if (protocol == null || protocol.Length > 128) return new(ApplyStatus.Refused, "candidate-protocol-missing");
            using (var input = protocol.Open())
            {
                byte[]? bytes = await GitHubReleaseClient.ReadBoundedAsync(input, 128, token).ConfigureAwait(false);
                if (bytes == null || System.Text.Encoding.UTF8.GetString(bytes) != UpdateProtocol.Capability) return new(ApplyStatus.Refused, "candidate-contract-unsupported");
            }
            long expanded = zip.Entries.Sum(e => e.Length);
            if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < expanded + 512L * 1024 * 1024) return new(ApplyStatus.Refused, "insufficient-space");
            Directory.CreateDirectory(transaction);
            snapshot = await ProfileSnapshot.TakeAsync(options.Profile, transaction, token).ConfigureAwait(false);
            record = new(1, ready.Request.Token, old.Version, ready.Request.TargetVersion, Convert.ToBase64String(oldPointer),
                UpdateTransaction.HashFile(old.Executable), UpdateTransaction.HashFile(Path.Combine(root, "Fyltrix.exe")), "Prepared");
            await UpdateTransaction.WriteJournalAsync(root, record, token).ConfigureAwait(false);
            options.Checkpoint?.Invoke(ApplyPoint.JournalWritten);
            var files = await ExtractAsync(zip, pending, target, ready.Request.TargetVersion, token).ConfigureAwait(false);
            await UpdateTransaction.RecordFilesAsync(root, files, token).ConfigureAwait(false);
            options.Checkpoint?.Invoke(ApplyPoint.PayloadExtracted);
            string trialExe = Path.Combine(target, "Fyltrix.exe");
            bool healthy = await TrialAsync(trialExe, snapshot.TrialFolder, Path.Combine(transaction, "trial-cache"), root,
                ready.Request.TargetVersion, ready.Request.Token, options, token).ConfigureAwait(false);
            if (!healthy) throw new IOException("health-check-failed");
            options.Checkpoint?.Invoke(ApplyPoint.TrialPassed);
            if (!await snapshot.UnchangedAsync(token).ConfigureAwait(false) || !NoOtherProcesses(root, options.Sandbox)) throw new IOException("live-data-or-process-changed");
            if (UpdateTransaction.HashFile(Path.Combine(root, "Fyltrix.exe")) != record.LauncherHash || UpdateTransaction.HashFile(old.Executable) != record.OldExecutableHash)
                throw new IOException("previous-code-changed");
            if (!(await StagingArea.ReadSmallAsync(pointer, ReleaseLayout.MaxPointerBytes, token).ConfigureAwait(false)).AsSpan().SequenceEqual(oldPointer))
                throw new IOException("pointer-changed");
            record = record with { Phase = "Switching" }; await UpdateTransaction.WriteJournalAsync(root, record, token).ConfigureAwait(false);
            await UpdateTransaction.AtomicWriteAsync(pointer, UpdateTransaction.Pointer(record.To, record.From), token).ConfigureAwait(false);
            options.Checkpoint?.Invoke(ApplyPoint.PointerSwitched);
            record = record with { Phase = "Committed" }; await UpdateTransaction.WriteJournalAsync(root, record, token).ConfigureAwait(false);
            committed = true;
            options.Checkpoint?.Invoke(ApplyPoint.Committed);
            await UpdateTransaction.CommitMetadataAsync(root, record.To, CancellationToken.None).ConfigureAwait(false);
            File.Delete(journal);
            result = new(ApplyStatus.Committed, "health-confirmed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { result = await AbortAsync(root, record, committed, "cancelled").ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or JsonException)
        { result = await AbortAsync(root, record, committed, ex is IOException && ex.Message == "health-check-failed" ? "health-check-failed" : ex.GetType().Name).ConfigureAwait(false); }
        finally { snapshot?.Dispose(); gate?.Dispose(); }
        // Rollback restores code only. Do not reopen potentially unvalidated live settings automatically after a failed trial.
        if (result.Status == ApplyStatus.Committed)
        {
            try { if (options.Restart != null) options.Restart(root); else options.Start(new ProcessStartInfo(Path.Combine(root, "Fyltrix.exe")) { UseShellExecute = false, WorkingDirectory = root }).Dispose(); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return result with { Reason = result.Reason + ";restart-failed" }; }
        }
        return result;
    }

    private static async Task<List<OwnedFile>> ExtractAsync(ZipArchive zip, string pending, string target, string version, CancellationToken token)
    {
        Directory.CreateDirectory(pending); StagingArea.RejectLinks(pending);
        var files = new List<OwnedFile>(); string prefix = "app-" + version + "/";
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;
            string relative = name[prefix.Length..];
            string output = Path.GetFullPath(Path.Combine(pending, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!StagingArea.Within(output, pending) || output == pending) throw new InvalidDataException("Unsafe extraction path.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); StagingArea.RejectLinks(output);
            using var input = entry.Open();
            await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long total = 0; byte[] buffer = new byte[64 * 1024]; int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += read; if (total > entry.Length) throw new InvalidDataException("Expanded file length mismatch.");
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); hash.AppendData(buffer, 0, read);
            }
            if (total != entry.Length) throw new InvalidDataException("Expanded file ended early.");
            files.Add(new(name, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }
        Directory.Move(pending, target); return files;
    }
    private static async Task<bool> TrialAsync(string executable, string profile, string cache, string root, string version, string nonce,
        ApplyOptions options, CancellationToken token)
    {
        using var pipe = new NamedPipeServerStream("Fyltrix.Update." + nonce, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("FYLTRIX_", StringComparison.Ordinal) || k == "DOTNET_STARTUP_HOOKS").ToArray()) start.Environment.Remove(key);
        start.Environment["FYLTRIX_UPDATE_TRIAL"] = nonce; start.Environment["FYLTRIX_UPDATE_OWNER"] = Environment.ProcessId.ToString();
        using var process = options.Start(start);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(options.HealthTimeout);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!HealthWire.ClientIs(pipe, process.Id)) return false;
            var hello = await HealthWire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (hello.Count != 2 || hello.GetValueOrDefault("token") != nonce || hello.GetValueOrDefault("version") != version) return false;
            await HealthWire.WriteAsync(pipe, new Dictionary<string, string> { ["token"] = nonce, ["contract"] = "1", ["profile"] = profile, ["cache"] = cache }, timeout.Token).ConfigureAwait(false);
            var healthy = await HealthWire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (healthy.Count != 4 || healthy.GetValueOrDefault("ready") != "1" || healthy.GetValueOrDefault("contract") != "1"
                || healthy.GetValueOrDefault("token") != nonce || healthy.GetValueOrDefault("version") != version) return false;
            await HealthWire.WriteAsync(pipe, new Dictionary<string, string> { ["close"] = nonce }, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (IOException) { return false; }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }
    private static bool NoOtherProcesses(string root, bool sandbox)
    {
        foreach (var process in Process.GetProcessesByName("Fyltrix").Concat(Process.GetProcessesByName("FileLens")))
            using (process)
            {
                if (process.Id == Environment.ProcessId || process.HasExited) continue;
                if (!sandbox || StagingArea.Within(Path.GetFullPath(process.MainModule!.FileName), root)) return false;
            }
        return true;
    }
    private static async Task<ApplyResult> AbortAsync(string root, TransactionRecord? record, bool committed, string reason)
    {
        if (record == null) return new(ApplyStatus.Refused, reason);
        if (committed) return new(ApplyStatus.RecoveryRequired, "commit-cleanup-pending");
        try
        {
            var old = ReleaseLayout.ParsePointer(Convert.FromBase64String(record.OldPointer));
            var now = ReleaseLayout.Resolve(root);
            byte[] current = await StagingArea.ReadSmallAsync(Path.Combine(root, "current.json"), ReleaseLayout.MaxPointerBytes, CancellationToken.None).ConfigureAwait(false);
            if (old?.Current != record.From || now == null || now.Version != record.From && now.Version != record.To
                || !current.AsSpan().SequenceEqual(Convert.FromBase64String(record.OldPointer)) && !current.AsSpan().SequenceEqual(UpdateTransaction.Pointer(record.To, record.From))
                || UpdateTransaction.HashFile(Path.Combine(root, "app-" + record.From, "Fyltrix.exe")) != record.OldExecutableHash
                || UpdateTransaction.HashFile(Path.Combine(root, "Fyltrix.exe")) != record.LauncherHash) return new(ApplyStatus.RecoveryRequired, "rollback-state-changed");
            await UpdateTransaction.AtomicWriteAsync(Path.Combine(root, "current.json"), Convert.FromBase64String(record.OldPointer), CancellationToken.None).ConfigureAwait(false);
            File.Delete(Path.Combine(root, UpdateProtocol.JournalFile));
            return new(ApplyStatus.RolledBack, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return new(ApplyStatus.RecoveryRequired, "rollback-needs-recovery"); }
    }
}
