using System.Diagnostics;
using Fyltrix.Core;
using Fyltrix.Launcher;
using Fyltrix.Updates;

namespace Fyltrix.UI;

public sealed partial class MainWindow
{
    private ManualUpdateSession? manualUpdates;
    private UpdateWindow? updateWindow;
    private Task updateWorker = Task.CompletedTask;
    internal bool UpdateWorkerRunning => !updateWorker.IsCompleted;
    internal Action<UpdateWindow>? PresentUpdatesForTest { get; set; }
    internal Func<ProcessStartInfo, Task>? StartUpdateWorkerForTest { get; set; }

    internal UpdateWindow OpenUpdates(ManualUpdateSession? session = null)
    {
        if (updateWindow is { IsLoaded: true }) { updateWindow.Activate(); return updateWindow; }
        manualUpdates ??= session ?? ManualUpdateSession.Create();
        var dialog = new UpdateWindow(this, manualUpdates); updateWindow = dialog;
        dialog.Closed += (_, _) => { if (ReferenceEquals(updateWindow, dialog)) updateWindow = null; };
        if (PresentUpdatesForTest != null) PresentUpdatesForTest(dialog); else dialog.Show();
        return dialog;
    }
    internal async Task<bool> InstallUpdateAsync(ManualUpdateSession session)
    {
        Dispatcher.VerifyAccess();
        if (!session.CanInstall || UpdateWorkerRunning || !session.BeginInstall()) return false;
        try
        {
            var problem = UpdateCloseProblem();
            if (problem != null) { session.SetMessage(problem); return false; }
            if (!UpdateProtocol.Supported(session.LayoutRoot!) || ReleaseLayout.Resolve(session.LayoutRoot!)?.Executable.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) != true)
            { session.SetMessage("The running installation changed. Restart Fyltrix before preparing an update."); return false; }
            var result = await UpdateHandover.PrepareAsync(session.Stage!, session.LayoutRoot!);
            if (result.Preparation == null) { session.SetMessage("The update could not be prepared. Check that this copy is writable and the download is still available. Nothing was installed."); return false; }
            var prepared = result.Preparation;
            if (!IsLoaded)
            {
                await UpdateHandover.RequestCloseAsync(prepared, () => false);
                return false;
            }
            try
            {
                var start = UpdateWorker.StartInfo(prepared, notifyFailure: true);
                updateWorker = StartUpdateWorkerForTest?.Invoke(start) ?? StartWorker(start);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                await UpdateHandover.RequestCloseAsync(prepared, () => false);
                Diagnostics.Log("Start updater", ex); session.SetMessage("Windows could not start the updater. Fyltrix remains open."); return false;
            }
            _ = ObserveWorkerAsync();
            var closed = await UpdateHandover.RequestCloseAsync(prepared, () => !updateWorker.IsCompleted && TryCloseForUpdate());
            if (closed == HandoverFailure.None) return true;
            session.SetMessage(UpdateCloseProblem() ?? "Installation cancelled. Fyltrix remains open. The previous helper may take up to 30 seconds to exit before retrying.");
            return false;
        }
        finally { session.EndInstall(); }
    }
    private static Task StartWorker(ProcessStartInfo start)
    {
        var process = Process.Start(start) ?? throw new IOException("Updater process was not created.");
        return WaitAsync(process);
        static async Task WaitAsync(Process process) { using (process) await process.WaitForExitAsync(); }
    }
    private async Task ObserveWorkerAsync()
    {
        try { await updateWorker; }
        catch (Exception ex) { Diagnostics.Log("Updater process", ex); }
        if (IsLoaded) updateWindow?.Refresh();
    }
}
