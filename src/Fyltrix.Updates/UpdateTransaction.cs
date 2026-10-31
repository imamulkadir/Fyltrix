using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fyltrix.Launcher;

namespace Fyltrix.Updates;

internal sealed record TransactionRecord(int Schema, string Token, string From, string To, string OldPointer, string OldExecutableHash, string LauncherHash, string Phase);
internal sealed record OwnedFile(string Path, string Hash);

internal static class UpdateTransaction
{
    internal const string InventoryName = "update-files.ini";
    internal static async Task AtomicWriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        StagingArea.RejectLinks(path);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            { await file.WriteAsync(bytes, token).ConfigureAwait(false); file.Flush(flushToDisk: true); }
            if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    internal static string HashFile(string path)
    {
        StagingArea.RejectLinks(path);
        using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
    internal static byte[] Pointer(string current, string previous) => JsonSerializer.SerializeToUtf8Bytes(new { current, previous });
    internal static Task WriteJournalAsync(string root, TransactionRecord record, CancellationToken token) =>
        AtomicWriteAsync(Path.Combine(root, UpdateProtocol.JournalFile), JsonSerializer.SerializeToUtf8Bytes(record), token);

    internal static Dictionary<string, string> ReadIni(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return result;
        StagingArea.RejectLinks(file);
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized update inventory.");
        string section = "";
        foreach (string text in File.ReadLines(file))
        {
            string line = text.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            if (line.Length == 0 || line.StartsWith(';')) continue;
            int equal = line.IndexOf('=');
            if (equal <= 0 || !result.TryAdd(section + "/" + line[..equal], line[(equal + 1)..])) throw new InvalidDataException("Invalid update inventory.");
        }
        return result;
    }
    internal static (string Id, string Mode)? Owner(string root)
    {
        var values = ReadIni(Path.Combine(root, ".fyltrix-install.ini"));
        if (values.Count == 0) return null;
        string? id = values.GetValueOrDefault("Installation/AppId"), mode = values.GetValueOrDefault("Installation/Mode");
        if (values.GetValueOrDefault("Installation/Schema") != "1" || !((id == "Fyltrix.PerUser" && mode == "Production") || (id == "Fyltrix.Sandbox.PerUser" && mode == "Sandbox")))
            throw new InvalidDataException("Unknown installation ownership.");
        return (id!, mode!);
    }
    internal static async Task RecordFilesAsync(string root, IEnumerable<OwnedFile> added, CancellationToken token)
    {
        var owner = Owner(root); string id = owner?.Id ?? "Fyltrix.Portable";
        var ini = ReadIni(Path.Combine(root, InventoryName));
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ini.Count != 0)
        {
            if (ini.GetValueOrDefault("UpdateFiles/Schema") != "1" || ini.GetValueOrDefault("UpdateFiles/Owner") != id
                || !int.TryParse(ini.GetValueOrDefault("UpdateFiles/Count"), out int count) || count is < 0 or > 20000) throw new InvalidDataException("Invalid owned-file inventory.");
            for (int i = 0; i < count; i++)
            {
                string path = ini.GetValueOrDefault($"File{i}/Path") ?? "", hash = ini.GetValueOrDefault($"File{i}/Hash") ?? "";
                if (!ValidOwnedPath(path) || !ValidHash(hash) || !files.TryAdd(path, hash)) throw new InvalidDataException("Unsafe owned-file inventory.");
            }
        }
        foreach (var file in added)
        {
            if (!ValidOwnedPath(file.Path) || !ValidHash(file.Hash)) throw new InvalidDataException("Invalid owned update path.");
            if (files.TryGetValue(file.Path, out string? oldHash))
            { if (oldHash != file.Hash) throw new InvalidDataException("Owned update hash changed."); }
            else files.Add(file.Path, file.Hash);
        }
        if (files.Count > 20000) throw new InvalidDataException("Owned-file inventory limit reached.");
        var text = new StringBuilder($"[UpdateFiles]\r\nSchema=1\r\nOwner={id}\r\nCount={files.Count}\r\n");
        int index = 0;
        foreach (var pair in files) text.Append($"[File{index++}]\r\nPath={pair.Key}\r\nHash={pair.Value}\r\n");
        byte[] body = Encoding.Unicode.GetBytes(text.ToString());
        byte[] encoded = [.. Encoding.Unicode.GetPreamble(), .. body];
        if (encoded.Length > 8 * 1024 * 1024) throw new InvalidDataException("Owned-file inventory is oversized.");
        await AtomicWriteAsync(Path.Combine(root, InventoryName), encoded, token).ConfigureAwait(false);
    }
    internal static bool ValidOwnedPath(string path)
    {
        if (path.Contains('\\') || path.Length > 1024) return false;
        var parts = path.Split('/');
        return parts.Length >= 2 && parts[0].StartsWith("app-", StringComparison.Ordinal) && ReleaseVersion.Parse(parts[0][4..]) != null
            && parts.All(p => p.Length > 0 && p is not "." and not ".." && !p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) && !p.EndsWith('.') && !p.EndsWith(' '));
    }
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    internal static async Task CommitMetadataAsync(string root, string version, CancellationToken token)
    {
        var owner = Owner(root);
        if (owner == null) return;
        var core = version.Split('-')[0].Split('.');
        string marker = $"[Installation]\r\nSchema=1\r\nAppId={owner.Value.Id}\r\nMode={owner.Value.Mode}\r\nVersionMajor={core[0]}\r\nVersionMinor={core[1]}\r\nVersionPatch={core[2]}\r\n";
        await AtomicWriteAsync(Path.Combine(root, ".fyltrix-install.ini"), Encoding.UTF8.GetBytes(marker), token).ConfigureAwait(false);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + owner.Value.Id + "_is1", writable: true);
                if (key != null && key.GetValue("InstallLocation") is string location && StagingArea.CanonicalPath(location).Equals(root, StringComparison.OrdinalIgnoreCase))
                    key.SetValue("DisplayVersion", version);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { /* Cosmetic registration cannot invalidate healthy code. */ }
        }
    }
}
