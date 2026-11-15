using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fyltrix.UI;
using PdfSharp.Pdf;

namespace Fyltrix.Tests;

/// <summary>The Comments list keeps its place: after Show, reopening it returns to where the user was.</summary>
[Collection("UI")]
public class PdfCommentsListTests
{
    /// <summary>A 3-page PDF with <paramref name="count"/> sticky notes spread down the pages.</summary>
    internal static string ManyComments(TempDir temp, int count, string name = "many.pdf")
    {
        using var doc = new PdfDocument();
        for (int p = 0; p < 3; p++) doc.AddPage();
        var arrays = Enumerable.Range(0, 3).Select(_ => new PdfArray(doc)).ToArray();
        for (int i = 0; i < count; i++)
        {
            var a = new PdfDictionary(doc);
            a.Elements.SetName("/Type", "/Annot");
            a.Elements.SetName("/Subtype", "/Text");
            a.Elements.SetString("/Contents", $"Comment number {i + 1}");
            a.Elements.SetString("/T", "Reviewer");
            a.Elements.SetString("/NM", $"c{i + 1}");
            double y = 760 - (i % 14) * 50;
            a.Elements["/Rect"] = new PdfRectangle(new PdfSharp.Drawing.XRect(60 + (i % 3) * 150, y - 20, 20, 20));
            doc.Internals.AddObject(a);
            arrays[i * 3 / count].Elements.Add(a.Reference!);
        }
        for (int p = 0; p < 3; p++) doc.Pages[p].Elements["/Annots"] = arrays[p];
        string path = temp[name];
        doc.Save(path);
        return path;
    }

    private static ScrollViewer Scroller(ListBox list) => VisualTree.FindDescendants<ScrollViewer>(list).First();

    /// <summary>Index of the first comment row currently on screen in the list.</summary>
    private static int FirstVisible(PdfCommentsFlyout flyout)
    {
        var list = flyout.Panel.CommentList;
        var top = list.PointToScreen(new Point(0, 0)).Y;
        return VisualTree.FindDescendants<ListBoxItem>(list)
            .Where(i => i.IsVisible && i.PointToScreen(new Point(0, i.ActualHeight)).Y > top + 1)
            .Select(i => flyout.Panel.Comments.ToList().IndexOf((Core.Pdf.PdfComment)i.DataContext))
            .Where(i => i >= 0).DefaultIfEmpty(-1).Min();
    }

    private static void Open(PdfCommentsFlyout flyout)
    {
        flyout.Button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Sta.WaitUntil(() => flyout.Popup.IsOpen && flyout.Panel.HasResult && flyout.Panel.Comments.Count > 0, "comments list open");
        Sta.DoEvents(150); // layout of the list
    }

    [Fact]
    public void Reopening_the_list_after_Show_returns_to_the_same_place_with_the_comment_selected()
    {
        using var t = new TempDir();
        // The PDFs live outside the folder the window browses: writing files there would refresh the listing, which
        // (with nothing selected) clears the preview the test drives directly.
        t.Dir("docs");
        var pdf = ManyComments(t, 40, "docs/many.pdf");
        var browse = t.Dir("browse"); t.File("browse/readme.txt", "x");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, browse, s => s.PreviewVisible = true);
            try
            {
                var flyout = w.PreviewCommentsForTest;
                w.PreviewForTest.Show(pdf, delayMs: 0);
                Sta.WaitUntil(() => flyout.Panel.HasResult && flyout.Button.Visibility == Visibility.Visible, "comments read");
                Open(flyout);
                Assert.Equal(40, flyout.Panel.Comments.Count);

                // The user scrolls down the list and shows comment 20.
                var list = flyout.Panel.CommentList;
                var chosen = flyout.Panel.Comments[19];
                list.ScrollIntoView(flyout.Panel.Comments[17]);
                Sta.DoEvents(150);
                double offsetBefore = Scroller(list).VerticalOffset;
                int firstBefore = FirstVisible(flyout);
                Assert.True(firstBefore >= 10, $"list scrolled to the middle (first visible {firstBefore})");
                flyout.Panel.Request(chosen);
                Assert.False(flyout.Popup.IsOpen);
                Sta.WaitUntil(() => flyout.RevealTask.IsCompleted, "reveal done", 30000);

                // Reopen: same place, and the comment just shown is selected.
                Open(flyout);
                Assert.Same(chosen, flyout.Panel.CommentList.SelectedItem);
                Assert.Equal(offsetBefore, Scroller(list).VerticalOffset, 1);
                Assert.Equal(firstBefore, FirstVisible(flyout));
                flyout.Close();

                // The PDF changed on disk (a comment was added elsewhere): it is re-read, and the same comment
                // (matched by its id) is selected at the same place.
                ManyComments(t, 41, "docs/many.pdf");
                File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddMinutes(1));
                Open(flyout);
                Sta.WaitUntil(() => flyout.Panel.Comments.Count == 41, "re-read after the change");
                Sta.DoEvents(150);
                Assert.Equal("c20", Assert.IsType<Core.Pdf.PdfComment>(flyout.Panel.CommentList.SelectedItem).Id);
                Assert.Equal(firstBefore, FirstVisible(flyout));
                flyout.Close();

                // Another document starts at the top with nothing selected.
                var other = ManyComments(t, 40, "docs/other.pdf");
                w.PreviewForTest.Show(other, delayMs: 0);
                try { Sta.WaitUntil(() => flyout.Panel.HasResult, "other read"); }
                catch (TimeoutException) // keep the state that explains a timeout
                {
                    throw new TimeoutException($"other read: preview='{w.PreviewForTest.CurrentPath}' view='{w.View?.CurrentPath}' status='{flyout.Panel.Status}' load={flyout.Panel.LoadTask.Status} button={flyout.Button.Visibility}");
                }
                Open(flyout);
                Assert.Null(flyout.Panel.CommentList.SelectedItem);
                Assert.Equal(0, Scroller(flyout.Panel.CommentList).VerticalOffset, 1);
                flyout.Close();
            }
            finally { w.Close(); }
        });
    }
}
