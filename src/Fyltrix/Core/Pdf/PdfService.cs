using System.Text;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Fyltrix.Core.Pdf;

public sealed record PdfInfo(int Pages, int Version, bool Encrypted, bool Signed, IReadOnlyList<(string Label, string Value)> Metadata);

public sealed class PdfPasswordException : Exception
{
    public bool WasWrong { get; }
    public PdfPasswordException(bool wasWrong) : base(wasWrong ? "The password is incorrect." : "This PDF is password protected.") => WasWrong = wasWrong;
}

/// <summary>
/// PDF page operations with PDFsharp. Every operation writes a new file (via a temporary file) and never changes
/// the source. Rotating and reordering keep bookmarks and metadata; extracting, splitting and merging copy pages.
/// Any of them invalidates digital signatures, which <see cref="PdfInfo.Signed"/> lets the UI warn about.
/// </summary>
public static class PdfService
{
    private static PdfDocument Open(string path, PdfDocumentOpenMode mode, string? password)
    {
        int asked = 0;
        try
        {
            // PDFsharp asks again after a wrong password and returns null when aborted: answer once, then give up
            var doc = PdfReader.Open(path, mode, args =>
            {
                asked++;
                if (password == null || asked > 1) args.Abort = true;
                else args.Password = password;
            }, new PdfReaderOptions());
            return doc ?? throw new PdfPasswordException(password != null);
        }
        catch (PdfReaderException) when (asked > 0)
        {
            throw new PdfPasswordException(password != null);
        }
        catch (Exception ex) when (asked > 0 && ex is not PdfPasswordException)
        {
            throw new PdfPasswordException(password != null);
        }
    }

