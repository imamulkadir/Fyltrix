namespace Fyltrix.Core;

/// <summary>
/// Short, plain type names for the Type column ("PDF", "Excel", "Word", "Text", "Markdown") instead of Windows' long ones
/// ("Microsoft Edge PDF Document", "Microsoft Excel Worksheet", "Markdown Source File"). Common types come from a table;
/// anything else is Windows' name with vendor prefixes and "Document"/"File"-style suffixes removed.
/// </summary>
public static class FileTypeNames
{
    private static readonly Dictionary<string, string> ByExtension = Build(new (string names, string exts)[]
    {
        ("PDF", "pdf"),
        ("Word", "doc docx docm dot dotx dotm"),
        ("Excel", "xls xlsx xlsm xlsb xlt xltx xltm"),
        ("PowerPoint", "ppt pptx pptm pps ppsx pot potx"),
        ("OneNote", "one"),
        ("Visio", "vsd vsdx"),
        ("Access", "mdb accdb"),
        ("OpenDocument text", "odt"),
        ("OpenDocument sheet", "ods"),
        ("OpenDocument slides", "odp"),
        ("Rich text", "rtf"),
        ("Text", "txt text"),
        ("Log", "log"),
        ("Markdown", "md markdown"),
        ("CSV", "csv"),
        ("TSV", "tsv"),
        ("JSON", "json jsonc"),
        ("XML", "xml"),
        ("YAML", "yaml yml"),
        ("TOML", "toml"),
        ("Config", "ini cfg conf config properties env"),
        ("HTML", "html htm xhtml"),
        ("CSS", "css scss sass less"),
        ("JavaScript", "js mjs cjs"),
        ("TypeScript", "ts mts cts"),
        ("React", "jsx tsx"),
        ("Python", "py pyw ipynb"),
        ("C#", "cs csx"),
        ("C# project", "csproj"),
        ("Solution", "sln slnx"),
        ("Java", "java jar"),
        ("Kotlin", "kt kts"),
        ("C", "c h"),
        ("C++", "cpp cc cxx hpp hh hxx"),
        ("Go", "go"),
        ("Rust", "rs"),
        ("Ruby", "rb"),
        ("PHP", "php"),
        ("Swift", "swift"),
        ("SQL", "sql"),
        ("PowerShell", "ps1 psm1 psd1"),
        ("Shell script", "sh bash zsh"),
        ("Batch", "bat cmd"),
        ("Application", "exe"),
        ("Installer", "msi msix appx appxbundle msixbundle"),
        ("Library", "dll"),
        ("Shortcut", "lnk url"),
        ("ZIP", "zip"),
        ("7-Zip", "7z"),
        ("RAR", "rar"),
        ("TAR", "tar"),
        ("GZip", "gz tgz"),
        ("BZip2", "bz2 tbz2"),
        ("XZ", "xz txz"),
        ("Disc image", "iso img"),
        ("JPEG", "jpg jpeg jfif"),
        ("PNG", "png"),
        ("GIF", "gif"),
        ("WebP", "webp"),
        ("SVG", "svg"),
        ("HEIC", "heic heif"),
        ("AVIF", "avif"),
        ("Bitmap", "bmp"),
        ("TIFF", "tif tiff"),
        ("Icon", "ico"),
        ("RAW photo", "cr2 cr3 nef arw dng orf rw2 raf"),
        ("Photoshop", "psd"),
        ("MP4", "mp4 m4v"),
        ("MKV", "mkv"),
        ("AVI", "avi"),
        ("MOV", "mov"),
        ("WebM", "webm"),
        ("WMV", "wmv"),
        ("MPEG", "mpg mpeg m2ts"),
        ("MP3", "mp3"),
        ("WAV", "wav"),
        ("FLAC", "flac"),
        ("M4A", "m4a"),
        ("AAC", "aac"),
        ("OGG", "ogg opus"),
        ("WMA", "wma"),
        ("Subtitles", "srt vtt ass"),
        ("Email", "eml msg"),
        ("Calendar", "ics"),
        ("Contact", "vcf"),
        ("Font", "ttf otf woff woff2"),
        ("Certificate", "cer crt pem pfx p12"),
        ("Torrent", "torrent"),
        ("Postman collection", "postman_collection"),
    });

    private static Dictionary<string, string> Build((string name, string exts)[] rows)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, exts) in rows)
            foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                d["." + e] = name;
        return d;
    }

    private static readonly string[] Prefixes = { "Microsoft Edge ", "Microsoft Office ", "Microsoft ", "Adobe Acrobat ", "Adobe ", "Google Chrome ", "Windows " };
    private static readonly string[] Suffixes = { " Source File", " Document", " Worksheet", " Presentation", " File", " Archive", " Image" };

    /// <summary>The short name for a file or folder; <paramref name="shellName"/> is Windows' own (used for types not in the table).</summary>
    public static string Short(string path, bool isDirectory, string shellName)
    {
        if (isDirectory) return "Folder";
        var ext = Path.GetExtension(path);
        if (ext.Length > 1 && ByExtension.TryGetValue(ext, out var known)) return known;
        var s = (shellName ?? "").Trim();
        foreach (var p in Prefixes) if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase) && s.Length > p.Length) { s = s[p.Length..]; break; }
        foreach (var x in Suffixes) if (s.EndsWith(x, StringComparison.OrdinalIgnoreCase) && s.Length > x.Length) { s = s[..^x.Length]; break; }
        if (s.Length > 0) return s;
        return ext.Length > 1 ? ext[1..].ToUpperInvariant() : "File";
    }
}
