using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>The comment card in the Preview pane and in both sides of Compare (off-screen windows, isolated profile).</summary>
[Collection("UI")]
public class PdfCommentsUiTests
{
    private static string ButtonText(Button b) => string.Concat(VisualTree.FindDescendants<TextBlock>(b).Select(t => t.Text));

    [Fact]
    public void Preview_shows_a_Comments_button_only_for_PDFs_with_comments_and_the_card_follows_the_file()
    {
        using var t = new TempDir();
        var review = PdfAnnotationTests.ReviewPdf(t, "review.pdf");
        NativePdfCompareTests.Pdf(t["plain.pdf"], "No comments here");
        var text = t.File("note.txt", "hello");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => s.PreviewVisible = true);
            var saved = PdfCommentsFlyout.AutoReadLimit;
            try
            {
                var flyout = w.PreviewCommentsForTest;
                var preview = w.PreviewForTest;
                preview.Show(review, delayMs: 0);
                Sta.WaitUntil(() => flyout.Panel.HasResult && flyout.Button.Visibility == Visibility.Visible, "comments read for the previewed PDF");
                Assert.Equal(5, flyout.Panel.Comments.Count);                 // replies are threaded, not counted as comments
                Assert.Contains("Comments (5)", ButtonText(flyout.Button));
                Assert.Contains("3 replies", flyout.Panel.Status);

                flyout.Panel.Request(flyout.Panel.Comments[0]);
                Assert.True(flyout.Card.Popup.IsOpen);
                Assert.Equal("edit-1", flyout.Card.Comment!.Id);
                Assert.Equal(review, flyout.Card.DocumentPath);
                Sta.WaitUntil(() => flyout.RevealTask.IsCompleted && flyout.LastReveal != null, "reveal finished", 30000);
                // The note always matches what was achieved; it never claims a precise position it didn't reach.
                var level = flyout.LastReveal!.Level;
                if (level == Fyltrix.UI.RevealLevel.Exact) Assert.Equal("", flyout.Card.LocationNote);
                else if (level == Fyltrix.UI.RevealLevel.Vertical) Assert.Contains("isn't outlined", flyout.Card.LocationNote);
                else Assert.Contains("couldn't be confirmed", flyout.Card.LocationNote);
                Assert.False(flyout.Popup.IsOpen);                            // the list made room for the card

                preview.Show(t["plain.pdf"], delayMs: 0);                    // another file: the card never stays on it
                Assert.False(flyout.Card.Popup.IsOpen);
                Sta.WaitUntil(() => flyout.Panel.HasResult, "plain PDF read");
                Assert.Equal(Visibility.Collapsed, flyout.Button.Visibility); // no comments: no button in the preview header

                preview.Show(text, delayMs: 0);
                Assert.Equal(Visibility.Collapsed, flyout.Button.Visibility);

                // Large PDFs are not parsed while browsing; their comments are read when Comments is opened.
                PdfCommentsFlyout.AutoReadLimit = 0;
                preview.Show(review, delayMs: 0);
                Assert.False(flyout.Panel.HasResult);
                Assert.Equal(Visibility.Visible, flyout.Button.Visibility);
                flyout.Button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Sta.WaitUntil(() => flyout.Panel.HasResult && flyout.Panel.Comments.Count == 5, "read on demand");
                flyout.Close();
            }
            finally { PdfCommentsFlyout.AutoReadLimit = saved; w.Close(); }
        });
    }

    [Fact]
    public void Compare_cards_belong_to_their_own_side_and_close_when_documents_change()
    {
        using var t = new TempDir();
        var left = PdfAnnotationTests.ReviewPdf(t, "left.pdf");
        var right = PdfCommentTests.Fixture(t, "right.pdf", "Right side comment", "Right author");
        Sta.Run(() =>
        {
            var owner = new Window(); Sta.ShowOffscreen(owner);
            var compare = new CompareWindow(owner, left, right) { Width = 1400, Height = 900 };
            try
            {
                Sta.ShowOffscreen(compare);
                Sta.WaitUntil(() => compare.LeftComments.HasResult && compare.RightComments.HasResult, "both sides read");
                compare.LeftComments.Request(compare.LeftComments.Comments[0]);
                Assert.True(compare.LeftCommentsFlyout.Card.Popup.IsOpen);
                Assert.Equal(left, compare.LeftCommentsFlyout.Card.DocumentPath);
                Assert.Equal("new words", compare.LeftCommentsFlyout.Card.Comment!.Text);
                Assert.False(compare.RightCommentsFlyout.Card.Popup.IsOpen);

                compare.RightComments.Request(compare.RightComments.Comments.Single());
                Assert.Equal(right, compare.RightCommentsFlyout.Card.DocumentPath);
                Assert.Equal("Right side comment", compare.RightCommentsFlyout.Card.Comment!.Text);
                Assert.Equal(left, compare.LeftCommentsFlyout.Card.DocumentPath ?? left); // the left card never shows right-side data
                Assert.NotEqual("Right side comment", compare.LeftCommentsFlyout.Card.Comment?.Text);

                var swap = VisualTree.FindDescendants<Button>(compare).Single(b => b.ToolTip?.ToString() == "Swap left and right");
                swap.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.False(compare.LeftCommentsFlyout.Card.Popup.IsOpen);
                Assert.False(compare.RightCommentsFlyout.Card.Popup.IsOpen);
                Sta.WaitUntil(() => compare.LeftComments.HasResult && compare.RightComments.HasResult, "swapped sides read");
                Assert.Equal("Right side comment", compare.LeftComments.Comments.Single().Text);
            }
            finally { compare.Close(); owner.Close(); }
        });
    }
}
