namespace Fyltrix.Core;

public enum PreviewKind
{
    None,       // folders
    Pdf,        // Edge PDF viewer
    Html,       // rendered page (scripts off by default)
    Svg,
    Image,
    Markdown,
    Json,
    Code,       // syntax highlighted
    Text,
    Csv,
    Docx,
    Xlsx,
    Media,       // played by Chromium (WebView2)
    NativeMedia, // played by Windows Media Foundation (formats Chromium lacks)
    Archive,    // contents listing
    ShellHandler, // Windows preview handler (pptx, msg, doc, ...)
    Auto,       // unknown: sniff text vs binary
}

public static class FileKinds
{
    private static readonly Dictionary<string, PreviewKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = PreviewKind.Pdf,
        [".html"] = PreviewKind.Html, [".htm"] = PreviewKind.Html, [".xhtml"] = PreviewKind.Html, [".mhtml"] = PreviewKind.ShellHandler, [".mht"] = PreviewKind.ShellHandler,
        [".svg"] = PreviewKind.Svg,
        [".png"] = PreviewKind.Image, [".jpg"] = PreviewKind.Image, [".jpeg"] = PreviewKind.Image, [".gif"] = PreviewKind.Image,
        [".bmp"] = PreviewKind.Image, [".webp"] = PreviewKind.Image, [".ico"] = PreviewKind.Image, [".avif"] = PreviewKind.Image,
        [".jpe"] = PreviewKind.Image, [".jfif"] = PreviewKind.Image, [".apng"] = PreviewKind.Image, [".dib"] = PreviewKind.Image,
        // shown via a PNG rendition made with the Windows codecs (Chromium has no decoder for these)
        [".tif"] = PreviewKind.Image, [".tiff"] = PreviewKind.Image, [".heic"] = PreviewKind.Image, [".heif"] = PreviewKind.Image, [".hif"] = PreviewKind.Image,
        [".jxr"] = PreviewKind.Image, [".wdp"] = PreviewKind.Image, [".hdp"] = PreviewKind.Image, [".dds"] = PreviewKind.Image,
        [".dng"] = PreviewKind.Image, [".cr2"] = PreviewKind.Image, [".cr3"] = PreviewKind.Image, [".nef"] = PreviewKind.Image, [".nrw"] = PreviewKind.Image,
        [".arw"] = PreviewKind.Image, [".sr2"] = PreviewKind.Image, [".orf"] = PreviewKind.Image, [".rw2"] = PreviewKind.Image, [".raf"] = PreviewKind.Image,
        [".pef"] = PreviewKind.Image, [".srw"] = PreviewKind.Image,
        [".md"] = PreviewKind.Markdown, [".markdown"] = PreviewKind.Markdown, [".mdown"] = PreviewKind.Markdown,
        [".json"] = PreviewKind.Json, [".geojson"] = PreviewKind.Json, [".jsonc"] = PreviewKind.Json, [".har"] = PreviewKind.Json,
        [".csv"] = PreviewKind.Csv, [".tsv"] = PreviewKind.Csv,
        [".docx"] = PreviewKind.Docx, [".docm"] = PreviewKind.Docx, [".dotx"] = PreviewKind.Docx,
        [".xlsx"] = PreviewKind.Xlsx, [".xlsm"] = PreviewKind.Xlsx, [".xls"] = PreviewKind.Xlsx, [".xlsb"] = PreviewKind.Xlsx, [".ods"] = PreviewKind.Xlsx,
        [".mp4"] = PreviewKind.Media, [".webm"] = PreviewKind.Media, [".mp3"] = PreviewKind.Media, [".wav"] = PreviewKind.Media,
        [".ogg"] = PreviewKind.Media, [".m4a"] = PreviewKind.Media, [".mov"] = PreviewKind.Media,
        [".m4v"] = PreviewKind.Media, [".mkv"] = PreviewKind.Media, [".ogv"] = PreviewKind.Media, [".flac"] = PreviewKind.Media,
        [".aac"] = PreviewKind.Media, [".opus"] = PreviewKind.Media, [".oga"] = PreviewKind.Media, [".weba"] = PreviewKind.Media,
        [".avi"] = PreviewKind.NativeMedia, [".wmv"] = PreviewKind.NativeMedia, [".asf"] = PreviewKind.NativeMedia, [".mpg"] = PreviewKind.NativeMedia,
        [".mpeg"] = PreviewKind.NativeMedia, [".mpe"] = PreviewKind.NativeMedia, [".m2v"] = PreviewKind.NativeMedia,
        [".m2ts"] = PreviewKind.NativeMedia, [".mts"] = PreviewKind.NativeMedia, [".3gp"] = PreviewKind.NativeMedia, [".3g2"] = PreviewKind.NativeMedia,
        [".flv"] = PreviewKind.NativeMedia, [".vob"] = PreviewKind.NativeMedia, [".wtv"] = PreviewKind.NativeMedia, [".wma"] = PreviewKind.NativeMedia,
        [".aif"] = PreviewKind.NativeMedia, [".aiff"] = PreviewKind.NativeMedia, [".aifc"] = PreviewKind.NativeMedia, [".ac3"] = PreviewKind.NativeMedia,
        [".txt"] = PreviewKind.Text, [".log"] = PreviewKind.Text, [".text"] = PreviewKind.Text, [".nfo"] = PreviewKind.Text,
        // Office / Outlook formats with no JS renderer: use the Windows preview handler if one is registered
        [".pptx"] = PreviewKind.ShellHandler, [".ppt"] = PreviewKind.ShellHandler, [".doc"] = PreviewKind.ShellHandler,
        [".msg"] = PreviewKind.ShellHandler, [".vsdx"] = PreviewKind.ShellHandler, [".rtf"] = PreviewKind.ShellHandler,
        [".eml"] = PreviewKind.ShellHandler, [".one"] = PreviewKind.ShellHandler,
    };

    /// <summary>Extension → highlight.js language name.</summary>
    private static readonly Dictionary<string, string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        [".xml"] = "xml", [".xsd"] = "xml", [".xsl"] = "xml", [".xslt"] = "xml", [".config"] = "xml", [".csproj"] = "xml",
        [".props"] = "xml", [".targets"] = "xml", [".resx"] = "xml", [".xaml"] = "xml", [".pom"] = "xml", [".wsdl"] = "xml",
        [".cs"] = "csharp", [".java"] = "java", [".kt"] = "kotlin", [".py"] = "python", [".pyw"] = "python",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript", [".ts"] = "typescript", [".tsx"] = "typescript", [".jsx"] = "javascript",
        [".css"] = "css", [".scss"] = "scss", [".less"] = "less",
        [".sql"] = "sql", [".yml"] = "yaml", [".yaml"] = "yaml", [".ini"] = "ini", [".toml"] = "ini", [".properties"] = "properties",
        [".sh"] = "bash", [".bash"] = "bash", [".ps1"] = "powershell", [".psm1"] = "powershell", [".bat"] = "dos", [".cmd"] = "dos",
        [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp", [".hpp"] = "cpp", [".go"] = "go", [".rs"] = "rust", [".rb"] = "ruby",
        [".php"] = "php", [".swift"] = "swift", [".vb"] = "vbnet", [".gradle"] = "gradle", [".groovy"] = "groovy",
        [".dockerfile"] = "dockerfile", [".diff"] = "diff", [".patch"] = "diff", [".graphql"] = "graphql", [".r"] = "r",
        [".ftl"] = "xml", [".vm"] = "xml", [".j2"] = "django", [".mustache"] = "handlebars", [".hbs"] = "handlebars",
        [".gitignore"] = "bash", [".env"] = "bash", [".editorconfig"] = "ini",
    };

    public static PreviewKind KindFor(string path, bool isDirectory)
    {
        if (isDirectory) return PreviewKind.None;
        if (Archives.ArchiveFormats.IsArchive(path)) return PreviewKind.Archive;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            var name = Path.GetFileName(path);
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Code;
            return PreviewKind.Auto;
        }
        if (ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) && LooksLikeMpegTs(path)) return PreviewKind.NativeMedia;
        if (Map.TryGetValue(ext, out var kind)) return kind;
        if (Code.ContainsKey(ext)) return PreviewKind.Code;
        return PreviewKind.Auto;
    }

    /// <summary>".ts" is both TypeScript and MPEG transport stream; video has a 0x47 sync byte every 188 bytes.</summary>
    public static bool LooksLikeMpegTs(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new byte[377];
            int n = fs.Read(b, 0, b.Length);
            return n == b.Length && b[0] == 0x47 && b[188] == 0x47 && b[376] == 0x47;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static string LanguageFor(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) && Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        return Code.TryGetValue(ext, out var lang) ? lang : "";
    }

    /// <summary>Kinds whose content is text, so two of them can be compared with a line diff.</summary>
    public static bool IsTextLike(PreviewKind kind) =>
        kind is PreviewKind.Markdown or PreviewKind.Json or PreviewKind.Code or PreviewKind.Text
            or PreviewKind.Csv or PreviewKind.Html or PreviewKind.Svg or PreviewKind.Auto;

    public static bool IsAudio(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".wav" or ".ogg" or ".oga" or ".m4a" or ".aac" or ".flac" or ".opus" or ".weba"
            or ".wma" or ".aif" or ".aiff" or ".aifc" or ".ac3";

    public static string MimeFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".html" or ".htm" or ".xhtml" => "text/html",
            ".mht" or ".mhtml" => "multipart/related",
            ".css" => "text/css",
            ".js" or ".mjs" => "text/javascript",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
            ".apng" => "image/apng",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".avif" => "image/avif",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".ogv" => "video/ogg",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" or ".opus" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            ".weba" => "audio/webm",
            ".vtt" => "text/vtt; charset=utf-8",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".txt" or ".md" or ".csv" or ".log" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };
    }
}
