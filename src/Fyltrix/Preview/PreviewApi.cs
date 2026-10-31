using System.Text;
using System.Text.Json;
using Fyltrix.Core;
using Fyltrix.Core.Archives;
using Fyltrix.Core.Imaging;
using Fyltrix.Core.Media;

namespace Fyltrix.Preview;

public sealed record ApiReply(byte[] Body, string ContentType);

/// <summary>Small endpoints for the viewer: archive listing, metadata, thumbnails, image renditions. Runs on a worker thread.</summary>
public static class PreviewApi
{
    private const int MaxListedEntries = 5000;
    /// <summary>Largest side of a PNG rendition (keeps decoded memory bounded for huge TIFF/RAW files).</summary>
    private const int MaxRenditionSide = 8192;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ApiReply? Handle(string name, string url)
    {
        var path = PreviewUrls.QueryString(url, "p");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return name switch
        {
            "archive" => JsonReply(Archive(path)),
            "props" => JsonReply(Props(path, PreviewUrls.QueryString(url, "g"))),
            "thumb" => Thumb(path, (int)(PreviewUrls.QueryLong(url, "s") ?? 256)),
            "image" => Rendition(path),
            "pdfpages" => PdfPages(path),
            "pdfpage" => PdfPage(path, (int)(PreviewUrls.QueryLong(url, "i") ?? 0), (int)(PreviewUrls.QueryLong(url, "w") ?? 1000)),
            _ => null,
        };
    }

    private static ApiReply JsonReply(string json) => new(Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");

    private static string Archive(string path)
    {
        try
        {
            var entries = ArchiveBrowser.GetEntries(path);
            var files = entries.Where(e => !e.IsDirectory).ToList();
            return JsonSerializer.Serialize(new
            {
                kind = ArchiveFormats.Detect(path)?.ToString(),
                files = files.Count,
                folders = entries.Count - files.Count,
                size = files.Sum(e => Math.Max(0, e.Size)),
                archiveSize = new FileInfo(path).Length,
                encrypted = files.Any(e => e.IsEncrypted),
                truncated = files.Count > MaxListedEntries,
                entries = files.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(MaxListedEntries).Select(e => new
                {
                    p = e.Path,
                    s = e.Size,
                    c = e.CompressedSize,
                    m = e.Modified?.ToString("yyyy-MM-dd HH:mm"),
                    e = e.IsEncrypted,
                    l = e.IsLink,
                }),
            }, Json);
        }
        catch (ArchivePasswordException)
        {
            return JsonSerializer.Serialize(new { error = "This archive's file list is encrypted. Open it in the file list to enter the password." }, Json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "This archive can't be read: " + ex.Message }, Json);
        }
    }

    private static string Props(string path, string? group)
    {
        var g = group switch { "video" => ShellProperties.Group.Video, "audio" => ShellProperties.Group.Audio, _ => ShellProperties.Group.Image };
        var info = ShellProperties.Read(path, g);
        return JsonSerializer.Serialize(new
        {
            items = info.Items.Select(i => new { label = i.Label, value = i.Value }),
            durationMs = info.Duration?.TotalMilliseconds,
            width = info.Width,
            height = info.Height,
            codec = info.VideoCodec,
            size = new FileInfo(path).Length,
        }, Json);
    }

    private static ApiReply? Thumb(string path, int size)
    {
        var png = ShellThumbnails.GetPng(path, Math.Clamp(size, 16, 1024));
        return png == null ? null : new ApiReply(png, "image/png");
    }

    /// <summary>Page sizes of a PDF, for Compare's page-by-page view: {"pages":[{"w":..,"h":..}]}. Null when Windows can't open it
    /// (e.g. it needs a password); the viewer then falls back to the standard PDF viewer.</summary>
    private static ApiReply? PdfPages(string path)
    {
        try
        {
            var pages = Core.Pdf.PdfService.PageSizes(path).Select(p => new { w = Math.Round(p.Width, 1), h = Math.Round(p.Height, 1) });
            return JsonReply(JsonSerializer.Serialize(new { pages }));
        }
        catch (Exception) // Windows' PDF engine reports damaged or password-protected files with plain HRESULT exceptions
        {
            return null;
        }
    }

    private static ApiReply? PdfPage(string path, int page, int width)
    {
        try
        {
            var bmp = Core.Pdf.PdfService.RenderPage(path, page, Math.Clamp(width, 64, 4096));
            return new ApiReply(ImageService.EncodePng(bmp), "image/png");
        }
        catch (Exception) // Windows' PDF engine reports damaged or password-protected files with plain HRESULT exceptions
        {
            return null;
        }
    }

    private static ApiReply? Rendition(string path)
    {
        try
        {
            var (w, h) = ImageService.PixelSize(path);
            var bmp = ImageService.Load(path, Math.Max(w, h) > MaxRenditionSide ? MaxRenditionSide : null);
            return new ApiReply(ImageService.EncodePng(bmp), "image/png");
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
