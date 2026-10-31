using System.Security.Cryptography;

namespace Fyltrix.Updates;

/// <summary>Read-only live-data lease plus separate snapshot/trial copies. Never restores or rewrites live data.</summary>
internal sealed class ProfileSnapshot : IDisposable
{
    private readonly string source;
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> leases = [];
    internal string TrialFolder { get; }
    private ProfileSnapshot(string source, string trial) { this.source = source; TrialFolder = trial; }
    internal static async Task<ProfileSnapshot> TakeAsync(string source, string transaction, CancellationToken token)
    {
        source = StagingArea.CanonicalPath(source); StagingArea.RejectLinks(source);
        var snapshot = new ProfileSnapshot(source, Path.Combine(transaction, "trial-profile"));
        try
        {
            string backup = Path.Combine(transaction, "profile-snapshot");
            Directory.CreateDirectory(backup); Directory.CreateDirectory(snapshot.TrialFolder);
            long total = 0;
            foreach (var path in Enumerate(source))
            {
                token.ThrowIfCancellationRequested();
                if (snapshot.leases.Count >= 10_000) throw new IOException("Profile has too many files.");
                string relative = Path.GetRelativePath(source, path);
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                snapshot.leases.Add(file); total += file.Length;
                if (total > 256L * 1024 * 1024) throw new IOException("Profile exceeds snapshot limit.");
                snapshot.hashes.Add(relative, Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)));
                file.Position = 0;
                string copy = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
                    await file.CopyToAsync(output, token).ConfigureAwait(false);
                string trial = Path.Combine(snapshot.TrialFolder, relative); Directory.CreateDirectory(Path.GetDirectoryName(trial)!); File.Copy(copy, trial);
            }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }
    internal async Task<bool> UnchangedAsync(CancellationToken token)
    {
        var files = Enumerate(source).ToArray();
        if (files.Length != hashes.Count) return false;
        foreach (string file in files)
        {
            if (!hashes.TryGetValue(Path.GetRelativePath(source, file), out string? expected)) return false;
            await using var stream = File.OpenRead(file);
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != expected) return false;
        }
        return true;
    }
    private static IEnumerable<string> Enumerate(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop(); StagingArea.RejectLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                StagingArea.RejectLinks(path);
                if (Directory.Exists(path)) pending.Push(path); else yield return path;
            }
        }
    }
    public void Dispose() { foreach (var lease in leases) lease.Dispose(); }
}
