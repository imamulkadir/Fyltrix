using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Fyltrix.Core.Tools;

public enum HashAlgorithmKind { Sha256, Sha512, Sha1, Md5 }

/// <summary>Streaming file hashes (any size, constant memory) and checksum comparison.</summary>
public static class FileHasher
{
    public static string Name(HashAlgorithmKind k) => k switch
    {
        HashAlgorithmKind.Sha256 => "SHA-256", HashAlgorithmKind.Sha512 => "SHA-512",
        HashAlgorithmKind.Sha1 => "SHA-1 (legacy)", HashAlgorithmKind.Md5 => "MD5 (legacy)", _ => k.ToString(),
    };

    private static HashAlgorithmName Algo(HashAlgorithmKind k) => k switch
    {
        HashAlgorithmKind.Sha512 => HashAlgorithmName.SHA512, HashAlgorithmKind.Sha1 => HashAlgorithmName.SHA1,
        HashAlgorithmKind.Md5 => HashAlgorithmName.MD5, _ => HashAlgorithmName.SHA256,
    };

    /// <summary>Computes several hashes in one read of the file. Hex is lowercase.</summary>
    public static Dictionary<HashAlgorithmKind, string> Compute(string path, IReadOnlyCollection<HashAlgorithmKind> kinds,
        Action<long>? onBytes = null, CancellationToken ct = default)
    {
        var hashes = kinds.Distinct().ToDictionary(k => k, k => IncrementalHash.CreateHash(Algo(k)));
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var h in hashes.Values) h.AppendData(buffer, 0, n);
                onBytes?.Invoke(n);
            }
            return hashes.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(kv.Value.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            foreach (var h in hashes.Values) h.Dispose();
        }
    }

    public static string Sha256(string path, Action<long>? onBytes = null, CancellationToken ct = default) =>
        Compute(path, new[] { HashAlgorithmKind.Sha256 }, onBytes, ct)[HashAlgorithmKind.Sha256];

    /// <summary>Normalises a pasted checksum ("SHA256: AB CD…", "ab:cd", upper case) to plain lowercase hex.</summary>
    public static string? NormalizeExpected(string text)
    {
        var m = Regex.Match(text, @"(?i)\b([0-9a-f]{2}[\s:\-]?){16,64}\b");
        if (!m.Success) return null;
        var hex = Regex.Replace(m.Value, @"[\s:\-]", "").ToLowerInvariant();
        return hex.Length is 32 or 40 or 64 or 128 ? hex : null;
    }

    public static HashAlgorithmKind? KindForLength(int hexLength) => hexLength switch
    {
        32 => HashAlgorithmKind.Md5, 40 => HashAlgorithmKind.Sha1, 64 => HashAlgorithmKind.Sha256, 128 => HashAlgorithmKind.Sha512, _ => null,
    };

    /// <summary>Constant-time comparison of two hex strings.</summary>
    public static bool Matches(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a.ToLowerInvariant()), System.Text.Encoding.ASCII.GetBytes(b.ToLowerInvariant()));

    /// <summary>Lines of a checksum file ("hash  name" or "hash *name", as written by sha256sum and certutil users).</summary>
    public static List<(string hash, string file)> ParseChecksumFile(string text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"^(?<h>[0-9a-fA-F]{32,128})\s+\*?(?<f>.+)$");
            if (!m.Success) m = Regex.Match(line, @"^\w+\s*\((?<f>.+)\)\s*=\s*(?<h>[0-9a-fA-F]{32,128})$"); // BSD style
            if (m.Success && KindForLength(m.Groups["h"].Length) != null) list.Add((m.Groups["h"].Value.ToLowerInvariant(), m.Groups["f"].Value.Trim()));
        }
        return list;
    }
}
