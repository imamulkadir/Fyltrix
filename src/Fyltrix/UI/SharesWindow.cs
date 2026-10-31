using Fyltrix.Core;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Fyltrix.Core.Sharing;

namespace Fyltrix.UI;

/// <summary>Every share this session knows about: active ones (copy link / extend / stop), ones paused by a restart (resume), and recently ended ones.</summary>
public sealed class SharesWindow : ToolWindow
{
    public sealed class Row
    {
        public required ShareSession Session { get; init; }
        public string Name => Session.Title;
        public string State => Session.State switch
        {
            ShareState.Active => "Active",
            ShareState.Interrupted => $"Paused ({ProductBranding.Name} was closed)",
            ShareState.Expired => "Expired",
            _ => "Stopped",
        };
        public string Left
        {
            get
            {
                var t = Session.State == ShareState.Active ? Session.Remaining : Session.State == ShareState.Interrupted ? Session.ExpiresUtc - DateTime.UtcNow : TimeSpan.Zero;
                return t <= TimeSpan.Zero ? "—" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min {t.Seconds:00} s";
            }
        }
        public string Ends => Session.ExpiresUtc.ToLocalTime().ToString("g");
        public string Audience => Session.AllowLan ? "Local network" : "This PC only";
        public string Downloads => Session.Downloads.ToString();
    }

    private readonly ShareHost _host;
    private readonly ListView _list;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly Button _open, _copy, _extend, _stop, _resume;
    private readonly TextBlock _note = new() { Foreground = Theme.Brush("Fg2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public SharesWindow(Window? owner, ShareHost? host = null) : base(owner, "Active shares", 860, 420)
    {
        _host = host ?? ShareHost.Current;
        _list = List(Column("Shared", "Name", 200), Column("Status", "State", 180), Column("Time left", "Left", 95), Column("Ends", "Ends", 135),
                     Column("Who", "Audience", 100), Column("Downloads", "Downloads", 80, right: true));
        _list.SelectionMode = SelectionMode.Single;
        _list.ItemsSource = _rows;
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => OpenSelected();

        var buttons = new WrapPanel { Margin = new Thickness(-6, 10, 0, 0) };
        _open = Dialogs.Button("Details…");
        _open.Click += (_, _) => OpenSelected();
        _copy = Dialogs.Button("Copy link");
        _copy.Click += (_, _) =>
        {
            if (Selected is not { State: ShareState.Active } s) return;
            try { Clipboard.SetText(_host.Urls(s)[0]); _note.Text = "Link copied."; } catch (System.Runtime.InteropServices.COMException) { }
        };
        _extend = Dialogs.Button("Extend 15 min");
        _extend.Click += (_, _) => { if (Selected is { State: ShareState.Active } s) _host.Extend(s, TimeSpan.FromMinutes(15)); };
        _stop = Dialogs.Button("Stop sharing");
        _stop.Click += (_, _) => { if (Selected is { } s) _host.Stop(s); };
        _resume = Dialogs.Button("Resume", primary: true);
        _resume.Click += async (_, _) =>
        {
            if (Selected is not { State: ShareState.Interrupted } s) return;
            _note.Text = (await _host.ResumeAsync(s)) ? "Sharing again, with the same link." : "That share couldn't be resumed.";
        };
        var stopAll = Dialogs.Button("Stop all");
        stopAll.Click += (_, _) => _host.StopAll();
        var clear = Dialogs.Button("Clear ended");
        clear.Click += (_, _) => _host.ClearEnded();
        foreach (var b in new[] { _open, _resume, _copy, _extend, _stop, stopAll, clear }) buttons.Children.Add(b);

        var root = new DockPanel { Margin = new Thickness(16) };
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(_note);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        EventHandler changed = (_, _) => Dispatcher.BeginInvoke(Reload);
        _host.Changed += changed;
        _clock.Tick += (_, _) => _list.Items.Refresh();
        Closed += (_, _) => { _clock.Stop(); _host.Changed -= changed; };
        Reload();
        _clock.Start();
    }

    private ShareSession? Selected => (_list.SelectedItem as Row)?.Session;

    internal IReadOnlyList<Row> Rows => _rows;

    private void Reload()
    {
        var keep = Selected?.Id;
        _rows.Clear();
        foreach (var s in _host.Sessions.OrderBy(s => s.State switch { ShareState.Active => 0, ShareState.Interrupted => 1, _ => 2 }).ThenByDescending(s => s.CreatedUtc))
            _rows.Add(new Row { Session = s });
        _list.SelectedItem = _rows.FirstOrDefault(r => r.Session.Id == keep) ?? _rows.FirstOrDefault();
        int active = _rows.Count(r => r.Session.State == ShareState.Active);
        _note.Text = _rows.Count == 0 ? "Nothing is shared. Right-click files or folders and choose Share on this network…"
            : active == 0 ? "Nothing is being shared right now." : $"{active} active share{(active == 1 ? "" : "s")}" +
              (_host.Port > 0 ? $" on port {_host.Port}{(_host.ListeningOnLan ? " (local network)" : " (this PC only)")}." : ".");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var s = Selected;
        _open.IsEnabled = s != null;
        _copy.IsEnabled = _extend.IsEnabled = s?.State == ShareState.Active;
        _stop.IsEnabled = s?.State is ShareState.Active or ShareState.Interrupted;
        _resume.Visibility = s?.State == ShareState.Interrupted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenSelected()
    {
        if (Selected is { } s) new ShareWindow(this, s, _host).Show();
    }
}
