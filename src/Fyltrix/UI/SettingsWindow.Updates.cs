using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;

namespace Fyltrix.UI;

public sealed partial class SettingsWindow
{
    internal ComboBox UpdateModeChoice { get; private set; } = null!;
    internal Button CheckUpdatesButton { get; private set; } = null!;
    internal TextBlock UpdatesStatus { get; private set; } = null!;
    internal Button CancelUpdateDownloadButton { get; private set; } = null!;

    private void BuildUpdatesPage(AppSettings settings, SettingsHost? host, List<Func<string?>> saves)
    {
        // The shared tool template has a single-line strip; wrap Settings' nine tabs locally.
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(DockPanel.DockProperty, Dock.Top);
        border.SetValue(Border.BorderBrushProperty, Theme.Brush("Line"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        var headers = new FrameworkElementFactory(typeof(WrapPanel));
        headers.SetValue(Panel.IsItemsHostProperty, true);
        border.AppendChild(headers); dock.AppendChild(border);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 0));
        dock.AppendChild(content);
        Tabs.Template = new ControlTemplate(typeof(TabControl)) { VisualTree = dock };
        var page = Page("Updates");
        page.Children.Add(Dialogs.Heading("Application updates"));
        page.Children.Add(Dialogs.Note($"Current version: {Diagnostics.Version}"));
        UpdateModeChoice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 310, Margin = new Thickness(0, 8, 0, 8) };
        foreach (var (label, mode) in new[] {
            ("Manual only", UpdateMode.ManualOnly), ("Notify only (recommended)", UpdateMode.NotifyOnly),
            ("Automatic download, manual installation", UpdateMode.DownloadAutomatically), ("Automatic download and install on exit", UpdateMode.InstallOnExit) })
            UpdateModeChoice.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        var selected = Enum.IsDefined(settings.UpdateMode) ? settings.UpdateMode : ApplicationDefaults.Updates;
        UpdateModeChoice.SelectedItem = UpdateModeChoice.Items.OfType<ComboBoxItem>().Single(item => (UpdateMode)item.Tag == selected);
        page.Children.Add(Dialogs.Field("Update mode", UpdateModeChoice));
        page.Children.Add(Dialogs.Note("Checks run shortly after startup, then every six hours. Automatic downloads need an update-enabled installation. Install on exit applies a verified download only when you close safely, without restarting. Busy work, tools, sharing or automation postpone it. Notify only remains the default."));
        UpdatesStatus = Dialogs.Note(""); page.Children.Add(UpdatesStatus);
        CancelUpdateDownloadButton = Dialogs.Button("Cancel download");
        CancelUpdateDownloadButton.HorizontalAlignment = HorizontalAlignment.Left;
        CancelUpdateDownloadButton.Click += (_, _) => host?.CancelUpdateDownload?.Invoke();
        void Refresh()
        {
            if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(Refresh); return; }
            UpdatesStatus.Text = host?.ReadUpdateStatus?.Invoke() ?? "No update check has been performed in this window.";
            CancelUpdateDownloadButton.Visibility = host?.IsUpdateDownloading?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed;
        }
        CheckUpdatesButton = Dialogs.Button("Check now");
        CheckUpdatesButton.HorizontalAlignment = HorizontalAlignment.Left;
        CheckUpdatesButton.IsEnabled = host?.CheckForUpdates != null;
        CheckUpdatesButton.Click += async (_, _) =>
        {
            if (host?.CheckForUpdates == null) return;
            CheckUpdatesButton.IsEnabled = false;
            try { await host.CheckForUpdates(); }
            finally { CheckUpdatesButton.IsEnabled = true; Refresh(); }
        };
        page.Children.Add(CheckUpdatesButton);
        page.Children.Add(CancelUpdateDownloadButton);
        if (host?.ShowUpdateDetails != null)
        {
            var details = Dialogs.Button("Release notes and download options");
            details.HorizontalAlignment = HorizontalAlignment.Left;
            details.Click += (_, _) => host.ShowUpdateDetails();
            page.Children.Add(details);
            page.Children.Add(Dialogs.Note("Close Settings before choosing Install and restart in the update window."));
        }
        host?.SubscribeUpdateStatus?.Invoke(Refresh);
        Closed += (_, _) => host?.UnsubscribeUpdateStatus?.Invoke(Refresh);
        Refresh();
        saves.Add(() => { settings.UpdateMode = (UpdateMode)((ComboBoxItem)UpdateModeChoice.SelectedItem).Tag; return null; });
    }
}
