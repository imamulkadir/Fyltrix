using System.Text.Json;
using Fyltrix.Core;

namespace Fyltrix.Tests;

[Collection("UI")]
public class UpdateProfileTests
{
    [Fact]
    public void Trial_loader_reads_existing_settings_without_changing_the_source()
    {
        using var t = new TempDir(); string original = AppSettings.Folder;
        try
        {
            AppSettings.Folder = t.Path;
            var saved = new AppSettings { ShowHidden = true, ShowExtensions = false, ShowFileOperationLabels = true,
                ShowSecondaryToolbarLabels = false, Theme = ThemeMode.Light, Favourites = [t.Dir("favourite")], OpenTabs = [t.Path] };
            saved.Save(); byte[] bytes = File.ReadAllBytes(t["settings.json"]);
            var loaded = AppSettings.LoadForUpdateTrial();
            Assert.True(loaded.ShowHidden); Assert.False(loaded.ShowExtensions); Assert.Equal(saved.Favourites, loaded.Favourites);
            Assert.Equal(ThemeMode.Light, loaded.Theme); Assert.Equal(bytes, File.ReadAllBytes(t["settings.json"]));
        }
        finally { AppSettings.Folder = original; }
    }
    [Theory]
    [InlineData("{not json")] [InlineData("{\"FutureSchema\":2}")] [InlineData("{\"ShowHidden\":\"invalid\"}")]
    public void Invalid_or_unknown_trial_settings_are_rejected_without_defaulting_or_saving(string content)
    {
        using var t = new TempDir(); string original = AppSettings.Folder;
        try
        {
            AppSettings.Folder = t.Path; t.File("settings.json", content);
            Assert.Throws<JsonException>(() => AppSettings.LoadForUpdateTrial());
            Assert.Equal(content, File.ReadAllText(t["settings.json"]));
        }
        finally { AppSettings.Folder = original; }
    }
}
