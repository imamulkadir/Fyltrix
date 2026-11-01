using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.Core.Sharing;
using Fyltrix.UI;
using Fyltrix.Updates;

namespace Fyltrix.Tests;

[Collection("UI")]
public sealed class InstallOnExitTests
{
    [Theory]
    [InlineData(UpdateMode.ManualOnly)] [InlineData(UpdateMode.NotifyOnly)] [InlineData(UpdateMode.DownloadAutomatically)]
    public void Other_modes_close_normally_without_installing(UpdateMode mode) => WithReady((owner, session) =>
    {
        int calls = 0; owner.Settings.UpdateMode = mode;
        owner.InstallOnExitForTest = _ => { calls++; return Task.FromResult(false); };
        owner.Close(); Assert.False(owner.IsVisible); Assert.Equal(0, calls);
    });
    [Fact]
    public void Safe_exit_uses_existing_close_once_and_preserves_tab_confirmation() => WithReady((owner, session) =>
    {
        owner.OpenNewTab(); owner.Settings.ConfirmCloseTabs = true; int questions = 0, calls = 0;
        owner.ConfirmForTest = _ => { questions++; return true; };
        owner.InstallOnExitForTest = _ => { calls++; return Task.FromResult(owner.TryCloseForUpdate()); };
        owner.Close(); Sta.WaitUntil(() => owner.ExitUpdateTask.IsCompleted, "safe exit installation");
        Assert.Equal(1, questions); Assert.Equal(1, calls); Assert.False(owner.IsVisible);
    });
    [Theory]
    [InlineData("tabs")] [InlineData("veto")]
    public void A_cancelled_normal_close_never_starts_preparation(string reason) => WithReady((owner, session) =>
    {
        int calls = 0; owner.InstallOnExitForTest = _ => { calls++; return Task.FromResult(false); };
        CancelEventHandler veto = (_, args) => args.Cancel = true;
        if (reason == "tabs") { owner.OpenNewTab(); owner.Settings.ConfirmCloseTabs = true; owner.ConfirmForTest = _ => false; }
        else owner.Closing += veto;
        owner.Close(); Sta.DoEvents(30); Assert.True(owner.IsVisible); Assert.Equal(0, calls);
        owner.Closing -= veto;
    });
    [Theory]
    [InlineData("work")] [InlineData("automation")] [InlineData("tools")]
    public void Busy_work_or_tools_skip_exit_installation_without_mutating_preferences(string reason) => WithReady((owner, session) =>
    {
        int calls = 0; owner.InstallOnExitForTest = _ => { calls++; return Task.FromResult(false); };
        IDisposable? work = reason == "work" ? UpdateWork.Track() : null;
        Window? tool = reason == "tools" ? new Window { Owner = owner } : null;
        if (tool != null) Sta.ShowOffscreen(tool);
        if (reason == "automation") owner.Settings.AutomationEnabled = true;
        try { owner.Close(); Assert.Equal(0, calls); Assert.Equal(reason == "automation", owner.Settings.AutomationEnabled); }
        finally { work?.Dispose(); tool?.Close(); }
    });
    [Fact]
    public void Windows_session_ending_and_previous_attempts_do_not_install() => WithReady((owner, session) =>
    {
        int calls = 0; owner.InstallOnExitForTest = _ => { calls++; return Task.FromResult(false); };
        owner.SuppressExitUpdateForShutdown(); owner.Close(); Assert.Equal(0, calls);
    });
    [Fact]
    public void Keeping_shares_in_background_and_later_tray_exit_never_install() => WithReady((owner, session) =>
    {
        int calls = 0; bool closed = false; owner.Closed += (_, _) => closed = true;
        owner.NoTrayForTest = true; owner.CloseChoiceForTest = () => "keep";
        owner.InstallOnExitForTest = _ => { calls++; return Task.FromResult(false); };
        var share = ShareHost.Current.Start([Path.Combine(owner.View!.CurrentPath, "file.txt")], new ShareOptions());
        try
        {
            owner.Close(); Assert.True(owner.InBackground); Assert.False(closed); Assert.Equal(0, calls);
            ShareHost.Current.Stop(share); Sta.WaitUntil(() => closed, "background share exit"); Assert.Equal(0, calls);
        }
        finally { owner.CloseChoiceForTest = () => "stop"; if (!closed) ShareHost.Current.Stop(share); }
    });
    [Fact]
    public void No_ready_package_means_an_ordinary_close()
    {
        using var temp = new TempDir(); temp.File("file.txt", "fixture");
        Sta.Run(() =>
        {
            var owner = MainWindowUiTests.OpenWindow(temp, temp.Path);
            owner.Settings.UpdateMode = UpdateMode.InstallOnExit; owner.Settings.ConfirmCloseTabs = false;
            owner.InstallOnExitForTest = _ => throw new InvalidOperationException("No package can authorize installation.");
            owner.Close(); Assert.False(owner.IsVisible);
        });
    }
    [Theory]
    [InlineData("handover.json")] [InlineData("update-result.json")]
    public void Previous_attempt_evidence_is_preserved_and_not_repeated(string name) => WithReady((owner, session) =>
    {
        int calls = 0; owner.InstallOnExitForTest = _ => { calls++; return Task.FromResult(false); };
        string path = Path.Combine(session.Stage!.Directory, name); File.WriteAllText(path, "keep previous evidence");
        owner.Close(); Assert.Equal(0, calls); Assert.Equal("keep previous evidence", File.ReadAllText(path));
    });
    [Fact]
    public void Failed_preparation_keeps_the_app_open_and_next_close_exits_without_retry() => WithReady((owner, session) =>
    {
        int calls = 0; owner.InstallOnExitForTest = _ => { calls++; throw new IOException("injected preparation failure"); };
        owner.Close(); Sta.WaitUntil(() => owner.ExitUpdateTask.IsCompleted, "failed exit preparation");
        Assert.True(owner.IsVisible); Assert.Contains("postponed", session.Message);
        owner.Close(); Assert.False(owner.IsVisible); Assert.Equal(1, calls);
    });
    [Fact]
    public void Duplicate_close_and_late_veto_do_not_authorize_installation() => WithReady((owner, session) =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
        owner.InstallOnExitForTest = async _ => { calls++; await release.Task; return owner.TryCloseForUpdate(); };
        owner.Close(); Sta.DoEvents(30); owner.Close(); Assert.Equal(1, calls);
        CancelEventHandler veto = (_, args) => args.Cancel = true; owner.Closing += veto;
        release.SetResult(); Sta.WaitUntil(() => owner.ExitUpdateTask.IsCompleted, "late close veto");
        Assert.True(owner.IsVisible); Assert.True(session.CanInstall); owner.Closing -= veto;
    });
    [Fact]
    public void Settings_can_save_the_opt_in_without_changing_the_default_or_other_preferences()
    {
        using var temp = new TempDir(); temp.File("file.txt", "fixture");
        Sta.Run(() =>
        {
            var owner = MainWindowUiTests.OpenWindow(temp, temp.Path);
            try
            {
                owner.Settings.ShowHidden = true;
                var window = new SettingsWindow(owner, owner.Settings, templates: [], host: owner.SettingsHostFor());
                Sta.ShowOffscreen(window); window.SelectTab("Updates");
                window.UpdateModeChoice.SelectedItem = window.UpdateModeChoice.Items.OfType<ComboBoxItem>().Single(x => (UpdateMode)x.Tag == UpdateMode.InstallOnExit);
                Assert.Equal(UpdateMode.NotifyOnly, owner.Settings.UpdateMode); Assert.True(window.TrySave());
                Assert.Equal(UpdateMode.InstallOnExit, AppSettings.Load().UpdateMode); Assert.True(owner.Settings.ShowHidden);
                Assert.Equal(UpdateMode.NotifyOnly, new AppSettings().UpdateMode);
                Sta.Render(window, "install-on-exit-settings"); window.Close();
            }
            finally { owner.Settings.ConfirmCloseTabs = false; owner.Close(); }
        });
    }
    [Theory]
    [InlineData("", true, false)] [InlineData("--notify", true, true)] [InlineData("--no-restart", false, false)]
    [InlineData("--notify|--no-restart", false, true)] [InlineData("--no-restart|--notify", false, true)]
    public void Worker_flags_preserve_manual_restart_and_support_safe_exit(string flags, bool restart, bool notify)
    {
        string[] args = [UpdateWorker.ApplyArgument, "request path", "nonce", .. flags.Split('|', StringSplitOptions.RemoveEmptyEntries)];
        Assert.True(App.TryWorkerOptions(args, out bool actualNotify, out bool actualRestart));
        Assert.Equal(restart, actualRestart); Assert.Equal(notify, actualNotify);
    }
    [Theory]
    [InlineData("--bad")] [InlineData("--notify|--notify")] [InlineData("--no-restart|--no-restart")]
    public void Unknown_or_duplicate_worker_flags_are_rejected(string flags)
    { Assert.False(App.TryWorkerOptions([UpdateWorker.ApplyArgument, "path", "nonce", .. flags.Split('|')], out _, out _)); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Next_start_distinguishes_an_installed_version_from_a_previous_failed_attempt(bool failed)
    {
        using var temp = new TempDir(); using var feed = new AutomaticDownloadTests.Feed(temp);
        var ready = (await feed.Download(feed.Release, new Progress<DownloadProgress>(), default)).Staged!;
        File.WriteAllText(Path.Combine(ready.Directory, "update-result.json"), "retained result evidence");
        var store = new UpdateNotificationStore(feed.Root); store.Save(new(PendingStage: Path.GetFileName(ready.Directory)));
        using var session = feed.Session(current: failed ? "0.1.0" : "0.2.0"); using var model = new UpdateNotifications(session, store);
        model.Configure(UpdateMode.ManualOnly); await model.Loop; await model.PendingSave;
        Assert.Equal(failed, session.CanInstall);
        Assert.Contains(failed ? "needs review" : "already installed", session.Message);
        Assert.Equal(failed, store.Load().PendingStage != null);
        Assert.True(File.Exists(ready.PackagePath));
    }

    private static void WithReady(Action<MainWindow, ManualUpdateSession> check)
    {
        using var temp = new TempDir(); temp.File("file.txt", "fixture"); using var feed = new AutomaticDownloadTests.Feed(temp);
        Sta.Run(() =>
        {
            var owner = MainWindowUiTests.OpenWindow(temp, temp.Path); owner.Settings.ConfirmCloseTabs = false;
            using var session = feed.Session(); using var model = new UpdateNotifications(session, new UpdateNotificationStore(feed.Root));
            try
            {
                owner.Settings.UpdateMode = UpdateMode.InstallOnExit; owner.StartUpdateNotifications(model);
                var request = session.CheckAsync(); Sta.WaitUntil(() => request.IsCompleted && model.DownloadTask.IsCompleted && session.CanInstall, "verified ready package");
                check(owner, session);
                Sta.WaitUntil(() => model.PendingSave.IsCompleted, "notification history");
            }
            finally
            {
                owner.Settings.UpdateMode = UpdateMode.ManualOnly; owner.Settings.ConfirmCloseTabs = false;
                if (owner.IsLoaded) owner.Close();
            }
        });
    }
}
