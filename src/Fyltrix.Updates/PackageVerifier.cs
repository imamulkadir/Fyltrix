using System.IO.Compression;
using System.Security.Cryptography;
using Fyltrix.Launcher;

namespace Fyltrix.Updates;

/// <summary>Reads an already supplied file through one handle. Never downloads, extracts, runs, or modifies it.</summary>
public static class PackageVerifier
{
    public static async Task<VerificationFailure> VerifyAsync(VerifiedRelease release, string assetName, string path,
        CancellationToken token = default)
    {
        var signed = release.Files.FirstOrDefault(f => f.Name == assetName);
        if (signed == null) return VerificationFailure.AssetMismatch;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            return await VerifyStreamAsync(release, assetName, stream, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return VerificationFailure.InvalidPackage; }
    }

    internal static async Task<VerificationFailure> VerifyStreamAsync(VerifiedRelease release, string assetName, Stream stream, CancellationToken token)
    {
        var signed = release.Files.FirstOrDefault(f => f.Name == assetName);
        if (signed == null) return VerificationFailure.AssetMismatch;
        try
        {
            stream.Position = 0;
            if (stream.Length != signed.Size) return VerificationFailure.PackageMismatch;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > signed.Size) return VerificationFailure.PackageMismatch;
                hash.AppendData(buffer, 0, read);
            }
            if (total != signed.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != signed.Sha256)
                return VerificationFailure.PackageMismatch;
            stream.Position = 0;
            bool valid = assetName == ReleasePolicy.SetupName ? IsPe(stream) : await IsLayoutZipAsync(stream, release.Release.Version.Text, token).ConfigureAwait(false);
            return valid ? VerificationFailure.None : VerificationFailure.InvalidPackage;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return VerificationFailure.InvalidPackage; }
    }
    private static bool IsPe(Stream stream)
    {
        if (stream.Length < 64) return false;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        if (reader.ReadUInt16() != 0x5a4d) return false;
        stream.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 24) return false;
        stream.Position = offset;
        return reader.ReadUInt32() == 0x4550;
    }
    private static async Task<bool> IsLayoutZipAsync(Stream stream, string version, CancellationToken token)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
        if (zip.Entries.Count is 0 or > 20_000) return false;
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        ZipArchiveEntry? pointer = null;
        string payload = "app-" + version;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            bool directory = name.EndsWith('/');
            name = directory ? name[..^1] : name;
            if (name.Length == 0 || name.Length > 1024 || !names.TryAdd(name, directory)) return false;
            var parts = name.Split('/');
            if (parts.Any(UnsafePart)) return false;
            if (parts[0] != payload && (parts.Length != 1 || directory || parts[0] is not ("Fyltrix.exe" or "current.json" or "LAYOUT.txt" or "UPDATE_PROTOCOL" or "startup.lock"))) return false;
            uint attributes = unchecked((uint)entry.ExternalAttributes);
            if (((attributes >> 16) & 0xf000) == 0xa000 || (attributes & 0x400) != 0) return false;
            if (directory && entry.Length != 0) return false;
            if (entry.Length < 0 || entry.Length > 2L * 1024 * 1024 * 1024 - total) return false;
            total += entry.Length;
            if (name == "current.json") pointer = entry;
        }
        foreach (var name in names.Keys)
        {
            int separator = name.IndexOf('/');
            while (separator >= 0)
            {
                if (names.TryGetValue(name[..separator], out bool directory) && !directory) return false;
                separator = name.IndexOf('/', separator + 1);
            }
        }
        if (!names.TryGetValue("Fyltrix.exe", out bool rootDir) || rootDir
            || !names.TryGetValue(payload + "/Fyltrix.exe", out bool appDir) || appDir
            || pointer == null || pointer.Length is <= 0 or > ReleaseLayout.MaxPointerBytes) return false;
        using var pointerStream = pointer.Open();
        var bytes = await GitHubReleaseClient.ReadBoundedAsync(pointerStream, ReleaseLayout.MaxPointerBytes, token).ConfigureAwait(false);
        var parsed = bytes == null ? null : ReleaseLayout.ParsePointer(bytes);
        return parsed?.Current == version && parsed.Previous == null;
    }
    private static bool UnsafePart(string part)
    {
        if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))) return true;
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]));
    }
}
