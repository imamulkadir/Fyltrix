using System.ComponentModel;
using System.Windows;
using Fyltrix.Core;
using Fyltrix.Core.Sharing;
using WinForms = System.Windows.Forms;

namespace Fyltrix.UI;

/// <summary>
/// Share lifecycle around the main window. Shares belong to the Fyltrix process: closing the main window while
/// something is shared asks whether to keep sharing in the background. If so, the window hides and a notification
/// area icon stays until the last share ends (then Fyltrix exits) or the user stops sharing from it. Sharing never
/// continues without that icon, and never after the process has exited.
/// </summary>
public sealed partial class MainWindow
{
    private WinForms.NotifyIcon? _tray;
    private SharesWindow? _sharesWindow;
    private bool _exitForReal, _inBackground;

    /// <summary>Tests answer the "still sharing" question here ("keep" / "stop" / "cancel") instead of a modal dialog.</summary>
    internal Func<string>? CloseChoiceForTest;
    /// <summary>Tests don't put an icon in the real notification area.</summary>
    internal bool NoTrayForTest;
    internal bool InBackground => _inBackground;

    internal void OpenShares()
    {
        if (_sharesWindow is { IsLoaded: true }) { _sharesWindow.Activate(); return; }
        _sharesWindow = new SharesWindow(IsVisible ? this : null);
        _sharesWindow.Closed += (_, _) => _sharesWindow = null;
        _sharesWindow.Show();
    }

    private void ShareWith(IEnumerable<string> paths) => new ShareWindow(this, paths).Show();

    private void InitSharing()
    {
        var host = ShareHost.Current;
        host.Changed += (_, _) => Dispatcher.BeginInvoke(OnSharesChanged);
        int paused = host.Sessions.Count(s => s.State == ShareState.Interrupted);
        if (paused > 0)
            SetStatus($"{paused} share{(paused == 1 ? " was" : "s were")} paused when {ProductBranding.Name} closed. Tools → Active shares… to resume or stop {(paused == 1 ? "it" : "them")}.");
    }

    /// <summary>Returns true when the close was turned into "keep sharing in the background".</summary>
    private bool KeepSharingInsteadOfClosing(CancelEventArgs e)
    {
        if (_exitForReal) return false;
        var active = ShareHost.Current.ActiveSessions;
        if (active.Count == 0) return false;

        string choice = "cancel";
        if (CloseChoiceForTest != null) choice = CloseChoiceForTest();
        else
        {
            var d = new DialogWindow(this, "Sharing is still on", 500);
            d.AddContent(Dialogs.Heading(active.Count == 1 ? $"'{active[0].Title}' is still being shared." : $"{active.Count} shares are still active."));
            d.AddContent(Dialogs.Note($"Keep sharing in the background: {ProductBranding.Name} closes its window but stays in the notification area (next to the clock) " +
                                      "until the shares end. You can stop them from that icon at any time.\n\nStop sharing and exit: the links stop working now."));
            d.AddButton("Keep sharing in the background", () => { choice = "keep"; d.DialogResult = true; }, primary: true);
            d.AddButton("Stop sharing and exit", () => { choice = "stop"; d.DialogResult = true; });
            d.AddButton("Cancel", () => d.DialogResult = false, cancel: true);
            d.ShowDialog();
        }

        switch (choice)
        {
            case "keep":
                e.Cancel = true;
                SaveSettings();
                foreach (var w in OwnedWindows.OfType<Window>().ToList()) w.Close();
                Hide();
                ShowTray();
                return true;
            case "stop":
                ShareHost.Current.StopAll();
                return false;
            default:
                e.Cancel = true;
                return true;
        }
    }

    private void ShowTray()
    {
        _inBackground = true;
        if (NoTrayForTest) return;
        if (_tray == null)
        {
            _tray = new WinForms.NotifyIcon { Icon = AppIcon.ForTray(), Text = ProductBranding.Name };
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Active shares…", null, (_, _) => Dispatcher.BeginInvoke(OpenShares));
            menu.Items.Add($"Open {ProductBranding.Name}", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Stop sharing and exit", null, (_, _) => Dispatcher.BeginInvoke(() => { ShareHost.Current.StopAll(); ExitFromTray(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(OpenShares);
        }
        UpdateTrayText();
        _tray.Visible = true;
        _tray.ShowBalloonTip(5000, $"{ProductBranding.Name} is still sharing", "Your shares stay available until they end. Right-click this icon to see or stop them.", WinForms.ToolTipIcon.Info);
    }

    private void UpdateTrayText()
    {
        if (_tray == null) return;
        var active = ShareHost.Current.ActiveSessions;
        var next = active.OrderBy(s => s.ExpiresUtc).FirstOrDefault();
        var text = active.Count == 0 ? ProductBranding.Name : $"{ProductBranding.Name} · sharing {active.Count} item set{(active.Count == 1 ? "" : "s")}" + (next != null ? $" · next ends {next.ExpiresUtc.ToLocalTime():t}" : "");
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
    }

    private void OnSharesChanged()
    {
        UpdateTrayText();
        // in the background only for sharing: when the last share ends, Fyltrix exits
        if (_inBackground && !IsVisible && ShareHost.Current.ActiveSessions.Count == 0)
        {
            _tray?.ShowBalloonTip(3000, "Sharing has ended", $"{ProductBranding.Name} has closed.", WinForms.ToolTipIcon.Info);
            ExitFromTray();
        }
    }

    private void RestoreFromTray()
    {
        _inBackground = false;
        if (_tray != null) _tray.Visible = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitForReal = true;
        _sharesWindow?.Close();
        Close();
    }

    private void DisposeSharing()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        ShareHost.Current.Dispose();
    }
}
