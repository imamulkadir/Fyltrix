using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class MarqueeSelectionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public MarqueeSelectionTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    private static TempDir Files(int count = 60)
    {
        var temp = new TempDir();
        for (int i = 0; i < count; i++) temp.File($"data/file{i:00000}.txt");
        return temp;
    }

    private static Rect Viewport(FolderView view)
    {
        var presenter = VisualTree.FindDescendants<ScrollContentPresenter>(view.List).OrderByDescending(p => p.ActualHeight).First();
        return new Rect(presenter.TranslatePoint(new Point(), view.List), presenter.RenderSize);
    }

    private static Rect Bounds(FolderView view, ListViewItem row)
    {
        var point = row.TranslatePoint(new Point(), view.List);
        double width = row.ActualWidth;
        if (view.Mode == ViewMode.Details)
        {
            var presenter = VisualTree.FindDescendant<GridViewRowPresenter>(row)!;
            point.X = presenter.TranslatePoint(new Point(), view.List).X;
            width = ((GridView)view.List.View).Columns.Sum(c => c.ActualWidth);
        }
        return new Rect(point, new Size(width, row.ActualHeight));
    }

    private static Point Blank(FolderView view)
    {
        var viewport = Viewport(view);
        // Right-hand gutter is outside selectable Details columns and outside complete tile columns.
        return new Point(viewport.Right - 2, viewport.Top + 5);
    }

    private static string[] Names(FolderView view) => view.SelectedItems.Select(i => i.Name).OrderBy(n => n).ToArray();

    [Theory]
    [InlineData(0, false)] [InlineData(48, false)] [InlineData(96, false)]
    [InlineData(160, false)] [InlineData(256, false)]
    [InlineData(0, true)] [InlineData(48, true)] [InlineData(96, true)]
    [InlineData(160, true)] [InlineData(256, true)]
    public void Rectangle_selects_real_intersecting_rows_and_finalizes_in_every_view(int size, bool grouped)
    {
        using var temp = Files();
        for (int i = 30; i < 60; i++) File.SetLastWriteTime(temp[$"data/file{i:00000}.txt"], DateTime.Now.AddDays(-8));
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                if (grouped) { view.GroupByDateFor = _ => true; view.RefreshGrouping(); }
                if (size != 0) window.SetView(size);
                Sta.DoEvents(80);
                var start = Blank(view);
                var viewport = Viewport(view);
                var end = new Point(viewport.Left + 2, Math.Min(viewport.Bottom - 30, viewport.Top + 230));
                var rectangle = new Rect(start, end);
                var expected = VisualTree.FindDescendants<ListViewItem>(view.List)
                    .Where(r => { var hit = Rect.Intersect(rectangle, Bounds(view, r)); return !hit.IsEmpty && hit.Width > 0 && hit.Height > 0; })
                    .Select(r => ((FileItem)r.DataContext).Name).OrderBy(n => n).ToArray();
                Assert.NotEmpty(expected);
                Assert.True(view.BeginMarquee(start, ModifierKeys.None, capture: false));
                Assert.False(view.MarqueeVisible);
                view.MoveMarquee(end);
                Assert.True(view.MarqueeVisible);
                Assert.Equal(expected, Names(view));
                Sta.Render(window, $"marquee-{size}-grouped-{grouped}");
                view.FinishMarquee();
                Assert.False(view.MarqueePending);
                Assert.False(view.MarqueeVisible);
                Assert.False(view.MarqueeAutoScrolling);
                Assert.Equal(expected, Names(view));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Item_down_and_headers_never_begin_marquee_and_threshold_keeps_rectangle_hidden()
    {
        using var temp = Files();
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                var row = (ListViewItem)view.List.ItemContainerGenerator.ContainerFromIndex(0);
                var point = row.TranslatePoint(new Point(40, row.ActualHeight / 2), view.List);
                view.SelectPaths(new[] { temp["data/file00000.txt"], temp["data/file00001.txt"] });
                Assert.False(view.BeginMarquee(point, ModifierKeys.None, false));
                Assert.Equal(2, view.SelectedItems.Count);
                Assert.False(view.BeginMarquee(new Point(20, 2), ModifierKeys.None, false));
                var start = Blank(view);
                Assert.True(view.BeginMarquee(start, ModifierKeys.None, false));
                view.MoveMarquee(new Point(start.X - 1, start.Y + 1));
                Assert.False(view.MarqueeVisible);
                view.CancelMarquee();
                Assert.Equal(2, view.SelectedItems.Count);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ModifierKeys.Control)] [InlineData(ModifierKeys.Shift)]
    public void Modifier_marquee_uses_initial_snapshot_without_repeated_toggling(ModifierKeys modifier)
    {
        using var temp = Files();
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                view.SelectPaths(new[] { temp["data/file00000.txt"], temp["data/file00020.txt"] });
                var start = Blank(view);
                var row = (ListViewItem)view.List.ItemContainerGenerator.ContainerFromIndex(2);
                var end = row.TranslatePoint(new Point(10, row.ActualHeight / 2), view.List);
                Assert.True(view.BeginMarquee(start, modifier, false));
                view.MoveMarquee(end);
                var expected = modifier == ModifierKeys.Control
                    ? new[] { "file00001.txt", "file00002.txt", "file00020.txt" }
                    : new[] { "file00000.txt", "file00001.txt", "file00002.txt", "file00020.txt" };
                Assert.Equal(expected, Names(view));
                for (int i = 0; i < 4; i++) view.MoveMarquee(end);
                Assert.Equal(expected, Names(view));
                view.MoveMarquee(start);
                Assert.Equal(new[] { "file00000.txt", "file00020.txt" }, Names(view));
                view.MoveMarquee(end);
                view.CancelMarquee();
                Assert.Equal(new[] { "file00000.txt", "file00020.txt" }, Names(view));
                Assert.False(view.MarqueeVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("escape")] [InlineData("filter")] [InlineData("view")]
    [InlineData("group")] [InlineData("navigate")] [InlineData("close")]
    [InlineData("capture")] [InlineData("sort")] [InlineData("refresh")]
    [InlineData("search")]
    public void Interrupted_gestures_release_overlay_timer_and_restore_selection(string interruption)
    {
        using var temp = Files();
        temp.File("other/other.txt");
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                view.SelectPaths(new[] { temp["data/file00020.txt"] });
                Assert.True(view.BeginMarquee(Blank(view), ModifierKeys.None, false));
                var viewport = Viewport(view);
                view.MoveMarquee(new Point(viewport.Left + 5, viewport.Bottom + 10));
                Assert.True(view.MarqueeAutoScrolling);
                switch (interruption)
                {
                    case "escape":
                        view.List.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view.List)!, 0, Key.Escape)
                            { RoutedEvent = UIElement.PreviewKeyDownEvent }); break;
                    case "filter": view.SetFilter("file"); break;
                    case "view": window.SetView(96); break;
                    case "group": view.RefreshGrouping(); break;
                    case "navigate": view.Navigate(temp["other"]); break;
                    case "close": view.Close(); break;
                    case "capture":
                        view.List.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent }); break;
                    case "sort": view.SortBy(SortColumn.Name, false); break;
                    case "refresh": _ = view.Refresh(); break;
                    case "search": view.StartSearch("file"); break;
                }
                Assert.False(view.MarqueePending);
                Assert.False(view.MarqueeVisible);
                Assert.False(view.MarqueeAutoScrolling);
                if (interruption is "escape" or "filter" or "close") Assert.Equal(new[] { "file00020.txt" }, Names(view));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(96)]
    public void Scrolling_extends_selection_without_realizing_the_whole_directory(int size)
    {
        using var temp = Files(5000);
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                if (size != 0) window.SetView(size);
                Sta.DoEvents(100);
                var viewport = Viewport(view);
                Assert.True(view.BeginMarquee(Blank(view), ModifierKeys.None, false));
                var end = new Point(viewport.Left + 1, viewport.Bottom + 20);
                view.MoveMarquee(end);
                int initial = view.SelectedItems.Count;
                for (int i = 0; i < 20; i++) { view.ScrollMarquee(); Sta.DoEvents(10); }
                Assert.True(view.SelectedItems.Count > initial);
                Assert.Contains(view.SelectedItems, i => i.Name == "file00000.txt");
                Assert.InRange(VisualTree.FindDescendants<ListViewItem>(view.List).Count(), 1, 400);
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 300; i++) view.MoveMarquee(new Point(end.X + i % 2, end.Y));
                watch.Stop();
                _output.WriteLine($"View {size}: 300 updates in {watch.Elapsed.TotalMilliseconds:F2} ms; {VisualTree.FindDescendants<ListViewItem>(view.List).Count()} realized containers; {view.SelectedItems.Count} selected of 5000.");
                Assert.True(watch.ElapsedMilliseconds < 3000, $"300 marquee updates took {watch.ElapsedMilliseconds}ms");
                view.CancelMarquee();
                Assert.Empty(view.SelectedItems);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(48)] [InlineData(96)] [InlineData(160)] [InlineData(256)]
    public void Sorted_filtered_rectangle_selects_only_the_visible_order(int size)
    {
        using var temp = Files();
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                view.SortBy(SortColumn.Name, false);
                view.SetFilter("0000"); // only file00000 through file00009
                if (size != 0) window.SetView(size);
                Sta.DoEvents(80);
                Assert.Equal(10, view.List.Items.Count);
                var start = Blank(view);
                var viewport = Viewport(view);
                var end = new Point(viewport.Left + 1, viewport.Bottom - 2);
                var rectangle = new Rect(start, end);
                var expected = VisualTree.FindDescendants<ListViewItem>(view.List)
                    .Where(r => { var hit = Rect.Intersect(rectangle, Bounds(view, r)); return !hit.IsEmpty && hit.Width > 0 && hit.Height > 0; })
                    .Select(r => ((FileItem)r.DataContext).Name).OrderBy(n => n).ToArray();
                Assert.True(view.BeginMarquee(start, ModifierKeys.None, false));
                view.MoveMarquee(end);
                view.FinishMarquee();
                Assert.Equal(expected, Names(view));
                Assert.All(view.SelectedItems, item => Assert.Contains(item, view.List.Items.Cast<FileItem>()));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(96)]
    public void Marquee_still_hits_actual_rows_after_scrolling_across_date_groups(int size)
    {
        using var temp = Files(180);
        for (int i = 60; i < 180; i++) File.SetLastWriteTime(temp[$"data/file{i:00000}.txt"], DateTime.Now.AddDays(i < 120 ? -2 : -12));
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                view.GroupByDateFor = _ => true;
                view.RefreshGrouping();
                if (size != 0) window.SetView(size);
                view.List.ScrollIntoView(view.List.Items[110]);
                Sta.DoEvents(150);
                var viewport = Viewport(view);
                var start = new Point(viewport.Right - 2, viewport.Bottom - 4);
                var end = new Point(viewport.Left + 2, viewport.Top + 20);
                var rectangle = new Rect(start, end);
                var expected = VisualTree.FindDescendants<ListViewItem>(view.List)
                    .Where(r => { var hit = Rect.Intersect(rectangle, Bounds(view, r)); return !hit.IsEmpty && hit.Width > 0 && hit.Height > 0; })
                    .Select(r => ((FileItem)r.DataContext).Name).OrderBy(n => n).ToArray();
                Assert.NotEmpty(expected);
                Assert.True(view.BeginMarquee(start, ModifierKeys.None, false));
                view.MoveMarquee(end);
                Assert.Equal(expected, Names(view));
                view.CancelMarquee();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Details_edge_scrolls_horizontally_and_selection_updates_commands()
    {
        using var temp = Files();
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                var columns = ((GridView)view.List.View).Columns;
                columns[0].Width = 1600;
                Sta.DoEvents(60);
                var scroll = VisualTree.FindDescendant<ScrollViewer>(view.List)!;
                // Filtering down leaves blank space below rows while keeping wide columns.
                view.SetFilter("0000");
                Sta.DoEvents(60);
                var viewport = Viewport(view);
                var start = new Point(viewport.Right - 3, viewport.Bottom - 12);
                Assert.True(view.BeginMarquee(start, ModifierKeys.None, false));
                view.MoveMarquee(new Point(viewport.Right + 30, viewport.Top + 2));
                Assert.True(view.MarqueeAutoScrolling);
                view.ScrollMarquee();
                Sta.DoEvents(60);
                Assert.True(scroll.HorizontalOffset > 0);
                view.MoveMarquee(new Point(viewport.Left + 2, viewport.Top + 2));
                Assert.NotEmpty(view.SelectedItems);
                var copy = (Button)window.ToolbarForTest.Single(b => b.Id == "copy").Element;
                Assert.True(copy.IsEnabled);
                Assert.True(MainWindowUiTests.Item(window.BuildContextMenu(view, view.SelectedItems), "Copy").IsEnabled);
                view.CancelMarquee();
                Assert.False(copy.IsEnabled);
                Assert.False(view.MarqueeAutoScrolling);
            }
            finally { window.Close(); }
        });
    }
}
