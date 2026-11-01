using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class ApplicationDefaultsTests
{
    private static CheckBox Labels(SettingsWindow window, string text)
    {
        window.SelectTab("Toolbar");
        return ((StackPanel)((ScrollViewer)window.Tabs.SelectedContent).Content).Children.OfType<CheckBox>()
            .Single(c => (c.Content as TextBlock)?.Text == text);
    }
    private static Button Button(MainWindow w, string id) => (Button)w.ToolbarForTest.Single(b => b.Id == id).Element;
    private static Visibility Label(Button button) => ((StackPanel)button.Content).Children[1].Visibility;

    [Fact]
    public void Fresh_settings_and_partial_imports_use_portable_defaults_without_overriding_saved_values()
    {
        var defaults = ApplicationDefaults.Create();
        Assert.Equal(ThemeMode.System, defaults.Theme);
        Assert.False(defaults.ShowFileOperationLabels);
        Assert.False(defaults.ShowSecondaryToolbarLabels);
        Assert.Equal(new[] { "newfolder", "favourite", "explorer", "terminal" }, defaults.HiddenToolbarButtons);
        Assert.Equal(new[] { "Active shares…" }, defaults.ToolbarExtras);
        Assert.Empty(defaults.Favourites); Assert.Empty(defaults.RecentFolders); Assert.Empty(defaults.OpenTabs);
        Assert.Empty(defaults.StartupFolder); Assert.Empty(defaults.FolderViews); Assert.Empty(defaults.Ides);
        Assert.True(defaults.RestoreTabs); Assert.True(defaults.ShowExtensions); Assert.True(defaults.TrackRecent);
        var missing = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal(defaults.HiddenToolbarButtons, missing.HiddenToolbarButtons);
        Assert.False(missing.ShowFileOperationLabels);
        var saved = JsonSerializer.Deserialize<AppSettings>("""
            { "Theme":0, "ShowSecondaryToolbarLabels":true, "ShowFileOperationLabels":true,
              "HiddenToolbarButtons":[], "ToolbarExtras":["New tab"], "ShowHidden":true }
            """)!;
        Assert.Equal(ThemeMode.Dark, saved.Theme); Assert.True(saved.ShowSecondaryToolbarLabels); Assert.True(saved.ShowFileOperationLabels);
        Assert.Empty(saved.HiddenToolbarButtons); Assert.Equal(new[] { "New tab" }, saved.ToolbarExtras); Assert.True(saved.ShowHidden);
        var jsonOptions = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var again = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved, jsonOptions), jsonOptions)!;
        Assert.True(again.ShowFileOperationLabels); Assert.True(again.ShowSecondaryToolbarLabels);
        saved.HiddenToolbarButtons.Add("copy"); saved.ToolbarExtras.Clear();
        Assert.DoesNotContain("copy", ApplicationDefaults.Create().HiddenToolbarButtons);
        Assert.Single(ApplicationDefaults.Create().ToolbarExtras);
    }

    [Fact]
    public void Default_icons_order_and_share_glyph_work_at_normal_and_narrow_widths()
    {
        using var t = new TempDir(); t.File("seed.txt");
        Sta.Run(() =>
        {
            foreach (int width in new[] { 1300, 700 })
            {
                var w = MainWindowUiTests.OpenWindow(t, t.Path, s => s.WindowWidth = width);
                try
                {
                    Assert.Equal(Visibility.Visible, Label(Button(w, "new")));
                    foreach (var id in ApplicationDefaults.FileOperationButtons)
                    {
                        var b = Button(w, id);
                        Assert.Equal(Visibility.Collapsed, Label(b));
                        Assert.NotNull(b.ToolTip); Assert.False(b.IsEnabled);
                        Assert.True(b.MinWidth >= 34); Assert.True(b.MinHeight >= 30);
                    }
                    Assert.False(Button(w, "copy").IsEnabled);
                    w.View!.SelectPaths(new[] { t["seed.txt"] });
                    Assert.True(Button(w, "copy").IsEnabled); Assert.True(Button(w, "rename").IsEnabled);
                    var share = w.ToolbarExtrasForTest.Children.OfType<Button>().Single();
                    Assert.Equal("Active shares…", share.Tag); Assert.Equal("Share", share.ToolTip);
                    Assert.Equal("\uE72D", ((TextBlock)((StackPanel)share.Content).Children[0]).Text);
                    Assert.True(share.IsEnabled);
                    w.UpdateLayout();
                    double X(string id) => Button(w, id).TranslatePoint(new Point(), (UIElement)w.Content).X;
                    Assert.True(X("settings") < X("preview"));
                    // Windows can constrain the requested width to the runner's work area/DPI.
                    // Compare coordinates in the same actual client space as TranslatePoint above.
                    double clientWidth = ((FrameworkElement)w.Content).ActualWidth;
                    Assert.InRange(X("preview") + Button(w, "preview").ActualWidth, clientWidth - 25, clientWidth);
                    Sta.Render(w, "round8-toolbar-" + width);
                }
                finally { w.Close(); }
            }
        });
    }

    [Fact]
    public void Labels_save_and_restore_toolbar_uses_same_baseline_without_resetting_other_preferences()
    {
        using var t = new TempDir(); t.File("seed.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => { s.Theme = ThemeMode.Light; s.HiddenToolbarButtons.Clear(); s.ToolbarExtras = new() { "New tab" }; });
            try
            {
                var dialog = new SettingsWindow(w, w.Settings, templates: new List<Core.Templates.FileTemplate>(), host: w.SettingsHostFor());
                try
                {
                    var operations = Labels(dialog, "Show labels for Cut, Copy, Paste, Rename and Delete");
                    var secondary = Labels(dialog, "Show labels for secondary toolbar buttons");
                    operations.IsChecked = secondary.IsChecked = true;
                    Assert.True(dialog.TrySave()); w.ApplySettings(relist: false, refilter: false);
                    foreach (var id in ApplicationDefaults.FileOperationButtons) Assert.Equal(Visibility.Visible, Label(Button(w, id)));
                    var reloaded = AppSettings.Load();
                    Assert.True(reloaded.ShowFileOperationLabels); Assert.True(reloaded.ShowSecondaryToolbarLabels);
                    Assert.Equal(ThemeMode.Light, reloaded.Theme); Assert.Empty(reloaded.HiddenToolbarButtons);
                    dialog.RestoreToolbarDefaults();
                    Assert.False(operations.IsChecked); Assert.False(secondary.IsChecked);
                    Assert.True(dialog.TrySave()); w.ApplySettings(relist: false, refilter: false);
                    var baseline = ApplicationDefaults.Create();
                    Assert.Equal(baseline.HiddenToolbarButtons, w.Settings.HiddenToolbarButtons);
                    Assert.Equal(baseline.ToolbarExtras, w.Settings.ToolbarExtras);
                    Assert.Equal(ThemeMode.Light, w.Settings.Theme);
                    foreach (var id in ApplicationDefaults.FileOperationButtons) Assert.Equal(Visibility.Collapsed, Label(Button(w, id)));
                    Assert.Equal(Visibility.Visible, Label(Button(w, "new")));
                }
                finally { dialog.Close(); }
            }
            finally { w.Close(); }
        });
    }
}
