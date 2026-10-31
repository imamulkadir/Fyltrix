using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>Quick Access / tab icons, Favourites and Recent management, This PC drive usage, the app icon's size.</summary>
[Collection("UI")]
public class PlacesTests
{
    private static string Hex(Brush b) => ((SolidColorBrush)b).Color.ToString();

    private static void Go(MainWindow w, string path)
    {
        w.View!.Navigate(path);
        Sta.WaitUntil(() => w.View.CurrentPath == path || (path == FolderView.ThisPC && w.View.IsThisPC), "navigate to " + path, 20000);
        Sta.DoEvents(150);
    }

    [Fact]
    public void Tabs_show_the_same_icon_as_quick_access_and_update_while_navigating()
    {
        using var t = new TempDir();
        t.File("a.txt");
        var plain = t.Dir("plain folder");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var accent = Hex(Theme.Brush("Accent"));
                // the window opens a Desktop tab, then the start folder in a second one
                Assert.Equal(2, w.TabIcons.Count);
                Assert.Equal(LocationIcons.QuickAccess().Single(p => p.Name == "Desktop").Glyph, w.TabIcons[0].Glyph);
                Assert.Equal((LocationIcons.Folder, accent), (w.ActiveTabIcon.Glyph, Hex(w.ActiveTabIcon.Brush)));

                var quick = w.SidebarRows("Quick access");
                var places = LocationIcons.QuickAccess();
                Assert.Contains(places, p => p.Name == "Downloads");
                Assert.Equal(places.Select(p => p.Name), quick.Select(q => q.Text));
                foreach (var place in places)
                {
                    Go(w, place.Path);
                    var row = quick.Single(q => q.Text == place.Name);
                    var tab = w.ActiveTabIcon;
                    Assert.Equal(row.Glyph, tab.Glyph);       // same icon…
                    Assert.Equal(Hex(row.Brush), Hex(tab.Brush)); // …and colour as the sidebar
                    Assert.Equal(place.Glyph, tab.Glyph);
                }
                // special places other than Home are in the Quick Access colour
                Assert.All(places.Where(p => p.Name != "Home"), p => Assert.Equal("Accent", p.Brush));

                Go(w, plain); // an ordinary folder, in the same tab: the folder icon again
                Assert.Equal((LocationIcons.Folder, accent), (w.ActiveTabIcon.Glyph, Hex(w.ActiveTabIcon.Brush)));
                var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
                Go(w, drive);
                Assert.Equal(LocationIcons.Drive, w.ActiveTabIcon.Glyph);
                Go(w, FolderView.ThisPC);
                Assert.Equal(LocationIcons.ThisPC, w.ActiveTabIcon.Glyph);
                Sta.Render(w, "tab-icons");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Favourites_toggle_remove_and_clear_without_touching_the_folders()
    {
        using var t = new TempDir();
        t.File("a.txt");
        var a = t.Dir("Alpha");
        var b = t.Dir("Beta");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var star = w.FavouriteButton!;
                string StarGlyph() => ((TextBlock)((StackPanel)star.Content).Children[0]).Text;
                void Click() => star.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Go(w, a);
                Assert.Equal(LocationIcons.FavouriteOutline, StarGlyph());
                Click(); // add
                Assert.True(w.IsFavourite(a));
                Assert.Equal(LocationIcons.Favourite, StarGlyph());
                var favRow = w.SidebarRows("Favourites").Single();
                Assert.Equal(("Alpha", LocationIcons.Favourite), (favRow.Text, favRow.Glyph));
                Assert.Equal(Hex(Theme.Brush("Accent")), Hex(favRow.Brush)); // the Quick Access colour, not yellow
                Assert.NotNull(w.SectionAction("Favourites"));

                Go(w, b);
                Assert.Equal(LocationIcons.FavouriteOutline, StarGlyph()); // per folder
                Click();
                Assert.Equal(new[] { "Alpha", "Beta" }, w.SidebarRows("Favourites").Select(r => r.Text));
                Assert.Equal(2, AppSettings.Load().Favourites.Count); // saved

                Click(); // the filled star removes it again
                Assert.False(w.IsFavourite(b));
                Assert.Equal(new[] { "Alpha" }, w.SidebarRows("Favourites").Select(r => r.Text));
                Click(); // and it can be added again
                Assert.True(w.IsFavourite(b));

                w.RemoveFavourite(a); // "Remove from Favourites" in the row's menu
                Assert.Equal(new[] { "Beta" }, w.SidebarRows("Favourites").Select(r => r.Text));

                w.ClearFavourites(confirm: false); // "Clear all Favourites…" after its confirmation
                Assert.Empty(w.SidebarRows("Favourites"));
                Assert.Null(w.SectionAction("Favourites")); // nothing left to clear
                Assert.Empty(AppSettings.Load().Favourites);
                Assert.Equal(LocationIcons.FavouriteOutline, StarGlyph()); // the open folder's star follows
                Assert.True(Directory.Exists(a) && Directory.Exists(b)); // only the list changed
                Sta.Render(w, "favourites-cleared");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Recent_entries_can_be_removed_and_cleared_without_touching_the_folders()
    {
        using var t = new TempDir();
        t.File("a.txt");
        var dirs = new[] { t.Dir("One"), t.Dir("Two"), t.Dir("Three") };
        t.File("Two/keep.txt", "keep");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                foreach (var d in dirs) Go(w, d);
                var names = w.SidebarRows("Recent").Select(r => r.Text).ToList();
                Assert.Equal(new[] { "Three", "Two", "One" }, names.Take(3));
                Assert.All(w.SidebarRows("Recent"), r => Assert.Equal(LocationIcons.Recent, r.Glyph));
                Assert.NotNull(w.SectionAction("Recent"));

                w.RemoveRecent(dirs[1]); // "Remove from Recent" in the row's menu
                Assert.DoesNotContain("Two", w.SidebarRows("Recent").Select(r => r.Text));
                Assert.DoesNotContain(AppSettings.Load().RecentFolders, f => AppSettings.SameFolder(f, dirs[1]));

                w.ClearRecent(confirm: false); // "Clear Recent…" after its confirmation
                Assert.DoesNotContain("RECENT", w.SidebarSections);
                Assert.Empty(AppSettings.Load().RecentFolders);
                Assert.All(dirs, d => Assert.True(Directory.Exists(d)));
                Assert.Equal("keep", File.ReadAllText(t["Two/keep.txt"]));

                Go(w, dirs[0]); // history starts again
                Assert.Contains("One", w.SidebarRows("Recent").Select(r => r.Text));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void This_PC_shows_each_drive_with_a_usage_bar_in_both_views()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                Go(w, FolderView.ThisPC);
                var view = w.View!;
                Sta.WaitUntil(() => view.Items.Count > 0 && view.Items.All(i => i.IsDrive), "drives"); // not the previous folder's items
                var system = Path.GetPathRoot(Environment.SystemDirectory)!;
                var item = view.Items.Single(i => string.Equals(i.FullPath, system, StringComparison.OrdinalIgnoreCase));
                var info = new DriveInfo(system);
                Assert.True(item.HasUsage);
                Assert.Equal(info.TotalSize, item.DriveTotal);
                Assert.InRange(item.DriveFree, info.AvailableFreeSpace - (1L << 30), info.AvailableFreeSpace + (1L << 30)); // other programs write meanwhile
                Assert.Equal($"{FileOps.FormatSize(item.DriveFree)} free of {FileOps.FormatSize(item.DriveTotal)}", item.UsageText);
                Assert.Contains("Total:", item.Tip);
                Assert.Contains($"({item.DriveTotal:N0} bytes)", item.Tip);

                ProgressBar Bar(FrameworkElement container) => VisualTree.FindDescendants<ProgressBar>(container).Single(p => p.IsVisible);
                Sta.DoEvents(300);
                var row = (FrameworkElement)view.List.ItemContainerGenerator.ContainerFromItem(item);
                var bar = Bar(row);
                Assert.Equal(item.UsedFraction, bar.Value, 3);
                Assert.Equal(Hex(Theme.Brush(item.IsLowSpace ? "Danger" : "Accent")), Hex(bar.Foreground));
                var indicator = (FrameworkElement)bar.Template.FindName("PART_Indicator", bar);
                Assert.InRange(indicator.ActualWidth, bar.ActualWidth * item.UsedFraction - 2, bar.ActualWidth * item.UsedFraction + 2);
                Assert.Contains(VisualTree.FindDescendants<TextBlock>(row), b => b.Text == item.UsageText && b.IsVisible);
                Sta.Render(w, "thispc-details");
                Sta.Render(w, "thispc-details-150", 1.5);

                view.SetViewMode(ViewMode.Tiles, 96);
                Sta.DoEvents(400);
                var tile = (FrameworkElement)view.List.ItemContainerGenerator.ContainerFromItem(item);
                Assert.Equal(item.UsedFraction, Bar(tile).Value, 3);
                Assert.Equal(item.Tip, tile.ToolTip);
                Sta.Render(w, "thispc-tiles");
                view.SetViewMode(ViewMode.Details, 96);

                // a normal folder row has no bar and no tooltip
                Go(w, t.Path);
                var fileRow = (FrameworkElement)view.List.ItemContainerGenerator.ContainerFromItem(view.Items.Single(i => i.Name == "a.txt"));
                Assert.DoesNotContain(VisualTree.FindDescendants<ProgressBar>(fileRow), p => p.IsVisible);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Sidebar_shows_drive_usage_under_each_drive_in_This_PC()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var refresh = w.RefreshSidebarDrivesForTest();
                Sta.WaitUntil(() => refresh.IsCompleted, "drive sizes", 20000);
                Sta.DoEvents(100);

                var system = Path.GetPathRoot(Environment.SystemDirectory)!;
                var info = new DriveInfo(system);
                var expected = FolderView.Drives();
                var rows = w.SidebarRows("This PC").Select(r => r.Text).ToList();
                Assert.Equal(new[] { "This PC" }.Concat(expected.Select(d => d.Name)), rows); // This PC, then every drive
                var c = expected.Single(d => string.Equals(d.FullPath, system, StringComparison.OrdinalIgnoreCase));

                var usage = w.SidebarDriveUsage(c.Name) ?? throw new Xunit.Sdk.XunitException("no usage under " + c.Name);
                double used = 1 - (double)info.AvailableFreeSpace / info.TotalSize;
                Assert.InRange(usage.Bar.Value, used - 0.01, used + 0.01);
                Assert.True(usage.Bar.IsVisible && usage.Bar.ActualWidth > 100, $"bar width {usage.Bar.ActualWidth}");
                Assert.Matches(@"^[\d.,]+ (GB|TB|MB) free of [\d.,]+ (GB|TB)$", usage.Text);
                Assert.EndsWith("free of " + FileOps.FormatSize(info.TotalSize), usage.Text);
                Assert.Contains("Total:", usage.Tip as string);
                Assert.Equal(Hex(Theme.Brush(c.IsLowSpace ? "Danger" : "Accent")), Hex(usage.Bar.Foreground));
                // drives that report nothing (no disc, not responding) show their name only
                foreach (var d in expected.Where(d => !d.HasUsage)) Assert.Null(w.SidebarDriveUsage(d.Name));
                Sta.Render(w, "sidebar-drives");
                Sta.Render(w, "sidebar-drives-150", 1.5);

                // opening This PC refreshes the sidebar from the list it just loaded
                Go(w, FolderView.ThisPC);
                Sta.WaitUntil(() => w.View!.Items.Count > 0 && w.View.Items.All(i => i.IsDrive), "drives");
                Sta.DoEvents(200);
                Assert.NotNull(w.SidebarDriveUsage(c.Name));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Low_space_is_red_and_a_drive_that_does_not_answer_is_listed_without_usage()
    {
        var low = new FileItem(@"Z:\", true, -1, DateTime.MinValue) { IsDrive = true, DriveTotal = 1000, DriveFree = 50 };
        Assert.True(low.IsLowSpace);
        Assert.Equal(0.95, low.UsedFraction, 3);
        Assert.Equal(Hex(Theme.Brush("Danger")), Hex((Brush)new LowSpaceBrush().Convert(true, typeof(Brush), null!, null!)));
        var ok = new FileItem(@"Y:\", true, -1, DateTime.MinValue) { IsDrive = true, DriveTotal = 1000, DriveFree = 400 };
        Assert.False(ok.IsLowSpace);
        var unknown = new FileItem(@"X:\", true, -1, DateTime.MinValue) { IsDrive = true };
        Assert.False(unknown.HasUsage);
        Assert.Null(unknown.UsageTip);

        // no time to answer: every drive is still listed, without usage, and nothing blocks or throws
        int before = FolderView.DriveTimeoutMs;
        try
        {
            FolderView.DriveTimeoutMs = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var drives = FolderView.Drives();
            Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
            Assert.Equal(DriveInfo.GetDrives().Length, drives.Count);
            Assert.All(drives.Where(d => !d.HasUsage), d => Assert.Matches("not (responding|available)", d.TypeName));
        }
        finally { FolderView.DriveTimeoutMs = before; }
    }

    [Fact]
    public void App_icon_fills_its_frame_like_other_apps()
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("Assets/Fyltrix.ico")!;
        var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 }, dec.Frames.Select(f => f.PixelWidth).OrderBy(x => x));
        foreach (var f in dec.Frames)
        {
            var c = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
            int n = c.PixelWidth;
            var px = new byte[n * n * 4];
            c.CopyPixels(px, n * 4, 0);
            int minX = n, maxX = -1, minY = n, maxY = -1;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (px[(y * n + x) * 4 + 3] > 40) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            int w = maxX - minX + 1, h = maxY - minY + 1;
            Assert.True(w >= n - Math.Max(1, n / 32), $"{n} px icon is only {w} px wide"); // was 27 of 32
            Assert.True(h >= n * 0.78, $"{n} px icon is only {h} px tall");             // was 23 of 32
            Assert.InRange((minY + maxY) / 2.0, n / 2.0 - 1.5, n / 2.0 + 1); // centred
        }
    }
}
