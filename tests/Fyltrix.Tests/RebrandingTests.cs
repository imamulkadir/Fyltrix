using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Fyltrix.Core;
using Fyltrix.Core.Sharing;
using Fyltrix.Core.Tools;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class RebrandingTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [Fact]
    public void Application_and_assembly_use_Fyltrix_while_protocol_rule_and_icon_stay_compatible()
    {
        var assembly = typeof(MainWindow).Assembly;
        Assert.Equal("Fyltrix", ProductBranding.Name);
        Assert.Equal("Fyltrix", assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        Assert.Equal("Fyltrix", assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title);
        Assert.Equal("Fyltrix", assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company);
        Assert.Equal("Fyltrix", assembly.GetName().Name);
        Assert.Equal("Fyltrix.UI", typeof(MainWindow).Namespace);
        var version = assembly.GetName().Version!;
        Assert.Equal(new Version(assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version), version);
        Assert.Equal(Diagnostics.Version.Split('-')[0], $"{version.Major}.{version.Minor}.{version.Build}");
        Assert.Equal("filelens.app", PreviewUrls.Host);
        Assert.Equal("FileLens file sharing", ShareFirewall.RuleName);
        using var icon = assembly.GetManifestResourceStream("Assets/Fyltrix.ico")!;
        Assert.DoesNotContain("Assets/FileLens.ico", assembly.GetManifestResourceNames());
        Assert.Equal("77AE6883F66A7FD51A9E0B81C51A2E1AEE7974229FC4E6DB1519DD737479D907", Convert.ToHexString(SHA256.HashData(icon)));
        using var html = new StreamReader(assembly.GetManifestResourceStream("Viewer/viewer.html")!);
        Assert.Contains("<title>Fyltrix viewer</title>", html.ReadToEnd());
    }

    [Theory]
    [InlineData("Fyltrix", true)]
    [InlineData("fyltrix", true)]
    [InlineData("FileLens", true)]
    [InlineData("FILELENS", true)]
    [InlineData("testhost", true)]
    [InlineData("UnrelatedApp", false)]
    public void Shared_archive_cache_preserves_running_legacy_and_current_instances(string processName, bool expected)
    {
        Assert.Equal(expected, Core.Archives.ArchiveBrowser.IsApplicationProcessName(processName, "testhost"));
    }

    [Fact]
    public void Existing_archive_cache_location_is_retained_without_migration()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileLens", "ArchiveCache"), Core.Archives.ArchiveBrowser.CacheBase);
    }

    private static (string First, string Second, JsonObject Json) LegacyProfile(TempDir t)
    {
        var first = t.Dir("Projects/FileLens"); // a user's old folder name must not be rebranded or moved
        var second = t.Dir("Pictures");
        t.File("Projects/FileLens/note.txt", "legacy data"); t.File("Pictures/photo.txt", "legacy data");
        var editor = t.File("IDE tools/legacy editor.exe", "");
        var profile = new Dictionary<string, object?>
        {
            ["Theme"] = 1, ["ShowHidden"] = true, ["ShowExtensions"] = false, ["ShowTabIcons"] = false,
            ["ShowFileOperationLabels"] = true, ["ShowSecondaryToolbarLabels"] = true,
            ["Favourites"] = new[] { first }, ["RecentFolders"] = new[] { first },
            ["OpenTabs"] = new[] { first, second }, ["ActiveTab"] = 1, ["RestoreTabs"] = true,
            ["StartupFolder"] = first, ["NewTabOpens"] = 1, ["ConfirmCloseTabs"] = true,
            ["WindowLeft"] = 82.5, ["WindowTop"] = 64.5, ["WindowWidth"] = 1375, ["WindowHeight"] = 875,
            ["WindowMaximized"] = false, ["SidebarWidth"] = 240, ["PreviewWidth"] = 480,
            ["PreviewVisible"] = false, ["PreviewAtStartup"] = 0, ["RememberFolderViews"] = true,
            ["FolderViews"] = new Dictionary<string, int> { [AppSettings.ViewKey(first)] = 160, [AppSettings.ViewKey(second)] = 96 },
            ["DefaultSort"] = "Size", ["DefaultSortAscending"] = false,
            ["HiddenToolbarButtons"] = new[] { "newfolder", "favourite" }, ["ToolbarExtras"] = new[] { "Active shares…" },
            ["HiddenMenuActions"] = new[] { "deletepermanently" }, ["HiddenNewTypes"] = new[] { ".bmp" },
            ["HiddenQuickAccess"] = new[] { "Music" }, ["CollapsedSections"] = new[] { "Recent" },
            ["ShowQuickAccess"] = true, ["ShowFavourites"] = true, ["ShowRecent"] = true,
            ["RememberSidebarState"] = true, ["TrackRecent"] = true, ["SearchScope"] = 1,
            ["SearchFinds"] = 1, ["FilterWhileTyping"] = false, ["GroupByDateFolders"] = new[] { first },
            ["HtmlScripts"] = false, ["OpenArchivesAsFolders"] = true,
            ["ArchiveMaxTotalGB"] = 32, ["ArchiveMaxRatio"] = 500, ["ArchivePreviewMaxMB"] = 256,
            ["FfmpegPath"] = t["Tools/ffmpeg.exe"], ["AutomationEnabled"] = false,
            ["SavedSearches"] = new[] { new SearchCriteria { Title = "Legacy search", Name = "*.txt", Tag = "work" } },
            ["Ides"] = new[] { new IdeEntry { Id = "legacy-ide", Name = "Legacy IDE", Program = editor, Arguments = "--folder {path}", Enabled = true, IsCustom = true } },
            ["CustomActions"] = new[] { new CustomAction { Name = "Legacy action", Program = editor, Arguments = "{paths}", ForFiles = true, ForFolders = true } },
        };
        var json = JsonSerializer.SerializeToNode(profile)!.AsObject();
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(Path.Combine(AppSettings.Folder, "settings.json"), json.ToJsonString());
        return (first, second, json);
    }

    [Fact]
    public void Existing_profile_round_trips_every_legacy_value_without_a_new_configuration_directory()
    {
        using var t = new TempDir();
        var previous = AppSettings.Folder;
        AppSettings.Folder = t["AppData/FileLens"];
        try
        {
            var legacy = LegacyProfile(t);
            var settings = AppSettings.Load();
            settings.Save();
            var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(AppSettings.Folder, "settings.json")))!.AsObject();
            foreach (var (key, expected) in legacy.Json)
                Assert.True(JsonNode.DeepEquals(expected, saved[key]), "Preserved legacy setting: " + key);
            Assert.Equal(160, settings.ViewFor(legacy.First)); Assert.Equal(96, settings.ViewFor(legacy.Second));
            Assert.True(File.Exists(t["Projects/FileLens/note.txt"]));
            Assert.False(Directory.Exists(t["AppData/Fyltrix"]));
        }
        finally { AppSettings.Folder = previous; }
    }

    [Fact]
    public void Restored_tabs_native_window_title_and_settings_use_new_brand_with_old_preferences()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            var previous = AppSettings.Folder;
            bool hideExtensions = FileItem.HideExtensions;
            AppSettings.Folder = t["AppData/FileLens"];
            try
            {
                var legacy = LegacyProfile(t);
                var w = new MainWindow(AppSettings.Load(), null);
                Sta.ShowOffscreen(w);
                try
                {
                    Sta.WaitUntil(() => w.View!.Items.Any(i => i.Name == "photo.txt"), "restored active tab");
                    Assert.Equal(2, w.TabCountForTest);
                    Assert.EndsWith(" – Fyltrix", w.Title);
                    var native = new StringBuilder(1024);
                    GetWindowText(new WindowInteropHelper(w).Handle, native, native.Capacity);
                    Assert.EndsWith(" – Fyltrix", native.ToString()); // Windows uses this title for taskbar/Alt+Tab
                    Assert.Contains(legacy.First, w.Settings.Favourites); Assert.Contains(legacy.First, w.Settings.RecentFolders);
                    Assert.Equal(96, w.View!.TileSize); Assert.Equal(160, w.Settings.ViewFor(legacy.First));
                    Assert.True(w.Settings.ShowFileOperationLabels); Assert.Single(w.Settings.Ides);
                    Sta.Render(w, "fyltrix-restored-tabs");
                    var dialog = new SettingsWindow(w, w.Settings, templates: new List<Core.Templates.FileTemplate>(), host: w.SettingsHostFor());
                    Sta.ShowOffscreen(dialog);
                    try
                    {
                        dialog.SelectTab("Appearance"); Sta.DoEvents(100);
                        var labels = VisualTree.FindDescendants<TextBlock>(dialog).Select(b => b.Text).ToArray();
                        Assert.Contains(labels, s => s.Contains("Fyltrix"));
                        Assert.DoesNotContain(labels, s => s.Contains("FileLens"));
                        Sta.Render(dialog, "fyltrix-settings");
                    }
                    finally { dialog.Close(); }
                }
                finally { w.Settings.ConfirmCloseTabs = false; w.Close(); } // cleanup never opens a confirmation
            }
            finally { FileItem.HideExtensions = hideExtensions; AppSettings.Folder = previous; }
        });
    }
}
