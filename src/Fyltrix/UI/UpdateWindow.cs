using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Fyltrix.Core;

namespace Fyltrix.UI;

internal sealed class UpdateWindow : DialogWindow
{
    private readonly MainWindow main;
    private readonly ManualUpdateSession session;
    private readonly TextBlock status = Dialogs.Note("");
    private readonly TextBox notes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 155, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 8) };
    private bool installing;
    private readonly ProgressBar progress = new() { Height = 6, Margin = new Thickness(0, 8, 0, 8) };
    internal Button CheckButton { get; }
    internal Button DownloadButton { get; }
    internal Button InstallButton { get; }
    internal Func<bool>? ConfirmInstallForTest { get; set; }

    internal UpdateWindow(MainWindow owner, ManualUpdateSession session) : base(owner, "Fyltrix updates", 570)
    {
        main = owner; this.session = session;
        AddContent(Dialogs.Heading($"Current version: {session.Current.Text}"));
        AddContent(Dialogs.Note("Manage automatic notifications in Settings → Updates. Downloads and installation require your approval."));
        if (session.LayoutRoot == null)
        {
            AddContent(Dialogs.Note("This copy needs a manual installer before in-app installation is available."));
            var link = new Hyperlink(new Run("Open releases to download the installer"));
            link.Click += (_, _) => Shell.Open("https://github.com/imamulkadir/Fyltrix/releases");
            AddContent(new TextBlock(link) { Margin = new Thickness(0, 6, 0, 8) });
        }
        AddContent(status); AddContent(progress); AddContent(notes);
        CheckButton = AddButton("Check for updates", () => _ = session.CheckAsync());
        DownloadButton = AddButton("Download", () => _ = session.DownloadAsync());
        InstallButton = AddButton("Install and restart", () => _ = InstallAsync(), primary: true);
        AddButton("Close", Close);
        session.Changed += Refresh;
        Closed += (_, _) => { session.Changed -= Refresh; if (!installing) session.Cancel(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
        Refresh();
    }
    internal void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(Refresh); return; }
        status.Text = session.Message; notes.Text = session.Notes;
        progress.Visibility = session.Busy || session.Progress != null ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = session.Busy && session.Progress is not { Total: > 0 };
        progress.Value = session.Progress is { Total: > 0 } p ? 100.0 * p.Received / p.Total : 0;
        notes.Visibility = session.Notes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CheckButton.IsEnabled = !session.Busy && !installing;
        DownloadButton.IsEnabled = session.CanDownload && !installing;
        InstallButton.IsEnabled = session.CanInstall && !installing && !main.UpdateWorkerRunning;
    }
    internal async Task InstallAsync()
    {
        if (!session.CanInstall || installing || main.UpdateWorkerRunning) return;
        if (!(ConfirmInstallForTest?.Invoke() ?? Dialogs.Confirm(this, "Install update", "Fyltrix will close normally, validate the new version using copied settings, and restart after a successful update. Install now?"))) return;
        installing = true; Close(); // Remove this owned tool before the normal-close safety check.
        bool closed;
        try { closed = await main.InstallUpdateAsync(session); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { Diagnostics.Log("Prepare update", ex); session.SetMessage("The update could not be prepared. Nothing was installed."); closed = false; }
        if (!closed && main.IsLoaded) main.OpenUpdates(session);
    }
}
