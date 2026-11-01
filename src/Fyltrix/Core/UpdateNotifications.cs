using Fyltrix.Updates;

namespace Fyltrix.Core;

/// <summary>Scheduled verification and optional downloads; never installs or writes application settings.</summary>
internal sealed class UpdateNotifications : IDisposable
{
    internal ManualUpdateSession Session { get; }
    private readonly UpdateNotificationStore store;
    private readonly Func<DateTimeOffset> now;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private CancellationTokenSource? schedule;
    private bool automaticCheck;
    private bool automaticDownload;
    private bool configured;
    private bool disposed;
    private string? attemptedVersion;
    private UpdateMode mode = UpdateMode.ManualOnly;
    private bool DownloadsEnabled => mode is UpdateMode.DownloadAutomatically or UpdateMode.InstallOnExit;
    internal UpdateNotificationHistory History { get; private set; }
    internal string? BannerVersion { get; private set; }
    internal Task Loop { get; private set; } = Task.CompletedTask;
    internal Task PendingSave { get; private set; } = Task.CompletedTask;
    internal Task DownloadTask { get; private set; } = Task.CompletedTask;
    internal event Action? Changed;

    internal UpdateNotifications(ManualUpdateSession session, UpdateNotificationStore store,
        Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        Session = session; this.store = store; now = clock ?? (() => DateTimeOffset.UtcNow); delay = wait ?? ((span, token) => Task.Delay(span, token));
        History = store.Load();
        Session.RestoreBackoff(History.RetryAfter);
        if (History.LastChecked is { } saved && saved > now()) History = History with { LastChecked = null };
        if (ReleaseVersion.Parse(History.LastAvailableVersion) is not { } available || available.CompareTo(Session.Current) <= 0)
            History = History with { LastAvailableVersion = null };
        Session.CheckCompleted += Checked;
        Session.DownloadCompleted += Downloaded;
    }
    internal void Configure(UpdateMode value)
    {
        if (disposed) return;
        value = Enum.IsDefined(value) ? value : ApplicationDefaults.Updates;
        if (configured && mode == value) return;
        configured = true; mode = value;
        schedule?.Cancel(); schedule?.Dispose();
        if (automaticCheck || automaticDownload && !DownloadsEnabled) Session.Cancel();
        if (mode == UpdateMode.ManualOnly) BannerVersion = null;
        schedule = new CancellationTokenSource(); Loop = RestartAsync(Loop, DownloadTask, schedule.Token);
        Changed?.Invoke();
    }
    private async Task RestartAsync(Task previous, Task download, CancellationToken token)
    {
        try
        {
            await previous; await download;
            token.ThrowIfCancellationRequested();
            await InitializeAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task InitializeAsync(CancellationToken token)
    {
        try
        {
            if (History.PendingStage != null && Session.Stage == null && Session.LayoutRoot != null && !Session.Busy)
            {
                if (store.StagePath(History.PendingStage) is { } folder) await Session.RestoreStageAsync(folder, token);
                token.ThrowIfCancellationRequested();
                if (Session.Stage != null) NotifyReady();
                else History = History with { PendingStage = null };
                SaveHistory(); Changed?.Invoke();
            }
            StartAutomaticDownload();
            if (mode != UpdateMode.ManualOnly) await RunAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await delay(ApplicationDefaults.UpdateStartupDelay, token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var due = History.LastChecked.GetValueOrDefault(DateTimeOffset.MinValue) + ApplicationDefaults.UpdateCheckInterval;
                if (Session.RetryAfter is { } retry && retry > due) due = retry;
                // Do not turn an automatic check into cancellation of a completed/active download.
                if (due > now() || Session.Busy || Session.Stage != null)
                {
                    var pause = due > now() ? due - now() : ApplicationDefaults.UpdateCheckInterval;
                    if (pause > ApplicationDefaults.UpdateCheckInterval) pause = ApplicationDefaults.UpdateCheckInterval;
                    await delay(pause, token); continue;
                }
                automaticCheck = true;
                try { await Session.CheckAsync(); }
                finally { automaticCheck = false; }
                await delay(ApplicationDefaults.UpdateCheckInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void Checked()
    {
        string? available = Session.AvailableVersion;
        History = History with { LastChecked = now(), RetryAfter = Session.RetryAfter };
        if (available != null)
        {
            History = History with { LastAvailableVersion = available };
            if (mode != UpdateMode.ManualOnly && History.LastNotifiedVersion != available)
            { BannerVersion = available; History = History with { LastNotifiedVersion = available }; }
        }
        else if (Session.LastCheckStatus is CheckStatus.UpToDate or CheckStatus.NoRelease)
        { BannerVersion = null; History = History with { LastAvailableVersion = null }; }
        var snapshot = History;
        PendingSave = SaveAfterAsync(PendingSave, snapshot);
        Changed?.Invoke();
        attemptedVersion = null;
        StartAutomaticDownload();
    }
    private void StartAutomaticDownload()
    {
        if (disposed || !DownloadsEnabled || !Session.CanDownload || automaticDownload
            || attemptedVersion == Session.AvailableVersion) return;
        attemptedVersion = Session.AvailableVersion;
        // Publish completion before invoking the downloader, including synchronously completing providers.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadTask = completion.Task;
        _ = DownloadAutomaticallyAsync(completion);
    }
    private async Task DownloadAutomaticallyAsync(TaskCompletionSource completion)
    {
        automaticDownload = true;
        try { await Session.DownloadAsync(); }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally { automaticDownload = false; }
        completion.TrySetResult();
    }
    private void Downloaded()
    {
        if (Session.Stage == null) return;
        History = History with { PendingStage = store.StageName(Session.Stage.Directory) };
        NotifyReady(); SaveHistory(); Changed?.Invoke();
    }
    private void NotifyReady()
    {
        var version = Session.Stage!.Verified.Release.Version.Text;
        History = History with { LastAvailableVersion = version };
        if (History.LastReadyNotifiedVersion != version && mode != UpdateMode.ManualOnly)
        { BannerVersion = version; History = History with { LastReadyNotifiedVersion = version }; }
    }
    private void SaveHistory() => PendingSave = SaveAfterAsync(PendingSave, History);
    private async Task SaveAfterAsync(Task previous, UpdateNotificationHistory snapshot)
    {
        await previous.ConfigureAwait(false);
        try { await Task.Run(() => store.Save(snapshot)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { /* Notification history is optional; never reset or rewrite the user's preferences. */ }
    }
    internal void Dismiss() { BannerVersion = null; Changed?.Invoke(); }
    public void Dispose()
    {
        disposed = true;
        schedule?.Cancel(); schedule?.Dispose(); schedule = null;
        if (automaticCheck || automaticDownload) Session.Cancel();
        Session.CheckCompleted -= Checked;
        Session.DownloadCompleted -= Downloaded;
    }
}
