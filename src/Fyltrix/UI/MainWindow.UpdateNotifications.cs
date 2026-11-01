using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;

namespace Fyltrix.UI;

public sealed partial class MainWindow
{
    internal static bool SuppressAutomaticChecksForTests;
    internal static Func<ManualUpdateSession, UpdateNotifications>? NotificationsFactoryForTests;
    private UpdateNotifications? notifications;
    private bool notificationsStarted;
    private Action? updateStatusChanged;
    private Border? updateBanner;
    private TextBlock? updateBannerText;
    internal Visibility UpdateBannerVisibility => updateBanner?.Visibility ?? Visibility.Collapsed;
    internal string UpdateBannerText => updateBannerText?.Text ?? "";

    private ManualUpdateSession UpdatesSession => manualUpdates ??= ManualUpdateSession.Create();
    private UpdateNotifications Notifications
    {
        get
        {
            if (notifications == null)
            {
                notifications = NotificationsFactoryForTests?.Invoke(UpdatesSession) ?? new UpdateNotifications(UpdatesSession, new UpdateNotificationStore());
                if (!ReferenceEquals(manualUpdates, notifications.Session))
                { manualUpdates?.Dispose(); manualUpdates = notifications.Session; }
                AttachUpdateStatus();
            }
            return notifications;
        }
    }
    private void AttachUpdateStatus()
    {
        notifications!.Changed += RefreshUpdateStatus;
        UpdatesSession.Changed += RefreshUpdateStatus;
    }
    internal void StartUpdateNotifications(UpdateNotifications? model = null)
    {
        if (model != null)
        {
            if (notifications != null) throw new InvalidOperationException("Update notifications already initialized.");
            notifications = model; manualUpdates = model.Session; AttachUpdateStatus();
        }
        notificationsStarted = true;
        Notifications.Configure(Settings.UpdateMode);
    }
    internal void ApplyUpdatePreferences()
    {
        if (notificationsStarted) Notifications.Configure(Settings.UpdateMode);
        RefreshUpdateStatus();
    }
    private UIElement BuildUpdateBanner()
    {
        var row = new DockPanel { Margin = new Thickness(10, 5, 10, 5) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var view = Dialogs.Button("View update"); view.Click += (_, _) => OpenUpdates();
        var later = Dialogs.Button("Later"); later.Click += (_, _) => Notifications.Dismiss();
        var options = Dialogs.Button("Settings"); options.Click += (_, _) => OpenSettings("Updates");
        actions.Children.Add(view); actions.Children.Add(later); actions.Children.Add(options);
        DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
        updateBannerText = Dialogs.Note(""); updateBannerText.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(updateBannerText);
        updateBanner = new Border { Child = row, Background = Theme.Brush("Bg2"), Visibility = Visibility.Collapsed };
        return updateBanner;
    }
    private void RefreshUpdateStatus()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(RefreshUpdateStatus); return; }
        string? version = notifications?.BannerVersion;
        if (updateBanner != null)
        {
            updateBanner.Visibility = version == null || _fullScreen != null ? Visibility.Collapsed : Visibility.Visible;
            updateBannerText!.Text = version == null ? "" : UpdatesSession.Stage != null
                ? $"Fyltrix {version} is ready to install."
                : UpdatesSession.Downloading ? $"Downloading Fyltrix {version}…" : $"Fyltrix {version} is available.";
        }
        updateStatusChanged?.Invoke();
    }
    private string ReadUpdatesStatus()
    {
        var model = Notifications;
        string checkedAt = model.History.LastChecked?.ToLocalTime().ToString("g") ?? "Never";
        string available = model.History.LastAvailableVersion ?? "None recorded";
        return $"Last checked: {checkedAt}\nLast verified available version: {available}\n{UpdatesSession.Message}";
    }
    private async Task CheckUpdatesNowAsync() => await UpdatesSession.CheckAsync();
}
