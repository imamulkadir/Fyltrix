using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.UI;
using Fyltrix.Updates;

namespace Fyltrix.Tests;

[Collection("UI")]
public sealed class UpdateNotificationTests
{
    private sealed class Clock
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal sealed record Request(TimeSpan Duration, TaskCompletionSource Completion);
        private readonly Channel<Request> requests = Channel.CreateUnbounded<Request>();
        internal Task Delay(TimeSpan duration, CancellationToken token)
        {
            var task = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => task.TrySetCanceled(token));
            requests.Writer.TryWrite(new(duration, task));
            return task.Task;
        }
        internal Task<Request> Next() => requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        internal void Advance(Request request) { Now += request.Duration; request.Completion.TrySetResult(); }
    }

    [Theory]
    [InlineData("true")] [InlineData("500")] [InlineData("\"invalid\"")]
    [InlineData("{}")] [InlineData("[]")] [InlineData("null")]
    public void Invalid_update_mode_never_discards_existing_preferences(string value)
    {
        using var temp = new TempDir(); string original = AppSettings.Folder;
        try
        {
            AppSettings.Folder = temp.Path;
            temp.File("settings.json", "{\"ShowHidden\":true,\"WindowWidth\":1492,\"Favourites\":[\"X:\\\\keep\"],\"UpdateMode\":" + value + "}");
            var loaded = AppSettings.Load();
            Assert.True(loaded.ShowHidden); Assert.Equal(1492, loaded.WindowWidth); Assert.Single(loaded.Favourites);
            Assert.Equal(ApplicationDefaults.Updates, loaded.UpdateMode);
        }
        finally { AppSettings.Folder = original; }
    }
    [Fact]
    public void Existing_profile_gets_only_the_approved_update_default_and_mode_roundtrips()
    {
        using var temp = new TempDir(); string original = AppSettings.Folder;
        try
        {
            AppSettings.Folder = temp.Path;
            temp.File("settings.json", "{\"ShowHidden\":true,\"ShowSecondaryToolbarLabels\":false}");
            var saved = AppSettings.Load(); Assert.Equal(UpdateMode.NotifyOnly, saved.UpdateMode);
            foreach (var mode in Enum.GetValues<UpdateMode>())
            {
                saved.UpdateMode = mode; saved.Save(); var loaded = AppSettings.Load();
                Assert.Equal(mode, loaded.UpdateMode); Assert.True(loaded.ShowHidden); Assert.False(loaded.ShowSecondaryToolbarLabels);
            }
        }
        finally { AppSettings.Folder = original; }
    }
    [Fact]
    public async Task Startup_and_six_hour_checks_notify_once_without_downloading_or_saving_preferences()
    {
        using var temp = new TempDir(); using var feed = new ManualUpdateTests.Feed(); var clock = new Clock();
        int checks = 0, downloads = 0;
        using var session = feed.Session(check: _ => { checks++; return Task.FromResult(new ReleaseCheck(CheckStatus.Available, feed.Release)); },
            download: (_, _, _) => { downloads++; return Task.FromResult(new DownloadResult(DownloadFailure.InvalidResponse)); });
        string profile = temp.File("settings.json", "original user preferences"); var original = File.ReadAllBytes(profile);
        var store = new UpdateNotificationStore(temp["updater-only"]);
        using var model = new UpdateNotifications(session, store, () => clock.Now, clock.Delay);
        model.Configure(UpdateMode.NotifyOnly);
        var startup = await clock.Next(); Assert.Equal(TimeSpan.FromSeconds(60), startup.Duration); Assert.Equal(0, checks);
        clock.Advance(startup); var interval = await clock.Next(); await model.PendingSave;
        Assert.Equal(1, checks); Assert.Equal("0.2.0", model.BannerVersion); Assert.Equal(0, downloads);
        Assert.Equal(TimeSpan.FromHours(6), interval.Duration);
        model.Dismiss(); clock.Advance(interval); _ = await clock.Next(); await model.PendingSave;
        Assert.Equal(2, checks); Assert.Null(model.BannerVersion); Assert.Equal(0, downloads);
        Assert.Equal(original, File.ReadAllBytes(profile));
        Assert.Equal("0.2.0", store.Load().LastNotifiedVersion);
        model.Configure(UpdateMode.ManualOnly); await model.Loop;
    }
    [Fact]
    public async Task Notification_history_prevents_repeat_after_restart_but_settings_keeps_available_version()
    {
        using var temp = new TempDir(); using var feed = new ManualUpdateTests.Feed(); var store = new UpdateNotificationStore(temp.Path);
        using (var session = feed.Session())
        using (var model = new UpdateNotifications(session, store))
        {
            model.Configure(UpdateMode.NotifyOnly); await session.CheckAsync(); await model.PendingSave;
            Assert.Equal("0.2.0", model.BannerVersion);
        }
        using var next = feed.Session(); using var restarted = new UpdateNotifications(next, store);
        restarted.Configure(UpdateMode.NotifyOnly); await next.CheckAsync(); await restarted.PendingSave;
        Assert.Null(restarted.BannerVersion); Assert.Equal("0.2.0", restarted.History.LastAvailableVersion);
    }
    [Fact]
    public async Task Manual_mode_has_no_schedule_and_untrusted_releases_never_notify()
    {
        using var temp = new TempDir(); using var feed = new ManualUpdateTests.Feed();
        using var session = feed.Session(verify: (_, _) => Task.FromResult(new ManifestVerification(VerificationFailure.InvalidSignature)));
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(temp.Path));
        model.Configure(UpdateMode.ManualOnly); Assert.True(model.Loop.IsCompleted);
        await session.CheckAsync(); await model.PendingSave; Assert.Null(model.BannerVersion);
        Assert.Null(model.History.LastAvailableVersion); Assert.False(session.CanDownload);
    }
    [Fact]
    public async Task Rate_limit_prevents_requests_until_server_backoff_expires()
    {
        using var temp = new TempDir(); using var feed = new ManualUpdateTests.Feed(); var clock = new Clock(); int checks = 0;
        using var session = feed.Session(check: _ => { checks++; return Task.FromResult(new ReleaseCheck(CheckStatus.RateLimited, RetryAfter: clock.Now.AddHours(10))); });
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(temp.Path), () => clock.Now, clock.Delay);
        model.Configure(UpdateMode.NotifyOnly); clock.Advance(await clock.Next());
        clock.Advance(await clock.Next()); var backoff = await clock.Next();
        Assert.Equal(1, checks); Assert.Equal(TimeSpan.FromHours(4), backoff.Duration);
        model.Configure(UpdateMode.ManualOnly); await model.Loop; await model.PendingSave;
    }
    [Fact]
    public async Task Backoff_survives_restart_and_also_blocks_an_early_manual_request()
    {
        using var temp = new TempDir(); using var feed = new ManualUpdateTests.Feed();
        var store = new UpdateNotificationStore(temp.Path); int calls = 0;
        store.Save(new UpdateNotificationHistory(LastChecked: DateTimeOffset.UtcNow, RetryAfter: DateTimeOffset.UtcNow.AddHours(10)));
        using var session = feed.Session(check: _ => { calls++; return Task.FromResult(new ReleaseCheck(CheckStatus.Available, feed.Release)); });
        using var model = new UpdateNotifications(session, store);
        await session.CheckAsync(); Assert.Equal(0, calls); Assert.Contains("wait", session.Message);
        Assert.Null(model.BannerVersion);
    }
    [Theory]
    [InlineData(UpdateMode.DownloadAutomatically)] [InlineData(UpdateMode.InstallOnExit)]
    public async Task Future_imported_modes_remain_notify_only_in_this_stage(UpdateMode mode)
    {
        using var temp = new TempDir(); using var feed = new ManualUpdateTests.Feed(); int downloads = 0;
        using var session = feed.Session(download: (_, _, _) => { downloads++; return Task.FromResult(new DownloadResult(DownloadFailure.InvalidResponse)); });
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(temp.Path));
        model.Configure(mode); await session.CheckAsync(); await model.PendingSave;
        Assert.Equal("0.2.0", model.BannerVersion); Assert.Equal(0, downloads); Assert.Null(session.Stage);
    }
    [Fact]
    public async Task Disabling_automatic_checks_cancels_only_the_active_automatic_check()
    {
        using var temp = new TempDir(); using var feed = new ManualUpdateTests.Feed(); var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = feed.Session(check: async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new ReleaseCheck(CheckStatus.NoRelease); });
        using var model = new UpdateNotifications(session, new UpdateNotificationStore(temp.Path), () => clock.Now, clock.Delay);
        model.Configure(UpdateMode.NotifyOnly); clock.Advance(await clock.Next()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        model.Configure(UpdateMode.ManualOnly); await model.Loop.WaitAsync(TimeSpan.FromSeconds(3)); await model.PendingSave;
        Assert.False(session.Busy); Assert.Null(model.BannerVersion);
    }
    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("not json")]
    public void Notification_storage_never_replaces_unrelated_or_corrupt_files(string content)
    {
        using var temp = new TempDir(); temp.File("notification-state.json", content);
        var store = new UpdateNotificationStore(temp.Path); Assert.Null(store.Load().LastChecked);
        Assert.ThrowsAny<Exception>(() => store.Save(new())); Assert.Equal(content, File.ReadAllText(temp["notification-state.json"]));
    }
    [Fact]
    public void Saving_manual_mode_applies_immediately_without_changing_unrelated_preferences()
    {
        using var temp = new TempDir(); using var feed = new ManualUpdateTests.Feed();
        Sta.Run(() =>
        {
            var owner = MainWindowUiTests.OpenWindow(temp, temp.Path);
            owner.Settings.ShowHidden = true;
            using var session = feed.Session(); using var model = new UpdateNotifications(session, new UpdateNotificationStore(temp["metadata"]));
            try
            {
                owner.StartUpdateNotifications(model);
                var settings = new SettingsWindow(owner, owner.Settings, templates: [], host: owner.SettingsHostFor());
                Sta.ShowOffscreen(settings); settings.SelectTab("Updates");
                settings.UpdateModeChoice.SelectedItem = settings.UpdateModeChoice.Items.OfType<ComboBoxItem>().Single(item => (UpdateMode)item.Tag == UpdateMode.ManualOnly);
                Assert.Equal(UpdateMode.NotifyOnly, owner.Settings.UpdateMode); // Editing is not applying.
                Assert.True(settings.TrySave()); owner.ApplyUpdatePreferences(); Sta.DoEvents(50);
                Assert.Equal(UpdateMode.ManualOnly, owner.Settings.UpdateMode);
                Assert.True(model.Loop.IsCompleted); Assert.True(owner.Settings.ShowHidden);
                Assert.Equal(UpdateMode.ManualOnly, AppSettings.Load().UpdateMode);
                settings.Close();
            }
            finally { owner.Close(); }
        });
    }
    [Fact]
    public void Settings_and_banner_show_the_shared_verified_status_without_opening_a_popup()
    {
        using var temp = new TempDir(); temp.File("file.txt", "test"); using var feed = new ManualUpdateTests.Feed();
        Sta.Run(() =>
        {
            var owner = MainWindowUiTests.OpenWindow(temp, temp.Path);
            using var session = feed.Session(); using var model = new UpdateNotifications(session, new UpdateNotificationStore(temp["updates"]));
            try
            {
                owner.StartUpdateNotifications(model);
                var check = session.CheckAsync(); Sta.WaitUntil(() => check.IsCompleted, "verified update");
                Assert.Equal(Visibility.Visible, owner.UpdateBannerVisibility); Assert.Contains("0.2.0", owner.UpdateBannerText);
                Assert.Empty(owner.OwnedWindows.Cast<Window>());
                Sta.Render(owner, "update-notification-banner");
                var settings = new SettingsWindow(owner, owner.Settings, templates: [], host: owner.SettingsHostFor());
                Sta.ShowOffscreen(settings); settings.SelectTab("Updates"); Sta.DoEvents(100);
                Assert.Contains("0.2.0", settings.UpdatesStatus.Text);
                var settingsContent = (FrameworkElement)settings.Content;
                foreach (TabItem tab in settings.Tabs.Items)
                {
                    var bounds = tab.TransformToAncestor(settingsContent).TransformBounds(new Rect(tab.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= settingsContent.ActualWidth, "Every Settings tab must remain visible.");
                }
                Assert.All(settings.UpdateModeChoice.Items.OfType<ComboBoxItem>().Where(item => (UpdateMode)item.Tag is UpdateMode.DownloadAutomatically or UpdateMode.InstallOnExit), item => Assert.False(item.IsEnabled));
                Sta.Render(settings, "settings-update-notifications"); settings.Close();
                model.Dismiss(); Assert.Equal(Visibility.Collapsed, owner.UpdateBannerVisibility);
                Sta.WaitUntil(() => model.PendingSave.IsCompleted, "metadata persistence");
            }
            finally { owner.Close(); }
        });
    }
}
