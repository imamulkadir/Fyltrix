using Fyltrix.Core;
using Fyltrix.Core.Sharing;

namespace Fyltrix.UI;

public sealed partial class MainWindow
{
    private bool _updateCloseRequested;
    private int _updateCriticalWork, _updatePastes;

    // Deliberately stricter than ordinary closing: do not cancel file work or change sharing/automation preferences.
    internal string? UpdateCloseProblem() => !IsEnabled || OwnedWindows.Count > 0 ? "Close other Fyltrix dialogs and tool windows first."
        : JobWindow.RunningCount > 0 || UpdateWork.RunningCount > 0 || _updateCriticalWork > 0 || _updatePastes > 0 ? "Wait for file operations to finish."
        : Settings.AutomationEnabled || _automation != null ? "Turn off automation before preparing an update."
        : InBackground || ShareHost.Current.ActiveSessions.Count > 0 ? "Stop active shares before preparing an update."
        : null;

    /// <summary>Only for an explicitly requested update. Existing tab confirmations and close cancellation remain authoritative.</summary>
    internal bool TryCloseForUpdate()
    {
        Dispatcher.VerifyAccess();
        if (_updateCloseRequested || UpdateCloseProblem() != null) return false;
        bool closed = false;
        void OnClosed(object? sender, EventArgs args) => closed = true;
        Closed += OnClosed;
        _updateCloseRequested = true;
        try { Close(); return closed; }
        finally { _updateCloseRequested = false; Closed -= OnClosed; }
    }

    private async Task RunUpdateTrackedPasteAsync(string target)
    {
        _updatePastes++;
        try { await PasteIntoAsync(target); }
        finally { _updatePastes--; }
    }
    private IDisposable TrackUpdateWork()
    {
        _updateCriticalWork++;
        return new UpdateWorkScope(() => _updateCriticalWork--);
    }
    private sealed class UpdateWorkScope(Action done) : IDisposable
    {
        public void Dispose() => done();
    }
}
