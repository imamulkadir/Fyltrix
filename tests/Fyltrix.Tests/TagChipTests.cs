using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.Core.Tools;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>No Tags column: tags are chips after the name and are listed in the status bar; the Type column fits its values.</summary>
[Collection("UI")]
public class TagChipUiTests
{
    private static List<string> Headers(FolderView view) =>
        ((GridView)view.List.View).Columns.Select(c => ((GridViewColumnHeader)c.Header).Content?.ToString()?.Split(' ')[0] ?? "").ToList();

    [Fact]
    public void Tags_show_as_chips_after_the_name_and_in_the_status_bar()
    {
        using var t = new TempDir();
        var report = t.File("quarterly report.txt");
        t.File("plain.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                Assert.Equal(new[] { "Name", "Date", "Type", "Size" }, Headers(view));

                TagStore.Shared.SetTags(new[] { report }, new[] { "work", "q3" }, Array.Empty<string>());
                w.RefreshActiveForTest();
                Sta.WaitUntil(() => view.Items.Any(i => i.Name == "quarterly report.txt" && i.HasTags), "tags applied");
                Sta.DoEvents(200);
                var item = view.Items.Single(i => i.Name == "quarterly report.txt");
                Assert.Equal(new[] { "q3", "work" }, item.Tags.OrderBy(x => x));

                // chips sit in the Name cell, right after the name
                var row = (ListViewItem)view.List.ItemContainerGenerator.ContainerFromItem(item);
                var chipTexts = VisualTree.FindDescendants<TextBlock>(row).Where(b => b.Text.StartsWith("#")).ToList();
                Assert.Equal(2, chipTexts.Count);
                var name = VisualTree.FindDescendants<TextBlock>(row).Single(b => b.Text == "quarterly report.txt");
                double nameRight = name.TranslatePoint(new Point(name.ActualWidth, 0), row).X;
                double chipLeft = chipTexts.Min(c => c.TranslatePoint(new Point(0, 0), row).X);
                Assert.InRange(chipLeft - nameRight, 0, 20);
                var plainRow = (ListViewItem)view.List.ItemContainerGenerator.ContainerFromItem(view.Items.Single(i => i.Name == "plain.txt"));
                Assert.DoesNotContain(VisualTree.FindDescendants<TextBlock>(plainRow), b => b.IsVisible && b.Text.StartsWith("#"));

                // the status bar lists the selection's tags
                // (select the current item object: a refresh still finishing replaces the items and drops the selection)
                Sta.WaitUntil(() =>
                {
                    var current = view.Items.Single(i => i.Name == "quarterly report.txt");
                    if (view.List.SelectedItem != current) view.List.SelectedItem = current;
                    Sta.DoEvents(100);
                    return w.StatusText.Contains("#work");
                }, "status bar shows the selection's tags");
                Assert.Contains("#work", w.StatusText);
                Assert.Contains("#q3", w.StatusText);
                Sta.Render(w, "tag-chips");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Type_column_is_as_wide_as_its_longest_value()
    {
        using var t = new TempDir();
        t.File("a.txt");
        t.File("b.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                var typeCol = ((GridView)view.List.View).Columns.Single(c => ((GridViewColumnHeader)c.Header).Content?.ToString()?.StartsWith("Type") == true);
                double Width(string s) => new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(view.List.FontFamily, view.List.FontStyle, view.List.FontWeight, view.List.FontStretch), view.List.FontSize, Brushes.Black,
                    VisualTreeHelper.GetDpi(view).PixelsPerDip).WidthIncludingTrailingWhitespace;
                double widest = view.Items.Select(i => Width(i.TypeName)).Append(Width("Type  ▲")).Max();
                Assert.InRange(typeCol.Width, widest + 10, widest + 30);
                Assert.True(typeCol.Width < 170, $"narrower than the old fixed 170 ({typeCol.Width})");

                // a folder with a longer type name widens it; going back narrows it again
                var sub = t.Dir("sub");
                File.WriteAllText(Path.Combine(sub, "x.docx"), "");
                File.WriteAllText(Path.Combine(sub, "y.zip"), "");
                view.Navigate(sub);
                Sta.WaitUntil(() => view.CurrentPath == sub && view.Items.Count == 2, "sub listing");
                double widestSub = view.Items.Select(i => Width(i.TypeName)).Append(Width("Type  ▲")).Max();
                Assert.InRange(typeCol.Width, widestSub + 10, widestSub + 30);
                // every value fits without being cut off
                Sta.DoEvents(200);
                foreach (var row in VisualTree.FindDescendants<ListViewItem>(view.List))
                {
                    var fi = (FileItem)row.DataContext;
                    var cell = VisualTree.FindDescendants<TextBlock>(row).First(b => b.Text == fi.TypeName);
                    Assert.True(cell.ActualWidth >= Width(fi.TypeName) - 0.5, $"'{fi.TypeName}' is cut off");
                }
            }
            finally { w.Close(); }
        });
    }
}
