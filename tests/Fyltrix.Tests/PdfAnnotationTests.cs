using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fyltrix.Core.Pdf;
using Fyltrix.UI;
using PdfSharp.Pdf;

namespace Fyltrix.Tests;

/// <summary>Annotation identity, geometry, reply threading and the comment card (docs/PDF_ANNOTATION_INTERACTION_INVESTIGATION.md).</summary>
[Collection("UI")]
public class PdfAnnotationTests
{
    private static PdfDictionary Annot(PdfDocument doc, string subtype, string? text = null, string? author = null, string? nm = null,
        double[]? rect = null, double[]? quads = null, PdfDictionary? irt = null, bool group = false, string? created = null)
    {
        var a = new PdfDictionary(doc);
        a.Elements.SetName("/Type", "/Annot");
        a.Elements.SetName("/Subtype", "/" + subtype);
        if (text != null) a.Elements.SetString("/Contents", text);
        if (author != null) a.Elements.SetString("/T", author);
        if (nm != null) a.Elements.SetString("/NM", nm);
        if (created != null) a.Elements.SetString("/CreationDate", created);
        var r = new PdfArray(doc); foreach (var v in rect ?? new double[] { 100, 100, 120, 120 }) r.Elements.Add(new PdfReal(v));
        a.Elements["/Rect"] = r;
        if (quads != null) { var q = new PdfArray(doc); foreach (var v in quads) q.Elements.Add(new PdfReal(v)); a.Elements["/QuadPoints"] = q; }
        doc.Internals.AddObject(a);
        if (irt != null) a.Elements["/IRT"] = irt.Reference;
        if (group) a.Elements.SetName("/RT", "/Group");
        return a;
    }

    private static void Place(PdfDocument doc, int page, params PdfDictionary[] annots)
    {
        var array = new PdfArray(doc);
        foreach (var a in annots) array.Elements.Add(a.Reference!);
        doc.Pages[page - 1].Elements["/Annots"] = array;
    }

    /// <summary>Like the owner's real review PDF: a replace-text edit (caret + grouped strikeout) answered by "done" replies.</summary>
    internal static string ReviewPdf(TempDir temp, string name = "review.pdf")
    {
        using var doc = new PdfDocument();
        doc.AddPage(); doc.AddPage();
        var caret = Annot(doc, "Caret", "new words", "Reviewer", nm: "edit-1", rect: new double[] { 200, 690, 212, 700 });
        var strike = Annot(doc, "StrikeOut", author: "Reviewer", rect: new double[] { 150, 688, 210, 702 },
            quads: new double[] { 150, 702, 210, 702, 150, 688, 210, 688 }, irt: caret, group: true);
        var reply2 = Annot(doc, "Text", "done later", "Editor", nm: "r2", irt: caret, created: "D:20261102100000");
        var reply1 = Annot(doc, "Text", "done", "Editor", nm: "r1", irt: caret, created: "D:20261101100000");
        var replyToReply = Annot(doc, "Text", "thanks", "Reviewer", nm: "r3", irt: reply1, created: "D:20261103100000");
        var note = Annot(doc, "Text", "Standalone note", "Reviewer", nm: "edit-1"); // duplicate /NM on purpose
        var notOnAnyPage = Annot(doc, "Text", "missing parent");
        var orphan = Annot(doc, "Text", "orphan reply", irt: notOnAnyPage);
        var loopA = Annot(doc, "Text", "loop a"); var loopB = Annot(doc, "Text", "loop b", irt: loopA);
        loopA.Elements["/IRT"] = loopB.Reference;
        var link = Annot(doc, "Link");
        var emptyHighlight = Annot(doc, "Highlight", quads: new double[] { 1, 2, 3, 4, 5, 6, 7 }); // malformed quads, no text, no replies
        Place(doc, 1, caret, strike, reply2, reply1, replyToReply, note, orphan, link, emptyHighlight);
        Place(doc, 2, loopA, loopB);
        string path = temp[name];
        doc.Save(path);
        return path;
    }