    public static PdfInfo Info(string path, string? password = null)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Import, password);
        bool encrypted = Contains(path, "/Encrypt");
        var i = doc.Info;
        var meta = new List<(string, string)>();
        void Add(string label, string? v) { if (!string.IsNullOrWhiteSpace(v)) meta.Add((label, v.Trim())); }
        Add("Title", i.Title);
        Add("Author", i.Author);
        Add("Subject", i.Subject);
        Add("Keywords", i.Keywords);
        Add("Created by", i.Creator);
        Add("Produced by", i.Producer);
        if (i.CreationDate != DateTime.MinValue) Add("Created", i.CreationDate.ToString("g"));
        if (i.ModificationDate != DateTime.MinValue) Add("Modified", i.ModificationDate.ToString("g"));
        return new PdfInfo(doc.PageCount, doc.Version, encrypted, Contains(path, "/ByteRange"), meta);
    }

    /// <summary>Signed PDFs carry a signature dictionary with a /ByteRange entry.</summary>
    public static bool IsSigned(string path) => Contains(path, "/ByteRange");

    /// <summary>Streams through the file looking for a PDF keyword (works for any size).</summary>
    private static bool Contains(string path, string keyword)
    {
        var needle = Encoding.ASCII.GetBytes(keyword);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var buf = new byte[1 << 20];
        int keep = 0, n;
        while ((n = fs.Read(buf, keep, buf.Length - keep)) > 0)
        {
            int len = keep + n;
            if (buf.AsSpan(0, len).IndexOf(needle) >= 0) return true;
            keep = Math.Min(needle.Length - 1, len);
            Array.Copy(buf, len - keep, buf, 0, keep);
        }
        return false;
    }

    /// <summary>Parses "1-3, 5, 8-" (1-based, inclusive) into zero-based page indexes, in the given order.</summary>
    public static List<int> ParsePages(string spec, int pageCount)
    {
        var result = new List<int>();
        foreach (var raw in spec.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            var dash = part.IndexOf('-');
            int a, b;
            if (dash < 0)
            {
                if (!int.TryParse(part, out a)) throw new FormatException($"'{part}' isn't a page number.");
                b = a;
            }
            else
            {
                var left = part.Substring(0, dash);
                var right = part.Substring(dash + 1);
                a = left.Length == 0 ? 1 : int.TryParse(left, out var l) ? l : throw new FormatException($"'{part}' isn't a page range.");
                b = right.Length == 0 ? pageCount : int.TryParse(right, out var r) ? r : throw new FormatException($"'{part}' isn't a page range.");
            }
            if (a < 1 || b < 1 || a > pageCount || b > pageCount) throw new FormatException($"Page {Math.Max(a, b)} doesn't exist (the document has {pageCount}).");
            if (a <= b) for (int p = a; p <= b; p++) result.Add(p - 1);
            else for (int p = a; p >= b; p--) result.Add(p - 1);
        }
        if (result.Count == 0) throw new FormatException("No pages given.");
        return result;
    }

    private static void SaveNew(PdfDocument doc, string output)
    {
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException($"'{Path.GetFileName(output)}' already exists.");
        var temp = output + ".fl-partial";
        try
        {
            doc.Save(temp);
            File.Move(temp, output);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    public static void Merge(IReadOnlyList<string> inputs, string output, Func<string, string?>? passwordFor = null, CancellationToken ct = default)
    {
        if (inputs.Count < 2) throw new ArgumentException("Choose at least two PDFs to merge.");
        using var target = new PdfDocument();
        foreach (var path in inputs)
        {
            ct.ThrowIfCancellationRequested();
            using var src = Open(path, PdfDocumentOpenMode.Import, passwordFor?.Invoke(path));
            foreach (var page in src.Pages) target.AddPage(page);
        }
        using (var first = Open(inputs[0], PdfDocumentOpenMode.Import, passwordFor?.Invoke(inputs[0])))
        {
            target.Info.Title = first.Info.Title;
            target.Info.Author = first.Info.Author;
        }
        SaveNew(target, output);
    }

    /// <summary>Copies the given pages (zero-based, in this order) into a new PDF.</summary>
    public static void ExtractPages(string input, IReadOnlyList<int> pages, string output, string? password = null)
    {
        using var src = Open(input, PdfDocumentOpenMode.Import, password);
        using var target = new PdfDocument();
        foreach (var p in pages)
        {
            if (p < 0 || p >= src.PageCount) throw new ArgumentOutOfRangeException(nameof(pages), $"Page {p + 1} doesn't exist.");
            target.AddPage(src.Pages[p]);
        }
        target.Info.Title = src.Info.Title;
        target.Info.Author = src.Info.Author;
        SaveNew(target, output);
    }

    /// <summary>Splits into files of <paramref name="pagesPerFile"/> pages: "name (pages 1-5).pdf"… Returns the files written.</summary>
    public static List<string> Split(string input, int pagesPerFile, string outputFolder, string? password = null, CancellationToken ct = default)
    {
        if (pagesPerFile < 1) throw new ArgumentException("Pages per file must be at least 1.");
        int count;
        using (var probe = Open(input, PdfDocumentOpenMode.Import, password)) count = probe.PageCount;
        var stem = Path.GetFileNameWithoutExtension(input);
        var written = new List<string>();
        try
        {
            for (int start = 0; start < count; start += pagesPerFile)
            {
                ct.ThrowIfCancellationRequested();
                int end = Math.Min(count, start + pagesPerFile);
                var name = end - start == 1 ? $"{stem} (page {start + 1}).pdf" : $"{stem} (pages {start + 1}-{end}).pdf";
                var output = FileOps.UniqueName(outputFolder, name);
                ExtractPages(input, Enumerable.Range(start, end - start).ToList(), output, password);
                written.Add(output);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var f in written) { try { File.Delete(f); } catch (IOException) { } }
            throw;
        }
        return written;
    }

    /// <summary>Writes the pages in a new order; <paramref name="order"/> lists every zero-based page exactly once.</summary>
    public static void Reorder(string input, IReadOnlyList<int> order, string output, string? password = null)
    {
        using var doc = Open(input, PdfDocumentOpenMode.Modify, password);
        if (order.Count != doc.PageCount || order.Distinct().Count() != doc.PageCount || order.Any(p => p < 0 || p >= doc.PageCount))
            throw new ArgumentException("The new order must contain every page exactly once.");
        var pages = order.Select(i => doc.Pages[i]).ToList();
        // move each wanted page to its final position, front to back
        for (int target = 0; target < pages.Count; target++)
        {
            int current = IndexOf(doc, pages[target]);
            if (current != target) doc.Pages.MovePage(current, target);
        }
        SaveNew(doc, output);
    }

    private static int IndexOf(PdfDocument doc, PdfPage page)
    {
        for (int i = 0; i < doc.PageCount; i++) if (ReferenceEquals(doc.Pages[i], page)) return i;
        throw new InvalidOperationException("Page not found.");
    }

    /// <summary>Rotates the given pages clockwise by a multiple of 90°.</summary>
    public static void RotatePages(string input, IReadOnlyCollection<int> pages, int degrees, string output, string? password = null)
    {
        if (degrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.");
        using var doc = Open(input, PdfDocumentOpenMode.Modify, password);
        foreach (var p in pages)
        {
            var page = doc.Pages[p];
            page.Rotate = (((page.Rotate + degrees) % 360) + 360) % 360;
        }
        SaveNew(doc, output);
    }

    /// <summary>
    /// Writes a new PDF with the given pages (zero-based, each at most once) in this order, each rotated by an extra
    /// multiple of 90°. Pages not listed are left out. Bookmarks and document metadata are kept where their pages remain.
    /// </summary>
    public static void Compose(string input, IReadOnlyList<(int page, int rotate)> pages, string output, string? password = null)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF needs at least one page.");
        if (pages.Select(p => p.page).Distinct().Count() != pages.Count) throw new ArgumentException("Each page can appear only once.");
        using var doc = Open(input, PdfDocumentOpenMode.Modify, password);
        if (pages.Any(p => p.page < 0 || p.page >= doc.PageCount)) throw new ArgumentOutOfRangeException(nameof(pages), "A page doesn't exist.");
        if (pages.Any(p => p.rotate % 90 != 0)) throw new ArgumentException("Rotation must be a multiple of 90 degrees.");
        var wanted = pages.Select(p => (page: doc.Pages[p.page], p.rotate)).ToList();
        var keep = new HashSet<PdfPage>(wanted.Select(w => w.page));
        foreach (var page in doc.Pages.Cast<PdfPage>().ToList())
            if (!keep.Contains(page)) doc.Pages.Remove(page);
        for (int target = 0; target < wanted.Count; target++)
        {
            int current = IndexOf(doc, wanted[target].page);
            if (current != target) doc.Pages.MovePage(current, target);
            var pg = wanted[target].page;
            pg.Rotate = (((pg.Rotate + wanted[target].rotate) % 360) + 360) % 360;
        }
        SaveNew(doc, output);
    }

    /// <summary>Removes pages (zero-based) and writes the rest to a new file.</summary>
    public static void DeletePages(string input, IReadOnlyCollection<int> pages, string output, string? password = null)
    {
        int count;
        using (var probe = Open(input, PdfDocumentOpenMode.Import, password)) count = probe.PageCount;
        var keep = Enumerable.Range(0, count).Where(p => !pages.Contains(p)).ToList();
        if (keep.Count == 0) throw new ArgumentException("A PDF needs at least one page.");
        ExtractPages(input, keep, output, password);
    }

    /// <summary>Images (any format Windows can decode) as pages of a new PDF, one image per page sized to the image.</summary>
    public static void ImagesToPdf(IReadOnlyList<string> images, string output, CancellationToken ct = default)
    {
        using var doc = new PdfDocument();
        foreach (var img in images)
        {
            ct.ThrowIfCancellationRequested();
            var bmp = Imaging.ImageService.Load(img);
            var png = Imaging.ImageService.EncodePng(bmp);
            using var ms = new MemoryStream(png);
            using var x = PdfSharp.Drawing.XImage.FromStream(ms);
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(x.PointWidth);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(x.PointHeight);
            using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            g.DrawImage(x, 0, 0, x.PointWidth, x.PointHeight);
        }
        SaveNew(doc, output);
    }

    // ------------------------------------------------------------------ rendering (Windows' built-in PDF engine)

    /// <summary>Renders one page (zero-based) to a bitmap <paramref name="width"/> pixels wide. Must not run on the UI thread.</summary>
    public static BitmapSource RenderPage(string path, int page, int width, string? password = null)
    {
        var doc = password == null ? Cached(path) : Load(path, password);
        lock (doc)
        {
            using var p = doc.GetPage((uint)page);
            return Render(p, width);
        }
    }

    /// <summary>Each page's size in DIPs (1/96 inch), for laying the pages out before they are drawn. Must not run on the UI thread.</summary>
    public static IReadOnlyList<(double Width, double Height)> PageSizes(string path)
    {
        var doc = Cached(path);
        lock (doc)
        {
            var sizes = new List<(double, double)>();
            for (uint i = 0; i < doc.PageCount; i++)
            {
                using var p = doc.GetPage(i);
                sizes.Add((p.Size.Width, p.Size.Height));
            }
            return sizes;
        }
    }

    private static Windows.Data.Pdf.PdfDocument Load(string path, string? password)
    {
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
        return (password == null
            ? Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file)
            : Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file, password)).AsTask().GetAwaiter().GetResult();
    }

    // the last few documents opened without a password (Compare draws two PDFs page by page). They are loaded from a copy
    // in memory, so the files themselves stay free to rename or delete.
    private static readonly List<(string Key, Windows.Data.Pdf.PdfDocument Doc)> Recent = new();
    private const long MaxCachedBytes = 100L * 1024 * 1024;

    private static Windows.Data.Pdf.PdfDocument Cached(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (info.Length > MaxCachedBytes) return Load(full, null);
        var key = full + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
        lock (Recent)
        {
            int i = Recent.FindIndex(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return Recent[i].Doc;
        }
        var bytes = File.ReadAllBytes(full);
        var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var writer = new Windows.Storage.Streams.DataWriter(ras);
        writer.WriteBytes(bytes);
        writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        writer.DetachStream();
        ras.Seek(0);
        var doc = Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(ras).AsTask().GetAwaiter().GetResult();
        lock (Recent)
        {
            Recent.Insert(0, (key, doc));
            if (Recent.Count > 4) Recent.RemoveAt(Recent.Count - 1);
        }
        return doc;
    }

    private static BitmapSource Render(Windows.Data.Pdf.PdfPage p, int width)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        p.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(16, width) }).AsTask().GetAwaiter().GetResult();
        using var net = stream.AsStreamForRead();
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = net;
        bmp.EndInit();
        bmp.Freeze();
        // the renderer applies the display scale to DestinationWidth; return exactly the width asked for
        if (bmp.PixelWidth == width) return bmp;
        return Imaging.ImageService.Resize(bmp, width, (int)Math.Round((double)bmp.PixelHeight * width / bmp.PixelWidth));
    }

    public static void RenderPageToPng(string path, int page, int width, string output, string? password = null)
    {
        var bmp = RenderPage(path, page, width, password);
        Imaging.ImageService.Save(bmp, output, Imaging.ImageFormat.Png);
    }
}
