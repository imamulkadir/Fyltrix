namespace Fyltrix.Core;

/// <summary>
/// Maps local paths to URLs on the private https://filelens.app/ host that the preview
/// WebView2 serves itself (nothing goes over the network). Using a path-shaped URL means
/// relative links inside HTML files (images, CSS) keep working.
///   C:\dir\a b.html      -> https://filelens.app/fs/C%3A/dir/a%20b.html
///   \\server\share\x.pdf -> https://filelens.app/fs/UNC/server/share/x.pdf
/// </summary>
public static class PreviewUrls
{
    public const string Host = "filelens.app";
    public const string Origin = "https://" + Host;
    public const string FsPrefix = "/fs/";
    public const string ViewerPrefix = "/viewer/";
    public const string ApiPrefix = "/api/";

    /// <summary>JSON endpoint used by the viewer, e.g. /api/archive?p=C%3A%5Cx.zip</summary>
    public static string ApiUrl(string name, string path, string? extra = null) =>
        Origin + ApiPrefix + name + "?p=" + Uri.EscapeDataString(path) + (extra != null ? "&" + extra : "");

    public static string? QueryString(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    public static string FileUrl(string path)
    {
        var full = Path.GetFullPath(path);
        IEnumerable<string> segments;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            segments = new[] { "UNC" }.Concat(full.Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            segments = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        }
        return Origin + FsPrefix + string.Join("/", segments.Select(Uri.EscapeDataString));
    }

    /// <summary>Returns the local path for an /fs/ URL, or null if the URL is not one.</summary>
    public static string? PathFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return null;
        var abs = uri.AbsolutePath; // still percent-encoded
        if (!abs.StartsWith(FsPrefix, StringComparison.Ordinal)) return null;
        var parts = abs.Substring(FsPrefix.Length)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();
        if (parts.Count == 0) return null;
        if (parts.Any(p => p == ".." || p.Contains('\\') || p.Contains('/'))) return null;

        if (parts[0].Equals("UNC", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count < 3) return null;
            return @"\\" + string.Join("\\", parts.Skip(1));
        }
        if (parts[0].Length == 2 && parts[0][1] == ':')
        {
            return parts[0] + "\\" + string.Join("\\", parts.Skip(1));
        }
        return null;
    }

    /// <summary>Reads an integer query parameter (e.g. ?max=65536).</summary>
    public static long? QueryLong(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var q = uri.Query.TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == name && long.TryParse(Uri.UnescapeDataString(kv[1]), out var v)) return v;
        }
        return null;
    }

    public static string ViewerUrl(IDictionary<string, string> query)
    {
        var qs = string.Join("&", query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        return Origin + ViewerPrefix + "viewer.html?" + qs;
    }
}