    [Fact]
    public void Replies_and_replace_text_groups_are_threaded_without_modifying_the_pdf()
    {
        using var temp = new TempDir();
        var path = ReviewPdf(temp);
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var result = PdfService.ReadComments(path);

        var edit = result.Items[0];
        Assert.Equal("Replace text", edit.Type);
        Assert.Equal("new words", edit.Text);
        Assert.Equal("edit-1", edit.Id);
        Assert.Equal(new PdfRect(150, 688, 210, 702), Assert.Single(edit.Quads)); // the struck-out words, from the grouped strikeout
        Assert.Equal(new PdfRect(150, 688, 210, 702), edit.Target);
        Assert.Equal(new PdfRect(200, 690, 212, 700), edit.Rect);
        // replies (including a reply to a reply) are under the edit, oldest first, and never listed on their own
        Assert.Equal(new[] { "done", "done later", "thanks" }, edit.Replies.Select(r => r.Text));
        Assert.All(edit.Replies, r => Assert.Equal("Reply", r.Type));
        Assert.Contains("3 replies", edit.Heading);
        Assert.Contains("↳ Editor: done", edit.RepliesSummary);
        Assert.DoesNotContain(result.Items, c => c.Text is "done" or "done later" or "thanks");

        Assert.Equal("Standalone note", result.Items[1].Text);
        Assert.Equal("edit-1#2", result.Items[1].Id); // duplicate /NM stays unique
        Assert.Contains(result.Items, c => c.Text == "orphan reply"); // its parent isn't on a page: kept as its own comment
        Assert.Equal(2, result.Items.Count(c => c.Text is "loop a" or "loop b")); // cyclic /IRT: both kept, nothing lost
        Assert.DoesNotContain(result.Items, c => c.Text.Length == 0 && c.Replies.Count == 0);
        Assert.True(result.Partial); // the malformed QuadPoints were reported
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Identity_falls_back_to_the_object_reference_and_rectangles_are_normalized_or_rejected()
    {
        using var temp = new TempDir();
        using (var doc = new PdfDocument())
        {
            doc.AddPage();
            Place(doc, 1,
                Annot(doc, "Text", "inverted", rect: new double[] { 300, 500, 280, 480 }),
                Annot(doc, "Text", "zero area", rect: new double[] { 10, 10, 10, 30 }),
                Annot(doc, "Text", "absurd", rect: new double[] { 0, 0, 1e9, 10 }));
            doc.Save(temp["ids.pdf"]);
        }
        var items = PdfService.ReadComments(temp["ids.pdf"]).Items;
        Assert.All(items, c => Assert.StartsWith("obj:", c.Id));
        Assert.Equal(3, items.Select(c => c.Id).Distinct().Count());
        Assert.Equal(new PdfRect(280, 480, 300, 500), items[0].Rect);
        Assert.Null(items[1].Rect);
        Assert.Null(items[2].Rect);
        Assert.Null(items[1].Target);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(90, 90)] [InlineData(180, 180)] [InlineData(270, 270)]
    [InlineData(-90, 270)] [InlineData(450, 90)] [InlineData(45, 0)]
    public void Page_geometry_uses_the_visible_crop_box_and_normalized_rotation(int rotate, int expected)
    {
        using var temp = new TempDir();
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            page.Elements["/MediaBox"] = new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, 612, 792));
            var crop = new PdfArray(doc); foreach (var v in new double[] { 50, -20, 562, 742 }) crop.Elements.Add(new PdfReal(v));
            page.Elements["/CropBox"] = crop; // extends below the media box: clipped to it
            page.Elements.SetInteger("/Rotate", rotate);
            Place(doc, 1, Annot(doc, "Text", "note"));
            doc.Save(temp["geo.pdf"]);
        }
        var geometry = Assert.Single(PdfService.ReadComments(temp["geo.pdf"]).Pages);
        Assert.Equal(new PdfRect(50, 0, 562, 742), geometry.Box);
        Assert.Equal(expected, geometry.Rotate);
        Assert.Equal(expected is 90 or 270 ? 742 : 512, geometry.DisplayWidth);
    }

    [Fact]
    public void Mapping_matches_the_native_viewer_measurements_for_every_rotation()
    {
        // Measured in the investigation: a 612×792 page shown 667 px wide; highlight /Rect [72 695 150 722] at x+78.5, y+76.3.
        var page = new PdfPageGeometry(new PdfRect(0, 0, 612, 792), 0);
        var shown = new Rect(-31891, -31922, 667, 667 * 792 / 612.0);
        var r = PdfAnnotationGeometry.ToDisplay(page, new PdfRect(72, 695, 150, 722), shown);
        Assert.InRange(r.X, -31813.5, -31811.5); Assert.InRange(r.Y, -31846.7, -31844.7); Assert.InRange(r.Width, 84, 86);

        // Rotated 90°: "GAMMA" baseline origin (72,700) appears at u = 700/792, v = 72/612 of the landscape page.
        var rotated = new PdfPageGeometry(new PdfRect(0, 0, 612, 792), 90);
        var (u, v) = PdfAnnotationGeometry.ToPageFraction(rotated, 72, 700);
        Assert.Equal(700 / 792.0, u, 6); Assert.Equal(72 / 612.0, v, 6);
        // Cropped page: x is measured from the crop box's left edge.
        var cropped = new PdfPageGeometry(new PdfRect(50, 50, 562, 742), 0);
        Assert.Equal(50 / 512.0, PdfAnnotationGeometry.ToPageFraction(cropped, 100, 600).U, 6);

        // Every rotation maps the four corners of the page box onto the four display corners.
        foreach (int rot in new[] { 0, 90, 180, 270 })
        {
            var g = new PdfPageGeometry(new PdfRect(10, 20, 110, 220), rot);
            var f = PdfAnnotationGeometry.ToPageFraction(g, g.Box);
            Assert.Equal((0d, 0d, 1d, 1d), (Math.Round(f.Left, 9), Math.Round(f.Top, 9), Math.Round(f.Right, 9), Math.Round(f.Bottom, 9)));
        }
        // 270°: the page's left edge (x = 0) is shown at the bottom.
        Assert.Equal((0.9, 1.0), Round(PdfAnnotationGeometry.ToPageFraction(new PdfPageGeometry(new PdfRect(0, 0, 100, 200), 270), 0, 20)));
        Assert.True(PdfAnnotationGeometry.MatchesAspect(rotated, 1056, 816));
        Assert.False(PdfAnnotationGeometry.MatchesAspect(rotated, 816, 1056));
        Assert.False(PdfAnnotationGeometry.MatchesAspect(page, 0, 100));
    }

    private static (double, double) Round((double U, double V) p) => (Math.Round(p.U, 9), Math.Round(p.V, 9));

    [Fact]
    public void Comment_card_shows_text_replies_and_document_and_closes_with_escape_or_close()
    {
        using var temp = new TempDir();
        var path = ReviewPdf(temp);
        var edit = PdfService.ReadComments(path).Items[0];
        Sta.Run(() =>
        {
            var viewer = new Grid { Width = 800, Height = 600 };
            var w = new Window { Width = 820, Height = 640, Content = viewer };
            Sta.ShowOffscreen(w);
            var card = new PdfAnnotationPopup(viewer);
            try
            {
                card.Show(edit, path, locationNote: "page only");
                Assert.True(card.Popup.IsOpen);
                Assert.Same(edit, card.Comment);
                Assert.Equal(path, card.DocumentPath);
                Assert.Equal("page only", card.LocationNote);
                Assert.Equal(new[] { "done", "done later", "thanks" }, card.ReplyTexts);
                var texts = VisualTree.FindDescendants<TextBox>(card.Popup.Child).Select(t => t.Text).ToList();
                Assert.Contains("new words", texts);
                Assert.All(VisualTree.FindDescendants<TextBox>(card.Popup.Child), t => Assert.True(t.IsReadOnly)); // selectable, never editable
                Assert.Contains(VisualTree.FindDescendants<TextBlock>(card.Popup.Child), t => t.Text == "review.pdf");
                Assert.False(w.IsActive && !card.Popup.IsOpen);

                var source = PresentationSource.FromVisual(card.Popup.Child)!;
                card.Popup.Child.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.False(card.Popup.IsOpen);
                Assert.Null(card.Comment);

                card.Show(edit, path, target: new Rect(100, 200, 60, 14));
                Assert.Equal(164, card.Popup.HorizontalOffset); // beside (right of) the comment
                Assert.Equal(188, card.Popup.VerticalOffset);
                var close = VisualTree.FindDescendants<Button>(card.Popup.Child).First();
                close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.False(card.Popup.IsOpen);

                // A click elsewhere in the Fyltrix window closes it; the card doesn't close on its own otherwise.
                card.Show(edit, path);
                Sta.DoEvents(200);
                Assert.True(card.Popup.IsOpen);
                viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                Assert.False(card.Popup.IsOpen);
            }
            finally { card.Dispose(); w.Close(); }
        });
    }
}
