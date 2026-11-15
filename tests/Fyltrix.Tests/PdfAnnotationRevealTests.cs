using System.Text;
using System.Windows;
using Fyltrix.Core;
using Fyltrix.Core.Pdf;
using Fyltrix.Preview;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>
/// Revealing a comment at its exact position in the native WebView2 PDF viewer (public UI Automation only), with honest
/// fallbacks. Off-screen windows and an isolated WebView2 profile; nothing is shown on the desktop.
/// </summary>
[Collection("UI")]
public class PdfAnnotationRevealTests
{
    /// <summary>4 pages (612×792): text and annotations at known positions, a rotated page and a CropBox offset.</summary>
    internal static string GeometryPdf(TempDir temp, string name = "geometry.pdf")
    {
        var objects = new List<string> { "", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var pages = new List<(string content, string extra, string[] annots)>
        {
            ("BT /F1 24 Tf 72 700 Td (ALPHA) Tj ET", "", new[] {
                "<< /Type /Annot /Subtype /Text /Rect [300 650 320 670] /Contents (note one) /T (probe) /NM (note-1) >>",
                "<< /Type /Annot /Subtype /Highlight /Rect [72 695 150 722] /QuadPoints [72 722 150 722 72 695 150 695] /C [1 1 0] /Contents (highlight one) /T (probe) /NM (hl-1) >>" }),
            ("BT /F1 24 Tf 72 700 Td (BETA) Tj ET", "", new[] {
                "<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /Contents (bottom note) /T (probe) /NM (note-bottom) >>",
                "<< /Type /Annot /Subtype /Text /Rect [400 700 420 720] /Contents (top note) /T (probe) /NM (note-top) >>" }),
            ("BT /F1 24 Tf 72 700 Td (GAMMA) Tj ET", "/Rotate 90", new[] {
                "<< /Type /Annot /Subtype /Text /Rect [300 400 320 420] /Contents (rotated note) /T (probe) /NM (note-rot) >>" }),
            ("BT /F1 24 Tf 100 600 Td (DELTA) Tj ET", "/CropBox [50 50 562 742]", new[] {
                "<< /Type /Annot /Subtype /Text /Rect [200 500 220 520] /Contents (cropped note) /T (probe) /NM (note-crop) >>" }),
        };
        var kids = new List<string>();
        foreach (var (content, extra, annots) in pages)
        {
            int c = objects.Count + 1;
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
            var refs = new List<string>();
            foreach (var a in annots) { objects.Add(a); refs.Add($"{objects.Count} 0 R"); }
            kids.Add($"{objects.Count + 1} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {extra} /Resources << /Font << /F1 3 0 R >> >> /Contents {c} 0 R /Annots [{string.Join(" ", refs)}] >>");
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R >>";
        objects[1] = $"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(" ", kids)}] >>";
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append($"{o:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        string path = temp[name];
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    private static T Await<T>(Task<T> task, string what, int timeoutMs = 30000)
    {
        Sta.WaitUntil(() => task.IsCompleted, what, timeoutMs);
        return task.GetAwaiter().GetResult();
    }

    private static (Window, PreviewPane) PdfHost(string pdf)
    {
        PreviewPane.Settings ??= new AppSettings();
        var pane = new PreviewPane(showHeader: true, showPopOut: false);
        var w = new Window { Width = 900, Height = 1000, Content = pane };
        Sta.ShowOffscreen(w);
        Sta.WaitUntil(() => pane.IsReady, "WebView2 ready", 30000);
        pane.Show(pdf, delayMs: 0);
        Sta.WaitUntil(() => pane.ShowsNativePdf && pane.Web.CoreWebView2.Source == PreviewUrls.FileUrl(pdf), "native PDF shown", 30000);
        return (w, pane);
    }

    [Fact]
    public void Every_comment_is_scrolled_into_view_and_the_unzoomed_page_position_is_exact()
    {
        using var t = new TempDir();
        var pdf = GeometryPdf(t);
        var comments = PdfService.ReadComments(pdf);
        Assert.Equal(6, comments.Items.Count);
        Sta.Run(() =>
        {
            var (w, pane) = PdfHost(pdf);
            try
            {
                // Order: later pages first, then back to the top, so every reveal really scrolls.
                foreach (var id in new[] { "note-crop", "note-rot", "note-bottom", "note-top", "hl-1", "note-1" })
                {
                    var c = comments.Items.Single(x => x.Id == id);
                    var r = Await(NativePdfLocator.RevealAsync(pane.Web.Handle, c.Page, c.Target, comments.Pages, CancellationToken.None), "reveal " + id);
                    Assert.True(r.Level >= RevealLevel.Vertical, $"{id}: {r.Level} ({r.Detail})");
                    var target = r.Target!.Value;
                    double centre = target.Top + target.Height / 2;
                    Assert.InRange(centre, r.Viewport.Top, r.Viewport.Bottom);
                    // In the unzoomed, fitted layout the page is centred, so the horizontal position is confirmed too.
                    Assert.Equal(RevealLevel.Exact, r.Level);
                    // The size on screen follows the page scale (about 1.09 px per point here).
                    Assert.InRange(target.Width / c.Target!.Value.Width, 1.0, 1.25);
                }
                // The highlight's outline lands on its text (as measured in the investigation): "ALPHA" starts ~78 px into the page.
                var hl = comments.Items.Single(x => x.Id == "hl-1");
                var exact = Await(NativePdfLocator.RevealAsync(pane.Web.Handle, 1, hl.Target, comments.Pages, CancellationToken.None), "highlight");
                Assert.InRange(exact.Target!.Value.Left - exact.Viewport.Left, 90, 200);
            }
            finally { pane.DisposeWeb(); w.Close(); }
        });
    }

    [Fact]
    public void Wrong_page_data_falls_back_instead_of_guessing_and_cancellation_stops()
    {
        using var t = new TempDir();
        var pdf = GeometryPdf(t);
        var comments = PdfService.ReadComments(pdf);
        Sta.Run(() =>
        {
            var (w, pane) = PdfHost(pdf);
            var saved = NativePdfLocator.LayoutWait;
            try
            {
                var c = comments.Items.Single(x => x.Id == "note-bottom");
                // Make sure the viewer's layout is available first (so a fallback below is due to the data, not loading).
                Assert.True(Await(NativePdfLocator.RevealAsync(pane.Web.Handle, c.Page, c.Target, comments.Pages, CancellationToken.None), "warm-up").Level >= RevealLevel.Vertical);
                NativePdfLocator.LayoutWait = TimeSpan.FromMilliseconds(500);
                // Pages that don't match the document (count/proportions): nothing is located, no position is invented.
                var wrong = comments.Pages.Select(p => p with { Box = new PdfRect(0, 0, 300, 900) }).ToList();
                var r = Await(NativePdfLocator.RevealAsync(pane.Web.Handle, c.Page, c.Target, wrong, CancellationToken.None), "mismatch");
                Assert.Equal(RevealLevel.Unavailable, r.Level);
                Assert.Null(r.Target);
                // No annotation geometry: page level only.
                var noGeometry = Await(NativePdfLocator.RevealAsync(pane.Web.Handle, c.Page, null, comments.Pages, CancellationToken.None), "no geometry");
                Assert.Equal(RevealLevel.Page, noGeometry.Level);
                // Cancelled before it runs: stops without an error.
                using var cts = new CancellationTokenSource(); cts.Cancel();
                var cancelled = Await(NativePdfLocator.RevealAsync(pane.Web.Handle, c.Page, c.Target, comments.Pages, cts.Token), "cancelled");
                Assert.Equal(RevealLevel.Unavailable, cancelled.Level);
                Assert.Equal("cancelled", cancelled.Detail);
            }
            finally { NativePdfLocator.LayoutWait = saved; pane.DisposeWeb(); w.Close(); }
        });
    }

    [Fact]
    public void Preview_card_is_placed_by_the_comment_with_a_temporary_outline_and_a_newer_choice_wins()
    {
        using var t = new TempDir();
        var pdf = GeometryPdf(t);
        var other = t.File("other.txt", "x");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => s.PreviewVisible = true);
            var saved = PdfAnnotationHighlight.Duration;
            try
            {
                PdfAnnotationHighlight.Duration = TimeSpan.FromMilliseconds(800);
                var flyout = w.PreviewCommentsForTest;
                var preview = w.PreviewForTest;
                preview.Show(pdf, delayMs: 0);
                Sta.WaitUntil(() => flyout.Panel.HasResult && preview.ShowsNativePdf, "comments and native PDF", 30000);
                var bottom = flyout.Panel.Comments.Single(c => c.Id == "note-bottom");
                var top = flyout.Panel.Comments.Single(c => c.Id == "note-top");

                // Two quick choices: only the newer one is revealed and shown.
                flyout.Panel.Request(top);
                flyout.Panel.Request(bottom);
                Assert.Equal("Finding the comment…", flyout.Card.LocationNote);
                Sta.WaitUntil(() => flyout.RevealTask.IsCompleted && flyout.LastReveal != null, "reveal", 30000);
                Assert.Same(bottom, flyout.Card.Comment);
                Assert.True(flyout.LastReveal!.Level == RevealLevel.Exact, flyout.LastReveal.Detail);
                Assert.Equal("", flyout.Card.LocationNote);          // exact: nothing to qualify
                Assert.True(flyout.Highlight.IsShown);
                var area = flyout.Highlight.Area;
                Assert.InRange(area.Top + area.Height / 2, 0, preview.ActualHeight);
                Assert.InRange(area.Left, 0, preview.ActualWidth);
                // The card is beside the outline (to its right, or to its left when there is no room).
                double cardLeft = flyout.Card.Popup.HorizontalOffset;
                Assert.True(cardLeft >= area.Right - 4 || cardLeft + PdfAnnotationPopup.CardWidth <= area.Left + 4, $"card {cardLeft} vs outline {area}");
                Sta.WaitUntil(() => !flyout.Highlight.IsShown, "outline removed after a moment", 5000);

                // Another file: card, outline and any pending reveal are gone.
                flyout.Panel.Request(top);
                preview.Show(other, delayMs: 0);
                Assert.False(flyout.Card.Popup.IsOpen);
                Assert.False(flyout.Highlight.IsShown);
            }
            finally { PdfAnnotationHighlight.Duration = saved; w.Close(); }
        });
    }

    [Fact]
    public void Compare_reveals_on_the_chosen_side_only()
    {
        using var t = new TempDir();
        var left = GeometryPdf(t, "left.pdf");
        var right = GeometryPdf(t, "right.pdf");
        Sta.Run(() =>
        {
            var owner = new Window(); Sta.ShowOffscreen(owner);
            var compare = new CompareWindow(owner, left, right) { Width = 1400, Height = 900 };
            try
            {
                Sta.ShowOffscreen(compare);
                Sta.WaitUntil(() => compare.LeftComments.HasResult && compare.RightComments.HasResult && compare.RightPane.ShowsNativePdf, "both sides", 30000);
                var c = compare.RightComments.Comments.Single(x => x.Id == "note-crop");
                compare.RightComments.Request(c);
                Sta.WaitUntil(() => compare.RightCommentsFlyout.RevealTask.IsCompleted && compare.RightCommentsFlyout.LastReveal != null, "right reveal", 30000);
                Assert.True(compare.RightCommentsFlyout.LastReveal!.Level >= RevealLevel.Vertical, compare.RightCommentsFlyout.LastReveal.Detail);
                Assert.Equal(right, compare.RightCommentsFlyout.Card.DocumentPath);
                Assert.False(compare.LeftCommentsFlyout.Card.Popup.IsOpen);   // never a card or outline on the other document
                Assert.False(compare.LeftCommentsFlyout.Highlight.IsShown);
            }
            finally { compare.Close(); owner.Close(); }
        });
    }
}
