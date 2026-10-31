using System.Windows;
using System.Windows.Data;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

public class DateGroupTests
{
    private static string G(DateTime modified, DateTime now, DayOfWeek first = DayOfWeek.Monday) => DateGroups.Names[DateGroups.Rank(modified, now, first)];

    [Fact]
    public void Buckets_match_Explorer()
    {
        var now = new DateTime(2026, 10, 29, 15, 0, 0); // a Thursday
        Assert.Equal("Today", G(new DateTime(2026, 10, 29, 0, 0, 0), now));
        Assert.Equal("Today", G(new DateTime(2026, 10, 29, 23, 59, 0), now));
        Assert.Equal("In the future", G(new DateTime(2026, 10, 30, 0, 0, 1), now));
        Assert.Equal("Yesterday", G(new DateTime(2026, 10, 28, 23, 0, 0), now));
        Assert.Equal("Yesterday", G(new DateTime(2026, 10, 28, 0, 0, 0), now));
        Assert.Equal("Earlier this week", G(new DateTime(2026, 10, 26, 8, 0, 0), now));    // Monday
        Assert.Equal("Last week", G(new DateTime(2026, 10, 25, 22, 0, 0), now));          // Sunday
        Assert.Equal("Last week", G(new DateTime(2026, 10, 19, 0, 0, 0), now));
        Assert.Equal("Earlier this month", G(new DateTime(2026, 10, 18, 23, 0, 0), now));
        Assert.Equal("Earlier this month", G(new DateTime(2026, 10, 1, 0, 0, 0), now));
        Assert.Equal("Last month", G(new DateTime(2026, 9, 30, 12, 0, 0), now));
        Assert.Equal("Last month", G(new DateTime(2026, 9, 1, 0, 0, 0), now));
        Assert.Equal("Earlier this year", G(new DateTime(2026, 8, 31, 0, 0, 0), now));
        Assert.Equal("Earlier this year", G(new DateTime(2026, 1, 1, 0, 0, 0), now));
        Assert.Equal("A long time ago", G(new DateTime(2025, 12, 31, 23, 0, 0), now));
        Assert.Equal("A long time ago", G(DateTime.MinValue, now));
    }

    [Fact]
    public void Weeks_start_on_the_users_first_day_and_months_cross_years()
    {
        var thursday = new DateTime(2026, 10, 29, 15, 0, 0);
        Assert.Equal("Earlier this week", G(new DateTime(2026, 10, 25, 10, 0, 0), thursday, DayOfWeek.Sunday));
        Assert.Equal("Last week", G(new DateTime(2026, 10, 24, 10, 0, 0), thursday, DayOfWeek.Sunday));

        // first day of the week: yesterday is still Yesterday, the days before it are last week
        var monday = new DateTime(2026, 11, 2, 9, 0, 0);
        Assert.Equal("Yesterday", G(new DateTime(2026, 11, 1, 9, 0, 0), monday));
        Assert.Equal("Last week", G(new DateTime(2026, 10, 27, 9, 0, 0), monday)); // last month too, but last week wins
        Assert.Equal("Last month", G(new DateTime(2026, 10, 20, 9, 0, 0), monday));

        // January: last month is December of the previous year
        var january = new DateTime(2026, 1, 20, 9, 0, 0);
        Assert.Equal("Last month", G(new DateTime(2025, 12, 5), january));
        Assert.Equal("A long time ago", G(new DateTime(2025, 11, 30), january));
    }

