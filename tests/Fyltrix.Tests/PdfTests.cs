using Fyltrix.Core.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Fyltrix.Tests;

public class PdfTests
{
    /// <summary>A PDF whose page i is (100 + i) points wide, so order is checkable without text.</summary>
    private static string MakePdf(string path, int pages, string? userPassword = null, string title = "Test doc")
    {
        using var doc = new PdfDocument();
        doc.Info.Title = title;
        for (int i = 0; i < pages; i++)
        {
            var p = doc.AddPage();
            p.Width = PdfSharp.Drawing.XUnit.FromPoint(100 + i);
            p.Height = PdfSharp.Drawing.XUnit.FromPoint(200);
        }
        if (userPassword != null)
        {
            doc.SecuritySettings.UserPassword = userPassword;
            doc.SecuritySettings.OwnerPassword = userPassword + "-owner";
        }
        doc.Save(path);
        return path;
    }

    private static int[] Widths(string path, string? password = null)
    {
        using var d = password == null ? PdfReader.Open(path, PdfDocumentOpenMode.Import) : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
        return d.Pages.Cast<PdfPage>().Select(p => (int)Math.Round(p.Width.Point) - 100).ToArray();
    }

    [Theory]
    [InlineData("1-3", 10, new[] { 0, 1, 2 })]
    [InlineData("5, 2", 10, new[] { 4, 1 })]
    [InlineData("8-", 10, new[] { 7, 8, 9 })]
    [InlineData("-2", 10, new[] { 0, 1 })]
    [InlineData("3-1", 10, new[] { 2, 1, 0 })]
    public void Page_ranges(string spec, int count, int[] expected) => Assert.Equal(expected, PdfService.ParsePages(spec, count));

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("x")]
    [InlineData("")]
    public void Bad_page_ranges(string spec) => Assert.Throws<FormatException>(() => PdfService.ParsePages(spec, 10));

    [Fact]
    public void Info_merge_extract_split()
    {
        using var t = new TempDir();
        var a = MakePdf(t["a.pdf"], 3);
        var b = MakePdf(t["b.pdf"], 2);
        var info = PdfService.Info(a);
        Assert.Equal(3, info.Pages);
        Assert.False(info.Encrypted);
        Assert.False(info.Signed);
        Assert.Contains(info.Metadata, m => m.Label == "Title" && m.Value == "Test doc");

        PdfService.Merge(new[] { a, b }, t["merged.pdf"]);
        Assert.Equal(new[] { 0, 1, 2, 0, 1 }, Widths(t["merged.pdf"]));
        Assert.Throws<IOException>(() => PdfService.Merge(new[] { a, b }, t["merged.pdf"])); // never overwrites

        PdfService.ExtractPages(a, new[] { 2, 0 }, t["extract.pdf"]);
        Assert.Equal(new[] { 2, 0 }, Widths(t["extract.pdf"]));

        var parts = PdfService.Split(t["merged.pdf"], 2, t.Dir("split"));
        Assert.Equal(3, parts.Count);
        Assert.EndsWith("merged (pages 1-2).pdf", parts[0]);
        Assert.EndsWith("merged (page 5).pdf", parts[2]);
        Assert.Equal(new[] { 1 }, Widths(parts[2]));
        Assert.Empty(Directory.GetFiles(t.Path, "*.fl-partial", SearchOption.AllDirectories));
    }

    [Fact]
    public void Reorder_rotate_delete_keep_source_untouched()
    {
        using var t = new TempDir();
        var src = MakePdf(t["s.pdf"], 4);
        var before = File.ReadAllBytes(src);
        PdfService.Reorder(src, new[] { 3, 1, 0, 2 }, t["r.pdf"]);
        Assert.Equal(new[] { 3, 1, 0, 2 }, Widths(t["r.pdf"]));
        using (var d = PdfReader.Open(t["r.pdf"], PdfDocumentOpenMode.Import)) Assert.Equal("Test doc", d.Info.Title); // metadata kept
        Assert.Throws<ArgumentException>(() => PdfService.Reorder(src, new[] { 0, 0, 1, 2 }, t["bad.pdf"]));

        PdfService.RotatePages(src, new[] { 1, 3 }, 90, t["rot.pdf"]);
        PdfService.RotatePages(t["rot.pdf"], new[] { 1 }, -180, t["rot2.pdf"]);
        using (var d = PdfReader.Open(t["rot2.pdf"], PdfDocumentOpenMode.Import))
            Assert.Equal(new[] { 0, 270, 0, 90 }, d.Pages.Cast<PdfPage>().Select(p => p.Rotate).ToArray());

        PdfService.DeletePages(src, new[] { 0, 2 }, t["del.pdf"]);
        Assert.Equal(new[] { 1, 3 }, Widths(t["del.pdf"]));
        Assert.Throws<ArgumentException>(() => PdfService.DeletePages(src, new[] { 0, 1, 2, 3 }, t["none.pdf"]));
        Assert.Equal(before, File.ReadAllBytes(src));
    }

    [Fact]
    public void Compose_reorders_rotates_drops_and_keeps_bookmarks()
    {
        using var t = new TempDir();
        using (var doc = new PdfDocument())
        {
            doc.Info.Title = "With outline";
            for (int i = 0; i < 4; i++) { var p = doc.AddPage(); p.Width = PdfSharp.Drawing.XUnit.FromPoint(100 + i); }
            doc.Outlines.Add("Chapter 1", doc.Pages[0]);
            doc.Outlines.Add("Chapter 3", doc.Pages[2]);
            doc.Save(t["o.pdf"]);
        }
        PdfService.Compose(t["o.pdf"], new[] { (2, 90), (0, 0), (3, 270) }, t["c.pdf"]);
        Assert.Equal(new[] { 2, 0, 3 }, Widths(t["c.pdf"]));
        using var d = PdfReader.Open(t["c.pdf"], PdfDocumentOpenMode.Import);
        Assert.Equal(new[] { 90, 0, 270 }, d.Pages.Cast<PdfPage>().Select(p => p.Rotate).ToArray());
        Assert.Equal("With outline", d.Info.Title);
        Assert.Throws<ArgumentException>(() => PdfService.Compose(t["o.pdf"], new[] { (1, 0), (1, 0) }, t["x.pdf"]));
    }

    [Fact]
    public void Encrypted_pdfs_need_the_password()
    {
        using var t = new TempDir();
        var enc = MakePdf(t["e.pdf"], 2, "secret");
        var none = Assert.Throws<PdfPasswordException>(() => PdfService.Info(enc));
        Assert.False(none.WasWrong);
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfService.Info(enc, "nope")).WasWrong);
        var info = PdfService.Info(enc, "secret");
        Assert.True(info.Encrypted);
        PdfService.ExtractPages(enc, new[] { 1 }, t["out.pdf"], "secret");
        Assert.Equal(new[] { 1 }, Widths(t["out.pdf"]));
    }

    [Fact]
    public void Signature_detection()
    {
        using var t = new TempDir();
        var plain = MakePdf(t["p.pdf"], 1);
        Assert.False(PdfService.IsSigned(plain));
        File.WriteAllBytes(t["signed.pdf"], File.ReadAllBytes(plain).Concat(System.Text.Encoding.ASCII.GetBytes("\n% /Type /Sig /ByteRange [0 1 2 3]\n")).ToArray());
        Assert.True(PdfService.IsSigned(t["signed.pdf"]));
    }

    [Fact]
    public void Render_pages_and_images_to_pdf()
    {
        using var t = new TempDir();
        var src = MakePdf(t["r.pdf"], 2);
        Sta.Run(() =>
        {
            var bmp = PdfService.RenderPage(src, 1, 202);
            Assert.Equal(202, bmp.PixelWidth);
            Assert.InRange(bmp.PixelHeight, 395, 405); // 101 x 200 pt page at 202 px wide
            MediaFixtures.Png(t["i1.png"], MediaFixtures.Marker(300, 150));
            MediaFixtures.Png(t["i2.png"], MediaFixtures.Marker(100, 200));
        });
        PdfService.ImagesToPdf(new[] { t["i1.png"], t["i2.png"] }, t["imgs.pdf"]);
        using var d = PdfReader.Open(t["imgs.pdf"], PdfDocumentOpenMode.Import);
        Assert.Equal(2, d.PageCount);
        Assert.True(d.Pages[0].Width.Point > d.Pages[0].Height.Point);
        Assert.True(d.Pages[1].Width.Point < d.Pages[1].Height.Point);
    }
}
