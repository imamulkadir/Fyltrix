using System.Diagnostics;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using Fyltrix.Launcher;

namespace Fyltrix.Updates;

internal static class UpdateRecovery
{
    internal static async Task<ApplyResult> RecoverAsync(string requestPath, string nonce, IReadOnlyList<string>? keys, ApplyOptions options, CancellationToken token)
    {
        string stage = Path.GetDirectoryName(requestPath)!;
        var loaded = await UpdateHandover.LoadStageAsync(stage, keys, token).ConfigureAwait(false);
        if (loaded == null || loaded.Token != nonce) return new(ApplyStatus.Refused, "recovery-untrusted-stage");
        var requestBytes = await StagingArea.ReadSmallAsync(requestPath, 16 * 1024, token).ConfigureAwait(false);
        using var requestJson = JsonDocument.Parse(requestBytes);
        if (!JsonRules.Unique(requestJson.RootElement) || !JsonRules.Exact(requestJson.RootElement,
            "Schema", "Token", "LayoutRoot", "CurrentVersion", "TargetVersion", "AssetName", "Processes")) return new(ApplyStatus.Refused, "recovery-invalid-request");
        var request = JsonSerializer.Deserialize<HandoverRequest>(requestBytes);
        if (request == null || request.Schema != 1 || request.Token != nonce || request.TargetVersion != loaded.Verified.Release.Version.Text
            || request.AssetName != loaded.AssetName || request.AssetName != ReleasePolicy.ZipName(request.TargetVersion)
            || !Path.IsPathFullyQualified(request.LayoutRoot) || ReleaseVersion.Parse(request.CurrentVersion) is not { } from
            || loaded.Verified.Release.Version.CompareTo(from) <= 0) return new(ApplyStatus.Refused, "recovery-request-mismatch");
        if (System.Text.Encoding.ASCII.GetString(await StagingArea.ReadSmallAsync(Path.Combine(stage, UpdateHandover.ApprovalName), 32, token).ConfigureAwait(false)) != nonce)
            return new(ApplyStatus.Refused, "recovery-not-approved");
        string root = StagingArea.CanonicalPath(request.LayoutRoot); StagingArea.RejectLinks(root);
        if (!UpdateProtocol.Supported(root) || UpdateHandover.ValidateLayout(root) == null || options.Sandbox && UpdateTransaction.Owner(root)?.Mode != "Sandbox") return new(ApplyStatus.Refused, "recovery-unsupported-layout");
        foreach (var process in Process.GetProcessesByName("Fyltrix").Concat(Process.GetProcessesByName("FileLens")))
            using (process)
                if (process.Id != Environment.ProcessId && !process.HasExited && (!options.Sandbox || StagingArea.Within(Path.GetFullPath(process.MainModule!.FileName), root)))
                    return new(ApplyStatus.Refused, "recovery-application-running");
        using (var gate = new FileStream(Path.Combine(root, UpdateProtocol.GateFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            string journalPath = Path.Combine(root, UpdateProtocol.JournalFile);
            var bytes = await StagingArea.ReadSmallAsync(journalPath, 16 * 1024, token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes);
            if (!JsonRules.Unique(json.RootElement) || !JsonRules.Exact(json.RootElement, "Schema", "Token", "From", "To", "OldPointer", "OldExecutableHash", "LauncherHash", "Phase"))
                return new(ApplyStatus.Refused, "recovery-invalid-journal");
            var record = JsonSerializer.Deserialize<TransactionRecord>(bytes);
            if (record == null || record.Schema != 1 || record.Token != nonce || record.From != request.CurrentVersion || record.To != request.TargetVersion
                || record.Phase is not ("Prepared" or "Switching" or "Committed")
                || UpdateTransaction.HashFile(Path.Combine(root, "Fyltrix.exe")) != record.LauncherHash
                || UpdateTransaction.HashFile(Path.Combine(root, "app-" + record.From, "Fyltrix.exe")) != record.OldExecutableHash)
                return new(ApplyStatus.Refused, "recovery-journal-mismatch");
            var old = ReleaseLayout.ParsePointer(Convert.FromBase64String(record.OldPointer));
            var now = ReleaseLayout.Resolve(root);
            byte[] current = await StagingArea.ReadSmallAsync(Path.Combine(root, "current.json"), ReleaseLayout.MaxPointerBytes, token).ConfigureAwait(false);
            if (old?.Current != record.From || now == null || now.Version != record.From && now.Version != record.To)
                return new(ApplyStatus.Refused, "recovery-pointer-changed");
            if (!current.AsSpan().SequenceEqual(Convert.FromBase64String(record.OldPointer)) && !current.AsSpan().SequenceEqual(UpdateTransaction.Pointer(record.To, record.From)))
                return new(ApplyStatus.Refused, "recovery-pointer-changed");
            var recoveredFiles = await ValidateTargetAsync(loaded, root, token).ConfigureAwait(false);
            if (record.Phase == "Committed" && recoveredFiles == null) return new(ApplyStatus.Refused, "recovery-candidate-changed");
            if (recoveredFiles != null) await UpdateTransaction.RecordFilesAsync(root, recoveredFiles, token).ConfigureAwait(false);
            if (record.Phase == "Committed")
            {
                if (now.Version != record.To) return new(ApplyStatus.Refused, "recovery-commit-changed");
                await UpdateTransaction.CommitMetadataAsync(root, record.To, token).ConfigureAwait(false);
            }
            else await UpdateTransaction.AtomicWriteAsync(Path.Combine(root, "current.json"), Convert.FromBase64String(record.OldPointer), token).ConfigureAwait(false);
            File.Delete(journalPath);
            return new(record.Phase == "Committed" ? ApplyStatus.Committed : ApplyStatus.RolledBack, "interrupted-update-recovered");
        }
    }
    private static async Task<List<OwnedFile>?> ValidateTargetAsync(StagedUpdate stage, string root, CancellationToken token)
    {
        await using var file = new FileStream(stage.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        if (await PackageVerifier.VerifyStreamAsync(stage.Verified, stage.AssetName, file, token).ConfigureAwait(false) != VerificationFailure.None) return null;
        file.Position = 0; using var zip = new ZipArchive(file, ZipArchiveMode.Read, true);
        string prefix = "app-" + stage.Verified.Release.Version.Text + "/"; var result = new List<OwnedFile>();
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;
            string target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!StagingArea.Within(target, root) || !File.Exists(target)) return null;
            StagingArea.RejectLinks(target);
            using var stream = entry.Open(); string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
            if (UpdateTransaction.HashFile(target) != hash) return null;
            result.Add(new(name, hash));
        }
        return result;
    }
}
