namespace Fyltrix.Core.Archives;

public enum ArchiveKind
{
    Zip,
    SevenZip,
    Rar,
    Tar,
    CompressedTar, // .tar.gz / .tgz / .tar.bz2 / .tar.xz / .tar.zst / .tar.lz
    GZip,          // single compressed file
    BZip2,
    Xz,
}

public enum ArchiveCreateFormat { Zip, Tar, TarGz, TarBz2, GZip }

public static class ArchiveFormats
{
    // Longest suffixes first so ".tar.gz" wins over ".gz".
    private static readonly (string Suffix, ArchiveKind Kind)[] Known =
    {
        (".tar.gz", ArchiveKind.CompressedTar), (".tar.bz2", ArchiveKind.CompressedTar), (".tar.xz", ArchiveKind.CompressedTar),
        (".tar.zst", ArchiveKind.CompressedTar), (".tar.lz", ArchiveKind.CompressedTar),
        (".tgz", ArchiveKind.CompressedTar), (".tbz2", ArchiveKind.CompressedTar), (".tbz", ArchiveKind.CompressedTar), (".txz", ArchiveKind.CompressedTar),
        (".zip", ArchiveKind.Zip), (".zipx", ArchiveKind.Zip), (".7z", ArchiveKind.SevenZip), (".rar", ArchiveKind.Rar),
        (".tar", ArchiveKind.Tar), (".gz", ArchiveKind.GZip), (".bz2", ArchiveKind.BZip2), (".xz", ArchiveKind.Xz),
    };

    public static ArchiveKind? Detect(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var (suffix, kind) in Known)
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return kind;
        return null;
    }

    public static bool IsArchive(string path) => Detect(path) != null;

    /// <summary>Single-file formats have no folder structure to browse.</summary>
    public static bool IsSingleFile(ArchiveKind kind) => kind is ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz;

    /// <summary>"photos.tar.gz" → "photos".</summary>
    public static string StripArchiveExtension(string fileName)
    {
        foreach (var (suffix, _) in Known)
            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return fileName.Substring(0, fileName.Length - suffix.Length);
        return Path.GetFileNameWithoutExtension(fileName);
    }

    public static string Extension(ArchiveCreateFormat f) => f switch
    {
        ArchiveCreateFormat.Zip => ".zip",
        ArchiveCreateFormat.Tar => ".tar",
        ArchiveCreateFormat.TarGz => ".tar.gz",
        ArchiveCreateFormat.TarBz2 => ".tar.bz2",
        ArchiveCreateFormat.GZip => ".gz",
        _ => ".zip",
    };

    public static string DisplayName(ArchiveCreateFormat f) => f switch
    {
        ArchiveCreateFormat.Zip => "ZIP (.zip)",
        ArchiveCreateFormat.Tar => "TAR, uncompressed (.tar)",
        ArchiveCreateFormat.TarGz => "TAR + gzip (.tar.gz)",
        ArchiveCreateFormat.TarBz2 => "TAR + bzip2 (.tar.bz2)",
        ArchiveCreateFormat.GZip => "gzip, single file (.gz)",
        _ => f.ToString(),
    };

    public static bool SupportsLevel(ArchiveCreateFormat f) => f is ArchiveCreateFormat.Zip or ArchiveCreateFormat.TarGz or ArchiveCreateFormat.GZip;

    public const string ReadableSummary = "ZIP, ZIPX (deflate/deflate64/bzip2/lzma/ppmd/zstd), 7Z, RAR (v1.5–v5, extract only), TAR, TAR.GZ/TGZ, TAR.BZ2, TAR.XZ, TAR.ZST, TAR.LZ, GZ, BZ2, XZ";
    public const string WritableSummary = "ZIP, TAR, TAR.GZ, TAR.BZ2, GZ (single file)";
}
