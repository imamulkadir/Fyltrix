using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Core.Sharing;

namespace Fyltrix.UI;

/// <summary>
/// Share files with a phone or another PC on the same network through a temporary link. If the items are already
/// shared, the existing share is shown (link, time left, Copy / Extend / Stop) instead of a new one being made.
/// Closing the window does not stop sharing; shares run until they expire or are stopped (Tools → Active shares).
/// </summary>
public sealed class ShareWindow : ToolWindow
{
    private readonly ShareHost _host;
    private readonly List<string> _items;
    private readonly CheckBox _lan = new() { Content = "Allow other devices on my local network (Wi-Fi / LAN)" };
    private readonly TextBlock _lanWarning = new()
    {
        Text = "Anyone on this network who gets the link can download the shared items until it ends. The connection is not encrypted (plain HTTP), " +
               "so only use this on networks you trust, not on public Wi-Fi.",
        Foreground = Theme.Brush("Star"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 4, 0, 8), Visibility = Visibility.Collapsed,
    };
    private readonly ComboBox _duration;
    private readonly StackPanel _setup = new();
    private readonly StackPanel _running = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _files = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Fg1") };
    private readonly TextBlock _remaining = new() { Foreground = Theme.Brush("Fg1") };
    private readonly TextBlock _expires = new() { Foreground = Theme.Brush("Fg1"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _audience = new() { Foreground = Theme.Brush("Fg1"), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _address = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _url = new() { IsReadOnly = true, FontSize = 14 };
    private readonly Image _qr = new() { Width = 180, Height = 180, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _state = new() { Foreground = Theme.Brush("Fg2"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Border _network = new() { Background = Theme.Brush("Bg2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _firewallText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Fg1") };
    private readonly Button _allowFirewall, _networkSettings, _copy, _extend15, _extend60, _stop, _resume, _again;
    private readonly ListBox _log = new() { Height = 110, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<LanAddress> _lanAddresses = new();
    private ShareSession? _session;

    public ShareWindow(Window owner, IEnumerable<string> items, ShareHost? host = null) : this(owner, items.ToList(), null, host) { }

    /// <summary>Shows an existing share (from the Active shares list or the tray).</summary>
    public ShareWindow(Window? owner, ShareSession session, ShareHost? host = null) : this(owner, session.Items.Select(i => i.Path).ToList(), session, host) { }

    private ShareWindow(Window? owner, List<string> items, ShareSession? session, ShareHost? host) : base(owner, "Share on this network", 660, 760)
    {
        _host = host ?? ShareHost.Current;
        _items = items;
        _duration = Dialogs.Combo(new[] { ("15 minutes", TimeSpan.FromMinutes(15)), ("1 hour", TimeSpan.FromHours(1)), ("4 hours", TimeSpan.FromHours(4)), ("24 hours", TimeSpan.FromHours(24)) }, TimeSpan.FromHours(1));
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Dialogs.Heading(_items.Count == 1 ? $"Share '{Path.GetFileName(_items[0].TrimEnd('\\'))}'" : $"Share {_items.Count} items"));

        // ---- set up a new share ----
        _setup.Children.Add(Dialogs.Note($"{ProductBranding.Name} runs a small, read-only download page for these items only. Nothing else on this PC is reachable, and nothing is uploaded anywhere."));
        _setup.Children.Add(_lan);
        _setup.Children.Add(_lanWarning);
        _lan.Checked += (_, _) => _lanWarning.Visibility = Visibility.Visible;
        _lan.Unchecked += (_, _) => _lanWarning.Visibility = Visibility.Collapsed;
        _setup.Children.Add(Dialogs.Field("Stop sharing automatically after", _duration));
        var start = Dialogs.Button("Start sharing", primary: true);
        start.HorizontalAlignment = HorizontalAlignment.Left;
        start.Margin = new Thickness(0, 10, 0, 0);
        start.Click += (_, _) => Start();
        _setup.Children.Add(start);
        root.Children.Add(_setup);

        // ---- an existing share ----
        _running.Children.Add(_status);
        var facts = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Fact(string label, UIElement value)
        {
            int row = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Theme.Brush("Fg2"), Margin = new Thickness(0, 2, 14, 2) };
            Grid.SetRow(l, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 0, 2);
            facts.Children.Add(l);
            facts.Children.Add(value);
        }
        Fact("Shared", _files);
        Fact("Time left", _remaining);
        Fact("Ends", _expires);
        Fact("Who can open it", _audience);
        _running.Children.Add(facts);

        _running.Children.Add(Dialogs.Label("Open this link on the other device"));
        _address.SelectionChanged += (_, _) => ShowUrl();
        _running.Children.Add(_address);
        var urlRow = new DockPanel();
        _copy = Dialogs.Button("Copy link");
        _copy.Click += (_, _) => { try { Clipboard.SetText(_url.Text); _state.Text = "Link copied."; } catch (System.Runtime.InteropServices.COMException) { } };
        DockPanel.SetDock(_copy, Dock.Right);
        urlRow.Children.Add(_copy);
        urlRow.Children.Add(_url);
        _running.Children.Add(urlRow);
        _running.Children.Add(_qr);

        var actions = new WrapPanel { Margin = new Thickness(-6, 12, 0, 0) };
        _extend15 = Dialogs.Button("Extend 15 min");
        _extend15.Click += (_, _) => Extend(TimeSpan.FromMinutes(15));
        _extend60 = Dialogs.Button("Extend 1 hour");
        _extend60.Click += (_, _) => Extend(TimeSpan.FromHours(1));
        _stop = Dialogs.Button("Stop sharing");
        _stop.Click += (_, _) => Stop();
        _resume = Dialogs.Button("Resume sharing", primary: true);
        _resume.Click += async (_, _) => await ResumeAsync();
        _again = Dialogs.Button("Share again…");
        _again.Click += (_, _) => ShowSetup();
        foreach (var b in new[] { _resume, _extend15, _extend60, _stop }) actions.Children.Add(b);
        actions.Children.Add(_again);
        _running.Children.Add(actions);
        _running.Children.Add(_state);

        // ---- network / firewall help (LAN shares) ----
        var net = new StackPanel();
        net.Children.Add(new TextBlock { Text = "Network and firewall", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        net.Children.Add(_firewallText);
        var netButtons = new WrapPanel { Margin = new Thickness(-6, 8, 0, 0) };
        _allowFirewall = Dialogs.Button("Allow through firewall…");
        _allowFirewall.ToolTip = $"Adds one Windows Firewall rule: incoming TCP on this port, for {ProductBranding.Name} only, from your local subnet only, on Private/Domain networks. Windows asks for administrator approval.";
        _allowFirewall.Click += (_, _) => AllowThroughFirewall();
        _networkSettings = Dialogs.Button("Network settings");
        _networkSettings.ToolTip = "Open Windows network settings (to mark your home or office network as Private).";
        _networkSettings.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } };
        netButtons.Children.Add(_allowFirewall);
        netButtons.Children.Add(_networkSettings);
        net.Children.Add(netButtons);
        net.Children.Add(Dialogs.Note("If it still doesn't open: both devices must be on the same network (guest Wi-Fi often keeps devices apart), and a VPN on either device can get in the way.", "Fg2"));
        _network.Child = net;
        _running.Children.Add(_network);

        _running.Children.Add(Dialogs.Label("Downloads"));
        _running.Children.Add(_log);
        _running.Children.Add(Dialogs.Note($"Closing this window doesn't stop sharing. Find your shares under Tools → Active shares; when the main window closes, {ProductBranding.Name} stays in the notification area until they end."));
        root.Children.Add(_running);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        EventHandler changed = (_, _) => Dispatcher.BeginInvoke(Refresh);
        EventHandler<ShareTransfer> transfer = (_, t) => Dispatcher.BeginInvoke(() =>
        {
            if (t.Completed && _session != null && t.SessionId == _session.Id)
                _log.Items.Insert(0, $"{DateTime.Now:T}  {t.Client}  {t.Item}  {FileOps.FormatSize(t.Bytes)}");
        });
        NetworkAddressChangedEventHandler netChanged = (_, _) => Dispatcher.BeginInvoke(RefreshAddresses);
        _host.Changed += changed;
        _host.Transfer += transfer;
        NetworkChange.NetworkAddressChanged += netChanged;
        _clock.Tick += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            // the window only stops listening; the share itself keeps running
            _clock.Stop();
            _host.Changed -= changed;
            _host.Transfer -= transfer;
            NetworkChange.NetworkAddressChanged -= netChanged;
        };

        _session = session ?? _host.FindActive(_items);
        if (_session != null) ShowSession();
    }

    internal ShareSession? Session => _session;
    internal void AllowLan(bool on) => _lan.IsChecked = on;
    internal string Url => _url.Text;
    internal string StatusText => _status.Text;
    internal string RemainingText => _remaining.Text;
    internal void SelectDuration(TimeSpan t) { foreach (ComboBoxItem i in _duration.Items) if (i.Tag is TimeSpan v && v == t) _duration.SelectedItem = i; }

    internal void Start()
    {
        try
        {
            _session = _host.Start(_items, new ShareOptions { AllowLan = _lan.IsChecked == true, Lifetime = Dialogs.Selected<TimeSpan>(_duration) });
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            Dialogs.Error(this, "Share", "Sharing couldn't start: " + ex.Message);
            return;
        }
        _log.Items.Clear();
        ShowSession();
    }

    internal void Stop()
    {
        if (_session == null) return;
        _host.Stop(_session);
        Refresh();
    }

    private void Extend(TimeSpan by)
    {
        if (_session?.State != ShareState.Active) return;
        _host.Extend(_session, by);
        _state.Text = $"Extended by {(by.TotalHours >= 1 ? $"{by.TotalHours:0} hour" : $"{by.TotalMinutes:0} minutes")}.";
        Refresh();
    }

    private async Task ResumeAsync()
    {
        if (_session == null) return;
        _resume.IsEnabled = false;
        _state.Text = "Starting the share again…";
        bool ok = await _host.ResumeAsync(_session);
        _resume.IsEnabled = true;
        _state.Text = ok ? "Sharing again, with the same link." : _session.State == ShareState.Interrupted ? "The share couldn't be started again." : "This share can't be resumed any more.";
        ShowSession();
    }

    private void ShowSetup()
    {
        _session = null;
        _running.Visibility = Visibility.Collapsed;
        _setup.Visibility = Visibility.Visible;
        _clock.Stop();
    }

    private void ShowSession()
    {
        _setup.Visibility = Visibility.Collapsed;
        _running.Visibility = Visibility.Visible;
        RefreshAddresses();
        Refresh();
        _clock.Start();
        if (_session is { AllowLan: true }) CheckFirewall();
    }

    private void RefreshAddresses()
    {
        if (_session == null) return;
        var previous = (_address.SelectedItem as ComboBoxItem)?.Tag as string;
        _lanAddresses = _session.AllowLan ? ShareNetwork.LanAddresses() : new List<LanAddress>();
        _address.Items.Clear();
        foreach (var a in _lanAddresses) _address.Items.Add(new ComboBoxItem { Content = a.Display, Tag = a.Address.ToString() });
        _address.Items.Add(new ComboBoxItem { Content = "127.0.0.1  ·  this PC only", Tag = "127.0.0.1" });
        _address.SelectedItem = _address.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == previous) ?? _address.Items[0];
        _address.Visibility = _address.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_session.AllowLan && _lanAddresses.Count == 0)
            _state.Text = "No local network connection was found; only this PC can open the link right now.";
        ShowUrl();
    }

    private void ShowUrl()
    {
        if (_session == null || _address.SelectedItem is not ComboBoxItem { Tag: string host }) return;
        var url = _host.Url(_session, host);
        if (_url.Text == url) return;
        _url.Text = url;
        _qr.Source = Qr(url);
    }

    private void Refresh()
    {
        if (_session == null) return;
        var s = _session;
        (string text, string brush) = s.State switch
        {
            ShareState.Active => ("● Active", "Ok"),
            ShareState.Expired => ("● Expired — the link no longer works", "Fg2"),
            ShareState.Stopped => ("● Stopped — the link no longer works", "Fg2"),
            _ => ($"● Paused — {ProductBranding.Name} was closed while this was shared", "Star"),
        };
        _status.Text = text;
        _status.Foreground = Theme.Brush(brush);
        _files.Text = string.Join("\n", s.Items.Select(i => i.Locate() ?? i.Path + " (missing)"));
        var left = s.State == ShareState.Active ? s.Remaining : s.State == ShareState.Interrupted ? s.ExpiresUtc - DateTime.UtcNow : TimeSpan.Zero;
        _remaining.Text = left <= TimeSpan.Zero ? "—" : left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : $"{left.Minutes} min {left.Seconds:00} s";
        _expires.Text = s.ExpiresUtc.ToLocalTime().ToString("t") + (s.ExpiresUtc != s.OriginalExpiresUtc ? $" (extended; first set to {s.OriginalExpiresUtc.ToLocalTime():t})" : "") +
                        (s.EndedUtc is { } ended && s.State is ShareState.Stopped or ShareState.Expired ? $" · ended {ended.ToLocalTime():t}" : "");
        _audience.Text = s.AllowLan ? "Devices on your local network that have the link" : "Only this PC";
        bool active = s.State == ShareState.Active;
        _extend15.IsEnabled = _extend60.IsEnabled = _stop.IsEnabled = active;
        _resume.Visibility = s.State == ShareState.Interrupted ? Visibility.Visible : Visibility.Collapsed;
        _stop.Visibility = s.State is ShareState.Active or ShareState.Interrupted ? Visibility.Visible : Visibility.Collapsed;
        _url.Opacity = _qr.Opacity = active ? 1 : 0.35;
        _copy.IsEnabled = active;
        _again.Visibility = s.State is ShareState.Expired or ShareState.Stopped ? Visibility.Visible : Visibility.Collapsed;
        if (!active) _clock.Stop();
    }

    private void CheckFirewall()
    {
        _network.Visibility = Visibility.Visible;
        _firewallText.Text = "Checking Windows Firewall…";
        _allowFirewall.Visibility = Visibility.Collapsed;
        int port = _host.Port;
        var exe = Environment.ProcessPath;
        Task.Run(() => ShareFirewall.Check(port, exe)).ContinueWith(t =>
        {
            var r = t.IsCompletedSuccessfully ? t.Result : FirewallReport.Unknown;
            _firewallText.Text = r.Explain();
            _firewallText.Foreground = Theme.Brush(r.LikelyBlocked ? "Star" : "Fg1");
            _allowFirewall.Visibility = r.Known && r.Enabled && !r.HasAllowRule && !r.LocalRulesIgnored ? Visibility.Visible : Visibility.Collapsed;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void AllowThroughFirewall()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        int port = _host.Port;
        var existing = ShareFirewall.HasOwnedRule(port);
        if (existing == null)
        {
            Dialogs.Error(this, "Allow through Windows Firewall", "The existing application rule couldn't be inspected. Check Windows Defender Firewall before adding or changing a rule.");
            return;
        }
        if (!Dialogs.Confirm(this, "Allow through Windows Firewall",
                $"{ProductBranding.Name} will ask Windows (administrator approval) to {(existing.Value ? "update the existing" : "add one")} firewall rule:\n\n" +
                $"• incoming TCP connections on port {port} only\n• to {ProductBranding.Name} (Fyltrix.exe) only\n• from your local subnet only\n• on Private and Domain networks (never Public)\n\n" +
                $"You can remove it any time in Windows Defender Firewall → Advanced settings → Inbound rules (\"{ShareFirewall.RuleName} (TCP {port})\").\n\nContinue?", defaultNo: false))
            return;
        var error = ShareFirewall.RunElevated(existing.Value ? ShareFirewall.UpdateRuleArguments(port, exe) : ShareFirewall.AddRuleArguments(port, exe));
        if (error != null) Dialogs.Error(this, "Allow through Windows Firewall", "The rule wasn't added: " + error);
        CheckFirewall();
    }

    private static BitmapSource? Qr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
