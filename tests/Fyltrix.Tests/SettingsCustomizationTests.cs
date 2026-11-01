using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fyltrix.Core;
using Fyltrix.Preview;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>Settings → Appearance, Navigation, Sidebar, Views, Context menu, Toolbar, Search: each option does what it says.</summary>
[Collection("UI")]
public class SettingsCustomizationTests
{
    private static void Go(MainWindow w, string path)
    {
        w.View!.Navigate(path);
        Sta.WaitUntil(() => w.View.CurrentPath == path && (path == FolderView.ThisPC ? w.View.Items.All(i => i.IsDrive) : true), "navigate to " + path, 20000);
        Sta.DoEvents(150);
    }

    private static MainWindow Open(TempDir t, string folder, Action<AppSettings> configure)
    {
        var w = MainWindowUiTests.OpenWindow(t, folder, configure);
        Sta.DoEvents(100);
        return w;
    }

    private static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public void Extensions_can_be_hidden_without_changing_real_names()
    {
        using var t = new TempDir();
        t.File("report.pdf", "x");
        t.Dir("folder.with.dots");
        Sta.Run(() =>
        {
            var w = Open(t, t.Path, s => s.ShowExtensions = false);
            try
            {
                var view = w.View!;
                Sta.WaitUntil(() => view.Items.Any(i => i.Name == "report.pdf"), "listing");
                var file = view.Items.Single(i => i.Name == "report.pdf");
                Assert.Equal("report", file.DisplayName);
                Assert.Equal("folder.with.dots", view.Items.Single(i => i.IsDirectory && i.Name.StartsWith("folder")).DisplayName); // folders keep dots
                Sta.DoEvents(200);
                var row = (FrameworkElement)view.List.ItemContainerGenerator.ContainerFromItem(file);
                Assert.Contains(VisualTree.FindDescendants<TextBlock>(row), b => b.Text == "report" && b.IsVisible);
                view.SelectPaths(new[] { file.FullPath });
                view.BeginRename();
                Assert.Equal("report.pdf", file.EditName); // renaming still edits the real name

                file.IsEditing = false;
                w.Settings.ShowExtensions = true;
                w.ApplySettings();
                Sta.WaitUntil(() => view.Items.Any(i => i.DisplayName == "report.pdf"), "extensions back");
            }
            finally { FileItem.HideExtensions = false; w.Close(); }
        });
    }

