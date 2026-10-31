using System.Reflection;
using Fyltrix.Core;
using Microsoft.Web.WebView2.Core;

namespace Fyltrix.Preview;

/// <summary>
/// Answers requests for https://filelens.app/* inside a WebView2:
///   /viewer/...  -> the bundled viewer (embedded in the exe)
///   /fs/...      -> local files (read-only)
/// Nothing is served over the network; the host name only exists inside the WebView.
/// </summary>
public static class PreviewServer
{
    private const long InMemoryLimit = 64L * 1024 * 1024;
    internal const long RangeChunk = 4L * 1024 * 1024;
    private static readonly Dictionary<string, string> Resources = BuildResourceMap();

    private static Dictionary<string, string> BuildResourceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in typeof(PreviewServer).Assembly.GetManifestResourceNames())
        {
            var norm = name.Replace('\\', '/');
            if (norm.StartsWith("Viewer/", StringComparison.OrdinalIgnoreCase))
                map[norm.Substring("Viewer/".Length)] = name;
        }
        return map;
    }

    public static void Attach(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter(PreviewUrls.Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += async (_, e) =>
        {
            // Disk and archive work happens off the UI thread; the response object is created back on it.
            var deferral = e.GetDeferral();
            var url = e.Request.Uri;
            var method = e.Request.Method;
            string? range = null;
            try { if (e.Request.Headers.Contains("Range")) range = e.Request.Headers.GetHeader("Range"); } catch (ArgumentException) { }
            Reply reply;
            try { reply = await Task.Run(() => Build(url, method, range)); }
            catch (Exception ex)
            {
                Diagnostics.Log("Preview server", ex, url);
                reply = Text(500, "Error", ex.Message);
            }
            try
            {
                e.Response = core.Environment.CreateWebResourceResponse(reply.Body, reply.Status, reply.Reason, reply.Headers);
                deferral.Complete();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // the preview was closed while this request was being served; nobody is waiting for it any more
                reply.Body.Dispose();
            }
        };
    }

    private sealed record Reply(Stream Body, int Status, string Reason, string Headers);

    private static Reply Text(int status, string reason, string text, string type = "text/plain; charset=utf-8") =>
        new(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), status, reason, $"Content-Type: {type}\r\nCache-Control: no-store");

    private static Reply Build(string url, string method, string? range)
    {
        var uri = new Uri(url);
        var abs = uri.AbsolutePath;

        if (abs.StartsWith(PreviewUrls.ViewerPrefix, StringComparison.Ordinal))
        {
            var rel = Uri.UnescapeDataString(abs.Substring(PreviewUrls.ViewerPrefix.Length));
            if (!Resources.TryGetValue(rel, out var resName)) return Text(404, "Not Found", rel);
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName)!;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return new Reply(ms, 200, "OK", $"Content-Type: {ContentType(rel)}\r\nCache-Control: no-store");
        }

        if (abs.StartsWith(PreviewUrls.ApiPrefix, StringComparison.Ordinal))
        {
            var reply = PreviewApi.Handle(abs.Substring(PreviewUrls.ApiPrefix.Length), url);
            return reply == null
                ? Text(404, "Not Found", "")
                : new Reply(new MemoryStream(reply.Body), 200, "OK", $"Content-Type: {reply.ContentType}\r\nCache-Control: no-store");
        }

        if (abs.StartsWith(PreviewUrls.FsPrefix, StringComparison.Ordinal))
        {
            var path = PreviewUrls.PathFromUrl(url);
            if (path == null || !File.Exists(path)) return Text(404, "Not Found", "");
            var max = PreviewUrls.QueryLong(url, "max");
            long total = new FileInfo(path).Length;
            var headers = $"Content-Type: {FileKinds.MimeFor(path)}\r\nX-File-Size: {total}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
            if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                return new Reply(new MemoryStream(), 200, "OK", headers);

            // Media elements (and the PDF viewer) ask for byte ranges. WebView2 copies a whole response into memory,
            // so a multi-GB file answered in one piece ran the in-process browser component out of memory and took
            // Fyltrix down with it (0xE0000008). Ranges are answered with bounded chunks; the player asks for more.
            if (range != null && max == null)
            {
                if (!TryParseRange(range, total, out long from, out long to))
                    return new Reply(new MemoryStream(), 416, "Range Not Satisfiable", $"Content-Range: bytes */{total}\r\nCache-Control: no-store");
                to = Math.Min(to, from + RangeChunk - 1);
                var chunk = ReadBytes(path, from, to - from + 1);
                return new Reply(new MemoryStream(chunk, writable: false), 206, "Partial Content",
                    headers + $"\r\nContent-Range: bytes {from}-{from + chunk.Length - 1}/{total}\r\nContent-Length: {chunk.Length}");
            }

            long want = max.HasValue ? Math.Min(max.Value, total) : total;
            Stream body = want <= InMemoryLimit
                ? new MemoryStream(ReadBytes(path, 0, want), writable: false)
                // Large file without a range (rare: the viewer always passes max=, media always sends Range): stream it.
                : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return new Reply(body, 200, "OK", headers);
        }

        return Text(404, "Not Found", "");
    }

    private static byte[] ReadBytes(string path, long offset, long count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Position = offset;
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buf, read, (int)(count - read));
            if (n <= 0) break;
            read += n;
        }
        return read == count ? buf : buf[..read];
    }

    /// <summary>Parses a single "bytes=a-b", "bytes=a-" or "bytes=-n" range against a file of <paramref name="total"/> bytes.</summary>
    internal static bool TryParseRange(string header, long total, out long from, out long to)
    {
        from = to = 0;
        var h = header.Trim();
        if (!h.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || total <= 0) return false;
        var spec = h[6..].Split(',')[0].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var a = spec[..dash].Trim();
        var b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            if (!long.TryParse(b, out var suffix) || suffix <= 0) return false;
            from = Math.Max(0, total - suffix);
            to = total - 1;
            return true;
        }
        if (!long.TryParse(a, out from) || from < 0 || from >= total) return false;
        if (b.Length == 0) to = total - 1;
        else if (!long.TryParse(b, out to) || to < from) return false;
        to = Math.Min(to, total - 1);
        return true;
    }

    private static string ContentType(string rel) => Path.GetExtension(rel).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };
}
