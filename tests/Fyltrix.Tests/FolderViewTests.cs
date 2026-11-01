using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>Per-folder views: storage rules (keys, defaults, pruning, renames) without UI.</summary>
[Collection("UI")] // mutates AppSettings.Folder and MaxFolderViews, shared with settings/UI tests
public class FolderViewStorageTests
{
    [Fact]
    public void Keys_are_normalised_and_case_insensitive()
    {
        Assert.Equal(@"C:\Projects", AppSettings.ViewKey(@"C:\Projects\"));
        Assert.Equal(@"C:\Projects\FileLens", AppSettings.ViewKey(@"C:\Projects\.\FileLens"));
        Assert.Equal("C:", AppSettings.ViewKey(@"C:\"));
        Assert.Equal(AppSettings.ThisPCViewKey, AppSettings.ViewKey(""));
        Assert.Equal(@"\\server\share\docs", AppSettings.ViewKey(@"\\server\share\docs\"));

        var s = new AppSettings { ViewTileSize = 0 };
        s.SetFolderView(AppSettings.ViewKey(@"C:\Projects"), 48);
        s.SetFolderView(AppSettings.ViewKey(@"c:\PROJECTS\"), 160); // same folder: replaces, no second entry
        Assert.Single(s.FolderViews);
        Assert.Equal(160, s.ViewFor(AppSettings.ViewKey(@"C:\projects")));
        Assert.Equal(0, s.ViewFor(AppSettings.ViewKey(@"C:\Other"))); // no entry: the default
        s.SetFolderView(@"C:\Bad", 77); // not a view: ignored
        Assert.False(s.HasOwnView(@"C:\Bad"));

        // a file with the same folder twice in different case (older or hand-edited) loads as one entry
        s.FolderViews = new Dictionary<string, int> { [@"D:\Pics\"] = 256, [@"d:\pics"] = 96, ["E:\\x"] = 5 };
        Assert.Single(s.FolderViews);
        Assert.Equal(96, s.ViewFor(@"D:\PICS"));
    }

    [Fact]
    public void Default_applies_only_to_folders_without_their_own_view_and_everything_persists()
    {
        using var t = new TempDir();
        var before = AppSettings.Folder;
        try
        {
            AppSettings.Folder = t.Dir("settings");
            var s = new AppSettings { ViewTileSize = 0 };
            s.SetFolderView(AppSettings.ViewKey(@"C:\Projects"), 48);
            s.SetFolderView(AppSettings.ViewKey(@"D:\Downloads"), 96);
            s.ViewTileSize = 160; // a new default
            Assert.Equal(48, s.ViewFor(@"C:\Projects"));
            Assert.Equal(160, s.ViewFor(@"C:\Elsewhere"));
            s.Save();

            var loaded = AppSettings.Load();
            Assert.Equal(160, loaded.ViewTileSize);
            Assert.Equal(48, loaded.ViewFor(AppSettings.ViewKey(@"c:\projects\"))); // case-insensitive after loading too
            Assert.Equal(96, loaded.ViewFor(@"D:\Downloads"));
            Assert.True(loaded.ResetFolderView(@"C:\PROJECTS"));
            Assert.Equal(160, loaded.ViewFor(@"C:\Projects"));
            loaded.ResetAllFolderViews();
            Assert.Empty(loaded.FolderViews);
        }
        finally { AppSettings.Folder = before; }
    }

    [Fact]
    public void Renamed_folders_keep_their_views_and_deleted_ones_are_pruned()
    {
        using var t = new TempDir();
        var s = new AppSettings();
        var a = t.Dir("Projects");
        var sub = t.Dir("Projects/FileLens");
        s.SetFolderView(AppSettings.ViewKey(a), 48);
        s.SetFolderView(AppSettings.ViewKey(sub), 160);
        s.SetFolderView(AppSettings.ViewKey(t["ProjectsOther"]), 96); // a sibling with the same prefix: not moved
        s.MoveFolderViews(a, t["Work"]);
        Assert.Equal(48, s.ViewFor(AppSettings.ViewKey(t["Work"])));
        Assert.Equal(160, s.ViewFor(AppSettings.ViewKey(t["Work/FileLens"])));
        Assert.False(s.HasOwnView(AppSettings.ViewKey(a)));
        Assert.True(s.HasOwnView(AppSettings.ViewKey(t["ProjectsOther"])));

        // over the limit, folders that no longer exist (on a local fixed drive) are dropped; existing and network ones stay
        Directory.Move(a, t["Work"]);
        Directory.Delete(t["Work/FileLens"]); // deleted outside Fyltrix
        int max = AppSettings.MaxFolderViews;
        try
        {
            AppSettings.MaxFolderViews = 3;
            s.SetFolderView(@"\\server\share\docs", 96);
            s.SetFolderView(AppSettings.ViewKey(t["Work"]), 48);
            Assert.True(s.HasOwnView(AppSettings.ViewKey(t["Work"])));
            Assert.True(s.HasOwnView(@"\\server\share\docs"));
            Assert.False(s.HasOwnView(AppSettings.ViewKey(t["ProjectsOther"]))); // never existed
            Assert.False(s.HasOwnView(AppSettings.ViewKey(t["Work/FileLens"]))); // gone
        }
        finally { AppSettings.MaxFolderViews = max; }
    }

    [Fact]
    public void Lookups_stay_fast_with_thousands_of_folders()
    {
        var s = new AppSettings();
        for (int i = 0; i < 4000; i++) s.SetFolderView(AppSettings.ViewKey($@"C:\Data\Folder {i}"), AppSettings.ViewSizes[i % 5]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int sum = 0;
        for (int i = 0; i < 100_000; i++) sum += s.ViewFor(AppSettings.ViewKey($@"c:\data\folder {i % 4000}\"));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms for 100k lookups");
        Assert.True(sum > 0);
    }
}

/// <summary>Per-folder views in the real window: each folder keeps its view while browsing, across tabs and restarts.</summary>
[Collection("UI")]
public class FolderViewUiTests
{
    private static void Go(MainWindow w, string path)
    {
        w.View!.Navigate(path);
        Sta.WaitUntil(() => w.View.CurrentPath == path, "navigate to " + path);
        Sta.DoEvents(100);
    }

    private static int Shown(MainWindow w) => MainWindow.ShownView(w.View!);

    private static string Ticked(MainWindow w) =>
        (string)w.BuildViewMenu().Items.OfType<MenuItem>().Take(5).Single(m => m.Icon != null).Header;

    [Fact]
    public void Each_folder_keeps_its_own_view_while_browsing_and_in_other_tabs()
    {
        using var t = new TempDir();
        var projects = t.Dir("Projects");
        var legacyFolder = t.Dir("Projects/FileLens");
        var downloads = t.Dir("D/Downloads");
        var pictures = t.Dir("D/Pictures");
        var plain = t.Dir("Plain");
        foreach (var d in new[] { projects, legacyFolder, downloads, pictures, plain }) File.WriteAllText(Path.Combine(d, "file.txt"), "x");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                Assert.Equal(0, Shown(w)); // default: Details
                Go(w, projects);
                w.SetView(48);
                Go(w, legacyFolder);
                Assert.Equal(0, Shown(w)); // not affected by Projects
                w.SetView(160);
                Assert.Equal("Large icons", Ticked(w));
                Go(w, downloads);
                w.SetView(96);
                Go(w, pictures);
                w.SetView(256);

                // back in history and by path: every folder comes back as it was left
                w.View!.GoBack();
                Sta.WaitUntil(() => w.View.CurrentPath == downloads, "back");
                Assert.Equal(96, Shown(w));
                Go(w, projects);
                Assert.Equal(48, Shown(w));
                Assert.Equal("Small icons", Ticked(w));
                Go(w, legacyFolder);
                Assert.Equal(160, Shown(w));
                Go(w, plain);
                Assert.Equal(0, Shown(w)); // never set: the default
                Assert.Equal("Details", Ticked(w));
                Go(w, t.Path);
                Assert.Equal(0, Shown(w));

                // another tab opening a folder uses that folder's view
                w.AddTabForTest(pictures);
                Sta.WaitUntil(() => w.View!.CurrentPath == pictures, "second tab");
                Assert.Equal(256, Shown(w));

                // the same folder in two tabs: changing it in one updates the other, and leaves a third alone
                w.AddTabForTest(projects);
                Sta.WaitUntil(() => w.View!.CurrentPath == projects, "third tab");
                var tabs = w.TabViews;
                var first = tabs.First(v => v.CurrentPath == t.Path);
                w.ActivateTabForTest(tabs.ToList().IndexOf(first));
                Go(w, projects); // first tab now also on Projects
                var other = tabs.Last();
                Assert.Equal(48, MainWindow.ShownView(other));
                var otherItems = other.Items;
                w.SetView(160);
                Assert.Equal(160, MainWindow.ShownView(other));
                Assert.Same(otherItems, other.Items); // updated in place: not reloaded, not navigated
                Assert.Equal(projects, other.CurrentPath);
                Assert.Equal(256, MainWindow.ShownView(tabs.Single(v => v.CurrentPath == pictures))); // a different folder: untouched
                Assert.Equal(160, w.Settings.ViewFor(AppSettings.ViewKey(projects)));
                Assert.Equal(0, w.Settings.ViewTileSize); // the default never moved

                // Reset this folder's view
                Assert.True(w.BuildViewMenu().Items.OfType<MenuItem>().Single(m => ((string)m.Header).StartsWith("Reset this folder's view")).IsEnabled);
                w.ResetFolderView();
                Assert.Equal(0, Shown(w));
                Assert.Equal(0, MainWindow.ShownView(other));
                Assert.False(w.Settings.HasOwnView(AppSettings.ViewKey(projects)));
                Assert.False(w.BuildViewMenu().Items.OfType<MenuItem>().Single(m => ((string)m.Header).StartsWith("Reset this folder's view")).IsEnabled);
                Assert.Equal(160, w.Settings.ViewFor(AppSettings.ViewKey(legacyFolder))); // others keep theirs
                Assert.True(Directory.Exists(projects) && File.Exists(Path.Combine(projects, "file.txt")));
                Sta.Render(w, "folder-views");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Default_view_and_reset_all_in_Settings_and_views_survive_a_restart()
    {
        using var t = new TempDir();
        var own = t.Dir("Own");
        var none = t.Dir("None");
        File.WriteAllText(Path.Combine(own, "a.txt"), "a");
        File.WriteAllText(Path.Combine(none, "b.txt"), "b");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                Go(w, own);
                w.SetView(96);
                Go(w, none);

                // Settings: a new default changes folders without a view of their own only
                var dlg = new SettingsWindow(w, w.Settings, templates: new List<Core.Templates.FileTemplate>());
                dlg.DefaultView.SelectedItem = dlg.DefaultView.Items.OfType<ComboBoxItem>().Single(i => (int)i.Tag == 256);
                Assert.True(dlg.TrySave());
                w.ReapplyViewsForTest(); // what closing Settings does
                Assert.Equal(256, Shown(w));
                Go(w, own);
                Assert.Equal(96, Shown(w));
                Assert.Equal(256, AppSettings.Load().ViewTileSize);
                Assert.Equal(96, AppSettings.Load().ViewFor(AppSettings.ViewKey(own)));
            }
            finally { w.Close(); }

            // restart: a new window from the saved settings opens each folder in its view
            var w2 = new MainWindow(AppSettings.Load(), own);
            Sta.ShowOffscreen(w2);
            try
            {
                Sta.WaitUntil(() => w2.View?.CurrentPath == own, "restart");
                Assert.Equal(96, Shown(w2));
                Go(w2, none);
                Assert.Equal(256, Shown(w2));

                // Settings → Reset all folder views: asks first; No changes nothing
                var dlg = new SettingsWindow(w2, w2.Settings, templates: new List<Core.Templates.FileTemplate>());
                int asked = -1;
                dlg.ConfirmResetViews = n => { asked = n; return false; };
                Assert.Contains("saved", (string)dlg.ResetViewsButton.Content);
                dlg.ResetViewsButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(asked >= 1);
                Assert.True(w2.Settings.HasOwnView(AppSettings.ViewKey(own)));
                dlg.ConfirmResetViews = _ => true;
                dlg.ResetViewsButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Empty(w2.Settings.FolderViews);
                Assert.Empty(AppSettings.Load().FolderViews); // saved straight away
                Assert.False(dlg.ResetViewsButton.IsEnabled);
                dlg.Close();
                w2.ReapplyViewsForTest();
                Go(w2, own);
                Assert.Equal(256, Shown(w2)); // the default now
                Assert.True(File.Exists(Path.Combine(own, "a.txt")) && File.Exists(Path.Combine(none, "b.txt")));
            }
            finally { w2.Close(); }
        });
    }

    [Fact]
    public void A_folder_renamed_in_Fyltrix_keeps_its_view()
    {
        using var t = new TempDir();
        var old = t.Dir("Before");
        File.WriteAllText(Path.Combine(old, "x.txt"), "x");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                Go(w, old);
                w.SetView(160);
                Go(w, t.Path);
                Sta.WaitUntil(() => w.View!.Items.Any(i => i.Name == "Before"), "listing");
                w.CommitRename(w.View!, w.View.Items.Single(i => i.Name == "Before"), "After");
                Sta.WaitUntil(() => Directory.Exists(t["After"]), "renamed");
                Assert.Equal(160, w.Settings.ViewFor(AppSettings.ViewKey(t["After"])));
                Assert.False(w.Settings.HasOwnView(AppSettings.ViewKey(old)));
                Go(w, t["After"]);
                Assert.Equal(160, Shown(w));
            }
            finally { w.Close(); }
        });
    }
}