    [Fact]
    public void Tab_icons_and_preview_at_startup_follow_settings()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            // the preview pane was open last time, but Settings says "always hidden": not shown, and no preview engine started
            var w = Open(t, t.Path, s => { s.PreviewVisible = true; s.PreviewAtStartup = PreviewStartup.Hide; s.ShowTabIcons = false; });
            try
            {
                Assert.False(w.Settings.PreviewVisible);
                Assert.False(w.PreviewForTest.IsVisible);
                Sta.DoEvents(500);
                Assert.False(w.PreviewForTest.IsEngineStarted);
                Assert.All(w.TabIconsVisibility, v => Assert.Equal(Visibility.Collapsed, v));
                w.Settings.ShowTabIcons = true;
                w.ApplySettings(relist: false);
                Assert.All(w.TabIconsVisibility, v => Assert.Equal(Visibility.Visible, v));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Startup_folder_restore_tabs_and_new_tab_location()
    {
        using var t = new TempDir();
        var start = t.Dir("Start here");
        var old1 = t.Dir("Old one");
        var old2 = t.Dir("Old two");
        Sta.Run(() =>
        {
            AppSettings.Folder = t.Dir("_settings");
            // tabs not restored: only the start folder opens
            var s = new AppSettings { PreviewVisible = false, RestoreTabs = false, StartupFolder = start, OpenTabs = new() { old1, old2 } };
            var w = new MainWindow(s, null);
            Sta.ShowOffscreen(w);
            try
            {
                Sta.WaitUntil(() => w.View?.CurrentPath == start, "start folder");
                Assert.Equal(1, w.TabCountForTest);

                w.Settings.NewTabOpens = NewTabTarget.ThisPC;
                w.OpenNewTab();
                Sta.WaitUntil(() => w.View!.IsThisPC, "new tab on This PC");
                w.Settings.NewTabOpens = NewTabTarget.StartupFolder;
                w.OpenNewTab();
                Sta.WaitUntil(() => w.View!.CurrentPath == start, "new tab on the start folder");
                w.Settings.NewTabOpens = NewTabTarget.CurrentFolder;
                Go(w, old1);
                w.OpenNewTab();
                Sta.WaitUntil(() => w.View!.CurrentPath == old1 && w.TabCountForTest == 4, "new tab on the current folder");

                // several tabs closing at once: asks, and "No" keeps them
                w.Settings.ConfirmCloseTabs = true;
                int asked = 0;
                w.ConfirmForTest = n => { asked = n; return false; };
                w.CloseOtherTabsForTest();
                Assert.Equal(3, asked);
                Assert.Equal(4, w.TabCountForTest);
                w.ConfirmForTest = _ => true;
                w.CloseOtherTabsForTest();
                Assert.Equal(1, w.TabCountForTest);
            }
            finally { w.Settings.ConfirmCloseTabs = false; w.Close(); }

            // restored tabs (the default) and "This PC" as the start location
            var s2 = new AppSettings { PreviewVisible = false, OpenTabs = new() { old1, old2 }, ActiveTab = 1 };
            var w2 = new MainWindow(s2, null);
            Sta.ShowOffscreen(w2);
            try
            {
                Sta.WaitUntil(() => w2.View?.CurrentPath == old2, "restored tabs");
                Assert.Equal(2, w2.TabCountForTest);
            }
            finally { w2.Close(); }
            var s3 = new AppSettings { PreviewVisible = false, RestoreTabs = false, StartupFolder = "This PC" };
            var w3 = new MainWindow(s3, null);
            Sta.ShowOffscreen(w3);
            try { Sta.WaitUntil(() => w3.View?.IsThisPC == true, "This PC at startup"); }
            finally { w3.Close(); }
        });
    }

    [Fact]
    public void Sidebar_sections_can_be_hidden_folded_and_recent_tracking_stopped()
    {
        using var t = new TempDir();
        var a = t.Dir("Alpha");
        Sta.Run(() =>
        {
            var w = Open(t, t.Path, s => { s.HiddenQuickAccess = new() { "Music", "Videos" }; s.Favourites = new() { a }; });
            try
            {
                var quick = w.SidebarRows("Quick access").Select(r => r.Text).ToList();
                Assert.DoesNotContain("Music", quick);
                Assert.DoesNotContain("Videos", quick);
                Assert.Contains("Desktop", quick);

                // fold Favourites by its title: rows go, the title stays, the state is saved
                Assert.Single(w.SidebarRows("Favourites"));
                w.ToggleSection("Favourites");
                Assert.Empty(w.SidebarRows("Favourites"));
                Assert.Contains("FAVOURITES", w.SidebarSections);
                Assert.Contains("Favourites", AppSettings.Load().CollapsedSections);
                w.ToggleSection("Favourites");
                Assert.Single(w.SidebarRows("Favourites"));

                w.Settings.ShowQuickAccess = false;
                w.Settings.ShowFavourites = false;
                w.ApplySettings(relist: false);
                Assert.DoesNotContain("QUICK ACCESS", w.SidebarSections);
                Assert.DoesNotContain("FAVOURITES", w.SidebarSections);
                Assert.Contains("THIS PC", w.SidebarSections);
                Assert.True(Directory.Exists(a)); // only hidden from view

                // Recent tracking off: visiting folders adds nothing
                w.Settings.TrackRecent = false;
                int before = w.Settings.RecentFolders.Count;
                Go(w, a);
                Assert.Equal(before, w.Settings.RecentFolders.Count);
                w.Settings.TrackRecent = true;
                Go(w, t.Path);
                Assert.Equal(t.Path, w.Settings.RecentFolders[0]);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Folder_view_memory_can_be_switched_off_and_default_sorting_applies_to_new_tabs()
    {
        using var t = new TempDir();
        var a = t.Dir("A");
        var b = t.Dir("B");
        File.WriteAllText(Path.Combine(a, "small.txt"), "1");
        File.WriteAllText(Path.Combine(a, "big.txt"), new string('x', 5000));
        Sta.Run(() =>
        {
            var w = Open(t, t.Path, _ => { });
            try
            {
                Go(w, a);
                w.SetView(160); // remembered for A
                Go(w, b);
                Assert.Equal(0, MainWindow.ShownView(w.View!));

                w.Settings.RememberFolderViews = false; // one view everywhere: the default
                w.ReapplyViewsForTest();
                Go(w, a);
                Assert.Equal(0, MainWindow.ShownView(w.View!));
                w.SetView(96); // now changes the default, for every folder
                Assert.Equal(96, w.Settings.ViewTileSize);
                Go(w, b);
                Assert.Equal(96, MainWindow.ShownView(w.View!));
                Assert.Equal(160, w.Settings.FolderViews[AppSettings.ViewKey(a)]); // kept for when it is switched back on

                w.Settings.RememberFolderViews = true;
                Go(w, a);
                Assert.Equal(160, MainWindow.ShownView(w.View!));

                w.Settings.DefaultSort = "Size";
                w.Settings.DefaultSortAscending = false;
                w.AddTabForTest(a);
                Sta.WaitUntil(() => w.View!.CurrentPath == a && w.View.Items.Count == 2, "new tab");
                Assert.Equal(SortColumn.Size, w.View!.Sort);
                Assert.False(w.View.Ascending);
                Assert.Equal("big.txt", w.View.Items[0].Name); // largest first
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Hidden_context_menu_actions_are_left_out_and_separators_stay_tidy()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = Open(t, t.Path, s => s.HiddenMenuActions = new() { "copypath", "properties", "compress", "cut", "copy", "paste", "rename", "delete", "deletepermanently" });
            try
            {
                var view = w.View!;
                Sta.WaitUntil(() => view.Items.Any(i => i.Name == "a.txt"), "listing");
                var menu = w.BuildContextMenu(view, view.Items.Where(i => i.Name == "a.txt").ToList());
                var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
                foreach (var gone in new[] { "Copy path", "Properties", "Compress", "Cut", "Copy", "Rename", "Delete", "Delete permanently" })
                    Assert.DoesNotContain(gone, headers);
                Assert.Contains("Open", headers);
                var items = menu.Items.Cast<object>().ToList();
                Assert.IsNotType<Separator>(items[0]);
                Assert.IsNotType<Separator>(items[^1]);
                for (int i = 1; i < items.Count; i++) Assert.False(items[i] is Separator && items[i - 1] is Separator, "two separators in a row");

                // the background menu too, and every action shown again after "Restore default context menu"
                var bg = w.BuildContextMenu(view, new List<FileItem>());
                Assert.DoesNotContain("Paste", bg.Items.OfType<MenuItem>().Select(m => m.Header as string));
                var dlg = new SettingsWindow(w, w.Settings, templates: new List<Core.Templates.FileTemplate>(), host: w.SettingsHostFor());
                Assert.False(dlg.MenuActionChecks["properties"].IsChecked);
                dlg.RestoreDefaultMenu();
                Assert.True(dlg.TrySave());
                Assert.Empty(w.Settings.HiddenMenuActions);
                Assert.Contains("Properties", w.BuildContextMenu(view, view.Items.Where(i => i.Name == "a.txt").ToList()).Items.OfType<MenuItem>().Select(m => m.Header as string));
                dlg.Close();
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Toolbar_buttons_can_be_hidden_added_and_restored()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = Open(t, t.Path, s => { s.HiddenToolbarButtons = new() { "cut", "copy", "paste", "rename", "delete" }; s.ToolbarExtras = new() { "New tab" }; });
            try
            {
                var bar = w.ToolbarForTest;
                Assert.All(bar.Where(b => b.Id is "cut" or "copy" or "paste" or "rename" or "delete"), b => Assert.Equal(Visibility.Collapsed, b.Element.Visibility));
                Assert.All(bar.Where(b => b.Id is "undo" or "newfolder" or "view"), b => Assert.Equal(Visibility.Visible, b.Element.Visibility));
                // no separator first, last, or twice in a row among what is visible
                var shown = bar.Where(b => b.Element.Visibility == Visibility.Visible).ToList();
                Assert.NotNull(shown[0].Id);
                Assert.NotNull(shown[^1].Id);
                for (int i = 1; i < shown.Count; i++) Assert.False(shown[i].Id == null && shown[i - 1].Id == null, "two separators in a row");

                // an added command is a button that runs it
                var extra = (Button)w.ToolbarExtrasForTest.Children.Cast<UIElement>().Single();
                int tabs = w.TabCountForTest;
                Click(extra);
                Sta.WaitUntil(() => w.TabCountForTest == tabs + 1, "the added button opened a tab");

                // Settings lists every button and the commands that can be added; defaults are tested separately.
                var dlg = new SettingsWindow(w, w.Settings, templates: new List<Core.Templates.FileTemplate>(), host: w.SettingsHostFor());
                Assert.Equal(18, dlg.ToolbarChecks.Count);
                Assert.False(dlg.ToolbarChecks["cut"].IsChecked);
                Assert.Contains("New tab", dlg.ToolbarCommandChoice!.Items.OfType<string>());
                Assert.DoesNotContain(dlg.ToolbarCommandChoice.Items.OfType<string>(), c => c.StartsWith("Go to "));
                foreach (var cb in dlg.ToolbarChecks.Values) cb.IsChecked = true;
                dlg.ToolbarExtrasList!.Items.Clear();
                Assert.True(dlg.TrySave());
                dlg.Close();
                w.ApplySettings(relist: false);
                Assert.All(w.ToolbarForTest.Where(b => b.Id != null), b => Assert.Equal(Visibility.Visible, b.Element.Visibility));
                Assert.Empty(w.ToolbarExtrasForTest.Children);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Search_can_find_only_folders_and_filtering_can_wait_for_Enter()
    {
        using var t = new TempDir();
        t.File("report one.txt");
        t.Dir("report folder");
        t.File("sub/report deep.txt");
        t.Dir("sub/report deep folder");
        Sta.Run(() =>
        {
            var w = Open(t, t.Path, s => { s.SearchFinds = Core.Tools.ItemTypeFilter.FoldersOnly; s.FilterWhileTyping = false; });
            try
            {
                var view = w.View!;
                Sta.WaitUntil(() => view.Items.Count >= 3, "listing");
                int all = view.List.Items.Count;
                w.TypeSearch("report");
                Sta.DoEvents(400);
                Assert.Equal(all, view.List.Items.Count); // typing doesn't filter

                w.PressEnterInSearch(); // Enter searches subfolders: folders only
                Sta.WaitUntil(() => view.IsSearchResults && !view.IsSearching, "search");
                Assert.Equal(new[] { "report deep folder", "report folder" }, view.Items.Select(i => i.Name).OrderBy(n => n));
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Settings_window_saves_every_page_and_checks_the_start_folder()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            var w = Open(t, t.Path, _ => { });
            try
            {
                var dlg = new SettingsWindow(w, w.Settings, templates: new List<Core.Templates.FileTemplate>(), host: w.SettingsHostFor());
                Assert.Equal(new[] { "Appearance", "Navigation", "Sidebar", "Views", "Context menu", "Toolbar", "Search", "Advanced", "Updates" },
                    dlg.Tabs.Items.OfType<TabItem>().Select(i => (string)i.Header));
                Sta.ShowOffscreen(dlg);
                foreach (var tab in dlg.Tabs.Items.OfType<TabItem>())
                {
                    dlg.Tabs.SelectedItem = tab;
                    Sta.DoEvents(80);
                    Sta.Render(dlg, "settings-" + ((string)tab.Header).Replace(' ', '-').ToLowerInvariant());
                }
                var startCombo = dlg.StartupFolderBox;
                startCombo.Text = t["does not exist"];
                Assert.False(dlg.TrySave()); // a missing start folder is reported, nothing saved
                startCombo.Text = "This PC";
                Assert.True(dlg.TrySave());
                Assert.Equal("This PC", AppSettings.Load().StartupFolder);
                dlg.Close();
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Light_theme_has_every_colour_of_the_dark_one()
    {
        try
        {
            Theme.Use(ThemeMode.Light);
            Assert.True(Theme.IsLight);
            Assert.Equal("#FFFFFFFF", Theme.Brush("Bg1").Color.ToString());
            var light = Theme.Colors.Keys.ToHashSet();
            Theme.Use(ThemeMode.Dark);
            Assert.False(Theme.IsLight);
            Assert.True(light.SetEquals(Theme.Colors.Keys));
            Assert.Equal("#FF1B1B1F", Theme.Brush("Bg1").Color.ToString());
            Theme.Use(ThemeMode.System);
            Assert.Equal(Theme.WindowsUsesLightTheme(), Theme.IsLight);
        }
        finally { Theme.Use(ThemeMode.Dark); }
    }
}

/// <summary>The resource fixes: the preview engine only while needed, background tabs don't re-list, bounded caches.</summary>
[Collection("UI")]
public class ResourceTests
{
    [Fact]
    public void Preview_engine_starts_when_shown_and_is_released_when_hidden()
    {
        using var t = new TempDir();
        t.File("note.txt", "hello preview");
        var before = MainWindow.PreviewReleaseAfter;
        Sta.Run(() =>
        {
            MainWindow.PreviewReleaseAfter = TimeSpan.FromMilliseconds(400);
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => s.PreviewVisible = false);
            try
            {
                Sta.DoEvents(500);
                Assert.False(w.PreviewForTest.IsEngineStarted); // hidden pane: no WebView2 at all

                w.SetPreviewVisibleForTest(true);
                Sta.WaitUntil(() => w.PreviewForTest.IsReady, "engine started when shown", 30000);
                w.View!.SelectPaths(new[] { t["note.txt"] });
                Sta.WaitUntil(() => w.PreviewForTest.CurrentPath == t["note.txt"], "preview");

                w.SetPreviewVisibleForTest(false);
                Sta.DoEvents(150);
                Assert.True(w.PreviewForTest.IsEngineStarted); // kept for a moment: hiding and showing again stays instant
                Sta.WaitUntil(() => !w.PreviewForTest.IsEngineStarted, "released after the delay", 5000);

                w.SetPreviewVisibleForTest(true); // and it works again afterwards
                Sta.WaitUntil(() => w.PreviewForTest.IsReady, "started again", 30000);
                w.View.SelectPaths(Array.Empty<string>());
                w.View.SelectPaths(new[] { t["note.txt"] });
                Sta.WaitUntil(() =>
                {
                    if (!w.PreviewForTest.IsReady) return false;
                    var task = w.PreviewForTest.Web.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                    while (!task.IsCompleted) Sta.DoEvents(10);
                    return task.Result.Contains("hello preview");
                }, "the file shows after the engine restarted", 30000);
            }
            finally { w.Close(); MainWindow.PreviewReleaseAfter = before; }
        });
    }

    [Fact]
    public void A_background_tab_refreshes_once_when_shown_not_on_every_change()
    {
        using var t = new TempDir();
        var watched = t.Dir("watched");
        File.WriteAllText(Path.Combine(watched, "first.txt"), "1");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, watched);
            try
            {
                var background = w.View!;
                Sta.WaitUntil(() => background.Items.Count == 1, "listing");
                w.AddTabForTest(t.Path); // the watched folder's tab goes to the background
                Sta.WaitUntil(() => w.View != background, "other tab");
                var listed = background.Items;

                File.WriteAllText(Path.Combine(watched, "second.txt"), "2");
                Sta.DoEvents(1500);
                Assert.Same(listed, background.Items); // not re-listed while hidden

                w.ActivateTabForTest(w.TabViews.ToList().IndexOf(background));
                Sta.WaitUntil(() => background.Items.Any(i => i.Name == "second.txt"), "refreshed when shown");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void The_icon_cache_is_bounded()
    {
        // .exe icons are cached per file; visiting many folders full of programs must not grow the cache without limit
        for (int i = 0; i < ShellInfo.MaxIcons + 50; i++) ShellInfo.Icon($@"Z:\nowhere\program {i}.exe", false);
        Assert.InRange(ShellInfo.CachedIcons, 1, ShellInfo.MaxIcons);
    }

    [Fact]
    public void Markdown_code_blocks_are_still_highlighted_with_libraries_loaded_on_demand()
    {
        using var t = new TempDir();
        t.File("doc.md", "# Title\n\n```csharp\nclass C { int x = 1; }\n```\n");
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            h.ShowAndWaitForText(t["doc.md"], s => s.Contains("class C"));
            Sta.WaitUntil(() => h.Eval("String(document.querySelectorAll('pre code span[class^=hljs-]').length > 0)") == "true", "highlighted code");
            Assert.Equal("true", h.Eval("String(!!window.marked && !window.XLSX && !window.docx)")); // only what this page needed
        });
    }
}
