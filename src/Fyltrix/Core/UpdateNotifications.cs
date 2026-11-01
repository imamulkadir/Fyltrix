using Fyltrix.Updates;

namespace Fyltrix.Core;

/// <summary>One cancellable scheduled check; no background downloads, installation, service or settings writes.</summary>
internal sealed class UpdateNotifications : IDisposable
{
    internal ManualUpdateSession Session { get; }
    private readonly UpdateNotificationStore store;
    private readonly Func<DateTimeOffset> now;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private CancellationTokenSource? schedule;
    private bool automaticCheck;
    private UpdateMode mode = UpdateMode.ManualOnly;
    internal UpdateNotificationHistory History { get; private set; }
    internal string? BannerVersion { get; private set; }
    internal Task Loop { get; private set; } = Task.CompletedTask;
    internal Task PendingSave { get; private set; } = Task.CompletedTask;
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
    }
    internal void Configure(UpdateMode value)
    {
        schedule?.Cancel(); schedule?.Dispose(); schedule = null;
        if (automaticCheck) Session.Cancel();
        mode = Enum.IsDefined(value) ? value : ApplicationDefaults.Updates;
        if (mode == UpdateMode.ManualOnly) { BannerVersion = null; Changed?.Invoke(); return; }
        // Future imported modes safely behave as notify-only until their implementation is approved.
        schedule = new CancellationTokenSource(); Loop = RunAsync(schedule.Token);
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
    }
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
        schedule?.Cancel(); schedule?.Dispose(); schedule = null;
        if (automaticCheck) Session.Cancel();
        Session.CheckCompleted -= Checked;
    }
}