    [Fact]
    public void Downloads_is_grouped_by_default_and_other_folders_are_not()
    {
        var s = new AppSettings();
        Assert.True(s.IsGroupedByDate(AppSettings.DownloadsFolder));
        Assert.True(s.IsGroupedByDate(AppSettings.DownloadsFolder + "\\"));
        Assert.False(s.IsGroupedByDate(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        s.SetGroupedByDate(AppSettings.DownloadsFolder.ToUpperInvariant(), false);
        Assert.False(s.IsGroupedByDate(AppSettings.DownloadsFolder));
        Assert.True(Directory.Exists(AppSettings.DownloadsFolder));
    }
}

[Collection("UI")]
public class DateGroupUiTests
{
    [Fact]
    public void Folder_lists_items_under_date_headings_and_can_be_switched_off()
    {
        using var t = new TempDir();
        var now = DateTime.Now;
        void F(string name, DateTime when) { var p = t.File(name); File.SetLastWriteTime(p, when); }
        F("b today.txt", now.AddMinutes(-5));
        F("a today.txt", now.Date.AddMinutes(1));
        F("yesterday.pdf", now.Date.AddDays(-1).AddHours(12));
        F("ten days.zip", now.AddDays(-10));
        F("forty days.mkv", now.AddDays(-40));
        F("old.docx", now.AddDays(-800));
        var dir = t.Dir("Project");
        Directory.SetLastWriteTime(dir, now.AddMinutes(-1));
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => s.GroupByDateFolders = new List<string> { t.Path });
            try
            {
                var view = w.View!;
                Sta.DoEvents(200);
                Assert.True(view.IsGroupedByDate);
                var cv = (ListCollectionView)CollectionViewSource.GetDefaultView(view.Items);
                var groups = cv.Groups.Cast<CollectionViewGroup>().ToList();
                var names = groups.Select(g => (string)g.Name).ToList();
                var counts = groups.Select(g => g.ItemCount).ToList();
                // newest group first, each bucket once, every item in the bucket of its own date
                Assert.Equal(names.OrderBy(n => Array.IndexOf(DateGroups.Names, n)), names);
                Assert.Equal(names.Distinct(), names);
                Assert.Equal("Today", names[0]);
                Assert.Equal("A long time ago", names[^1]);
                foreach (var g in groups)
                    Assert.All(g.Items.Cast<FileItem>(), i => Assert.Equal((string)g.Name, DateGroups.Name(i.Modified, DateTime.Now)));
                // inside a group: the column sort (folders first, then by name)
                Assert.Equal(new[] { "_settings", "Project", "a today.txt", "b today.txt" }, groups[0].Items.Cast<FileItem>().Select(i => i.Name));
                Assert.Contains(w.SidebarSections, s => s == "QUICK ACCESS"); // window intact
                Sta.Render(w, "date-groups");

                // a heading is not an item: right-click there opens the folder's menu
                var header = VisualTree.FindDescendants<System.Windows.Controls.GroupItem>(view.List).First();
                var at = header.TranslatePoint(new Point(40, 14), view.List);
                Assert.Null(view.RightClickForTest(at));

                // the type-to-filter box still works inside groups
                view.SetFilter("today");
                Sta.DoEvents(100);
                Assert.Equal(new[] { "Today" }, cv.Groups.Cast<CollectionViewGroup>().Where(g => g.ItemCount > 0).Select(g => (string)g.Name));
                view.SetFilter("");

                // the View menu shows it ticked; switching it off removes the headings and is remembered
                var item = w.GroupByDateMenuItem();
                Assert.True(item.IsEnabled);
                Assert.NotNull(item.Icon);
                w.SetGroupByDate(t.Path, false);
                Assert.False(view.IsGroupedByDate);
                Assert.Empty(CollectionViewSource.GetDefaultView(view.Items).GroupDescriptions);
                Assert.Equal(new[] { "_settings", "Project", "a today.txt" }, view.Items.Take(3).Select(i => i.Name)); // plain name order again
                Assert.False(AppSettings.Load().IsGroupedByDate(t.Path));
                w.SetGroupByDate(t.Path, true);
                Assert.True(view.IsGroupedByDate);
                Assert.True(AppSettings.Load().IsGroupedByDate(t.Path));

                // thumbnails: the same groups, drawn by the tile panel (WPF groups are off there)
                w.SetView(96);
                Sta.DoEvents(200);
                Assert.True(view.IsGroupedByDate);
                Assert.True(w.GroupByDateMenuItem().IsEnabled);
                Assert.Empty(CollectionViewSource.GetDefaultView(view.Items).GroupDescriptions);
                var panel = view.TilePanel!;
                Assert.Equal(names, panel.Groups.Select(g => g.Name));
                Assert.Equal(counts, panel.Groups.Select(g => g.Count));
                // each group starts a new row, under its heading; a click on a heading is background
                foreach (var g in panel.Groups)
                {
                    var first = panel.Position(g.Start);
                    Assert.Equal(0, first.X);
                    Assert.Equal(g.Top + VirtualizingWrapPanel.HeaderHeight, first.Y, 1);
                }
                var headingAt = panel.TranslatePoint(new Point(60, panel.Groups[1].Top + 12), view.List);
                Assert.Null(view.RightClickForTest(headingAt));
                var tileAt = panel.TranslatePoint(new Point(panel.Position(panel.Groups[1].Start).X + 20, panel.Position(panel.Groups[1].Start).Y + 20), view.List);
                Assert.Same(view.List.Items[panel.Groups[1].Start], view.RightClickForTest(tileAt));
                // Down from the last row of "Today" goes to the next group, Up comes back
                Assert.Equal(panel.Groups[1].Start, panel.IndexBelow(0));
                Assert.Equal(0, panel.IndexAbove(panel.Groups[1].Start));
                Assert.Equal(-1, panel.IndexAbove(0));
                Sta.Render(w, "date-groups-tiles");
                w.SetView(0);
                Sta.DoEvents(100);
                Assert.True(view.IsGroupedByDate);
                Assert.Single(CollectionViewSource.GetDefaultView(view.Items).GroupDescriptions);
                Assert.Null(VirtualizingWrapPanel.GetGroupKey(view.List));

                // sorting by date ascending puts the oldest group first
                view.SortBy(SortColumn.Modified, ascending: true);
                cv = (ListCollectionView)CollectionViewSource.GetDefaultView(view.Items);
                Assert.Equal("A long time ago", (string)((CollectionViewGroup)cv.Groups[0]).Name);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Large_grouped_grid_creates_only_the_visible_tiles()
    {
        using var t = new TempDir();
        var now = DateTime.Now;
        for (int i = 0; i < 3000; i++) File.SetLastWriteTime(t.File($"f{i:0000}.txt"), now.AddHours(-i * 3));
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => { s.GroupByDateFolders = new List<string> { t.Path }; s.ViewTileSize = 96; });
            try
            {
                var view = w.View!;
                Sta.WaitUntil(() => view.TilePanel is { Groups.Count: > 3 }, "grouped grid");
                var panel = view.TilePanel!;
                Assert.True(panel.Realized.count < 200, $"realized {panel.Realized.count} tiles");
                Assert.Equal(3001, panel.Groups.Sum(g => g.Count)); // + the _settings folder
                // scrolling to the end realizes the last tiles, under the right heading
                view.List.ScrollIntoView(view.List.Items[^1]);
                Sta.DoEvents(200);
                Assert.True(panel.Realized.first + panel.Realized.count >= view.List.Items.Count - 1);
                Assert.True(panel.Realized.count < 200);
                Assert.Equal(panel.ExtentHeight, panel.Groups[^1].Top + VirtualizingWrapPanel.HeaderHeight + panel.Groups[^1].Rows * panel.ItemHeight, 1);
                Sta.Render(w, "date-groups-tiles-large");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Other_folders_and_search_results_are_not_grouped()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                Assert.False(w.View!.IsGroupedByDate);
                Assert.Empty(CollectionViewSource.GetDefaultView(w.View.Items).GroupDescriptions);
            }
            finally { w.Close(); }
        });
    }
}
