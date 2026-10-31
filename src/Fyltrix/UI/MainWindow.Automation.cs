using Fyltrix.Core.Automation;

namespace Fyltrix.UI;

/// <summary>Automation engine lifetime: runs only when switched on in Settings/Automation, stops with the window.</summary>
public sealed partial class MainWindow
{
    private AutomationEngine? _automation;

    private void RestartAutomation()
    {
        _automation?.Dispose();
        _automation = null;
        if (!Settings.AutomationEnabled) return;
        var engine = new AutomationEngine(Settings.ArchiveLimits());
        engine.Logged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (!e.DryRun) SetStatus($"Automation · {e.Rule}: {System.IO.Path.GetFileName(e.File)} — {e.Outcome}");
        });
        _automation = engine;
        var rules = AutomationStore.Load();
        // watchers and the initial folder listing happen off the UI thread
        Task.Run(() => engine.Start(rules));
    }

    private void OpenAutomation() => new AutomationWindow(this, Settings, () => _automation, RestartAutomation).Show();
}
