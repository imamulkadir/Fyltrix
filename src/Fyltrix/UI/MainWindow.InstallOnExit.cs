using System.ComponentModel;
using Fyltrix.Core;
using Fyltrix.Updates;

namespace Fyltrix.UI;

public sealed partial class MainWindow
{
    private bool _exitUpdatePreparing, _exitUpdateClosing, _skipExitUpdateOnce, _systemShutdown;
    private CancellationTokenSource? exitUpdateCancellation;
    internal Task ExitUpdateTask { get; private set; } = Task.CompletedTask;
    internal Func<ManualUpdateSession, Task<bool>>? InstallOnExitForTest;

    // This runs after all ordinary Closing handlers, including tab/sharing choices and external vetoes.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_exitUpdatePreparing && !_updateCloseRequested && !_systemShutdown) { e.Cancel = true; return; }
        base.OnClosing(e);
        if (e.Cancel || _updateCloseRequested || _systemShutdown) return;
        if (_skipExitUpdateOnce) { _skipExitUpdateOnce = false; return; }
        if (UpdateWorkerRunning || manualUpdates is not { CanInstall: true } session || !ExitUpdateAllowed(session)) return;
        // Never repeat a previous interrupted/finished attempt automatically. Keep evidence for explicit review/recovery.
        if (File.Exists(Path.Combine(session.Stage!.Directory, UpdateHandover.RequestName))
            || File.Exists(Path.Combine(session.Stage.Directory, "update-result.json"))) return;
        e.Cancel = true; _exitUpdatePreparing = true;
        ExitUpdateTask = Dispatcher.InvokeAsync(() => InstallAtExitAsync(session)).Task.Unwrap();
    }
    private bool ExitUpdateAllowed(ManualUpdateSession session) => !_systemShutdown && IsLoaded
        && Settings.UpdateMode == UpdateMode.InstallOnExit && session.Stage != null && session.LayoutRoot != null
        && UpdateCloseProblem() == null;

    private async Task InstallAtExitAsync(ManualUpdateSession session)
    {
        bool closed = false;
        using var cancel = new CancellationTokenSource(); exitUpdateCancellation = cancel;
        _exitUpdateClosing = true;
        try
        {
            if (!UpdateWorkerRunning && ExitUpdateAllowed(session))
                closed = await (InstallOnExitForTest?.Invoke(session) ?? InstallUpdateAsync(session, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { Diagnostics.Log("Install on exit", ex); }
        finally
        {
            _exitUpdateClosing = false; _exitUpdatePreparing = false; exitUpdateCancellation = null;
            if (!closed && IsLoaded)
            {
                _skipExitUpdateOnce = true;
                session.SetMessage("The update was postponed. Close Fyltrix again to exit without installing; the download is retained.");
                SetStatus("Update postponed. Close again to exit without installing.");
            }
        }
    }
    internal void SuppressExitUpdateForShutdown()
    { _systemShutdown = true; exitUpdateCancellation?.Cancel(); }
}
