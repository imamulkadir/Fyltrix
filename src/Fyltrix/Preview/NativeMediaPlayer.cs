using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Core.Media;
using Fyltrix.UI;

namespace Fyltrix.Preview;

/// <summary>
/// Plays formats Chromium can't (AVI, WMV, MPEG, TS, WMA, AIFF…) through Windows Media Foundation (WPF MediaElement),
/// with the same controls as the web player: play/pause, seek, volume, speed, fullscreen, subtitles and details.
/// </summary>
public sealed class NativeMediaPlayer : Grid
{
    private readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true, Stretch = Stretch.Uniform,
    };
    private readonly Image _art = new() { Width = 220, Height = 220, Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    private readonly TextBlock _audioTitle = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _audioSub = new() { Foreground = Theme.Brush("Fg2"), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _audioPanel = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _subtitle = new()
    {
        Foreground = Brushes.White, FontSize = 20, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(40, 0, 40, 24),
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 1, Color = Colors.Black },
    };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Value = 0.8, Width = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 96, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _infoLine = new() { Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Border _message = new() { Background = Theme.Brush("Bg1"), Visibility = Visibility.Collapsed };
    private readonly TextBlock _messageText = new() { Foreground = Theme.Brush("Fg2"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
    private readonly StackPanel _details = new() { Margin = new Thickness(12, 10, 12, 10) };
    private readonly Border _detailsPanel;
    private readonly Button _play, _mute, _info;
    private readonly ComboBox _speed;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _openTimeout = new() { Interval = TimeSpan.FromSeconds(15) };
    private List<SubtitleCue> _cues = new();
    private bool _dragging, _playing, _fullScreen;
    private string? _path, _failedPath;
    private int _version;

    public event EventHandler<bool>? FullScreenRequested;
    public event EventHandler<string>? Failed;

    public NativeMediaPlayer()
    {
        Background = Brushes.Black;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stage = new Grid { ClipToBounds = true };
        stage.Children.Add(_media);
        _audioPanel.Children.Add(new Border { Child = _art, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Center });
        _audioPanel.Children.Add(_audioTitle);
        _audioPanel.Children.Add(_audioSub);
        stage.Children.Add(_audioPanel);
        stage.Children.Add(_subtitle);
        var openExternal = Dialogs.Button("Open in default player");
        openExternal.HorizontalAlignment = HorizontalAlignment.Center;
        openExternal.Click += (_, _) => { if (_failedPath != null) Shell.Open(_failedPath); };
        var messageStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        messageStack.Children.Add(_messageText);
        messageStack.Children.Add(openExternal);
        _message.Child = messageStack;
        stage.Children.Add(_message);
        stage.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) ToggleFullScreen(); else TogglePlay(); };
        Children.Add(stage);

        _detailsPanel = new Border
        {
            Width = 260, Background = Theme.Brush("Bg2"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1, 0, 0, 0), Visibility = Visibility.Collapsed,
            Child = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        SetColumn(_detailsPanel, 1);
        Children.Add(_detailsPanel);

        _play = Theme.SmallButton("", "", "Play / pause (Space)");
        _play.Click += (_, _) => TogglePlay();
        _mute = Theme.SmallButton("", "", "Mute (M)");
        _mute.Click += (_, _) => { _media.IsMuted = !_media.IsMuted; UpdateMuteGlyph(); };
        var full = Theme.SmallButton("", "", "Fullscreen (F)");
        full.Click += (_, _) => ToggleFullScreen();
        _info = Theme.SmallButton("", "", "Details");
        _info.Click += (_, _) => _detailsPanel.Visibility = _detailsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        _speed = Dialogs.Combo(new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 }.Select(v => ($"{v}×", v)), 1.0);
        _speed.Width = 70;
        _speed.ToolTip = "Playback speed";
        _speed.SelectionChanged += (_, _) => _media.SpeedRatio = Dialogs.Selected<double>(_speed);
        _volume.ValueChanged += (_, _) => { _media.Volume = _volume.Value; UpdateMuteGlyph(); };

        var bar = new DockPanel { Background = Theme.Brush("Bg2"), Height = 40, LastChildFill = true };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        left.Children.Add(_play);
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        right.Children.Add(_time);
        right.Children.Add(_mute);
        right.Children.Add(_volume);
        right.Children.Add(new Border { Width = 8 });
        right.Children.Add(_speed);
        right.Children.Add(_info);
        right.Children.Add(full);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_seek);
        var barStack = new StackPanel();
        barStack.Children.Add(bar);
        var infoBar = new Border { Background = Theme.Brush("Bg2"), Padding = new Thickness(0, 0, 0, 6), Child = _infoLine };
        barStack.Children.Add(infoBar);
        SetRow(barStack, 1);
        SetColumnSpan(barStack, 2);
        Children.Add(barStack);

        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _dragging = false; Seek(_seek.Value); }));
        _seek.PreviewMouseLeftButtonUp += (_, _) => { if (!_dragging) Seek(_seek.Value); };
        _timer.Tick += (_, _) => UpdatePosition();
        _openTimeout.Tick += (_, _) =>
        {
            _openTimeout.Stop();
            if (_path != null) ShowFailure("Windows Media Foundation didn't manage to open it within 15 seconds.");
        };
        _media.MediaOpened += (_, _) => { _openTimeout.Stop(); OnOpened(); };
        _media.MediaEnded += (_, _) => { _playing = false; _media.Pause(); _media.Position = TimeSpan.Zero; UpdatePlayGlyph(); UpdatePosition(); };
        _media.MediaFailed += (_, e) => ShowFailure(e.ErrorException?.Message ?? "Unknown error");
        Focusable = true;
        PreviewKeyDown += OnKey;
        MouseDown += (_, _) => Focus();
    }

    public string? CurrentPath => _path;

    public void Open(string path)
    {
        Close();
        int v = ++_version;
        _path = path;
        _message.Visibility = Visibility.Collapsed;
        _messageText.Text = "";
        try
        {
            if (new FileInfo(path).Length == 0) { ShowFailure("The file is empty (0 bytes)."); return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowFailure(ex.Message); return; }
        // Media Foundation reports neither success nor failure for some damaged files; don't wait forever.
        _openTimeout.Stop();
        _openTimeout.Start();
        _audioPanel.Visibility = Visibility.Collapsed;
        _subtitle.Text = "";
        _infoLine.Text = "Opening with Windows Media Foundation…";
        _media.Volume = _volume.Value;
        _media.SpeedRatio = Dialogs.Selected<double>(_speed);
        _media.Source = new Uri(path);
        _media.Pause(); // opens the file and shows the first frame without playing

        var subs = Subtitles.FindSidecar(path);
        _cues = new List<SubtitleCue>();
        if (subs != null)
            Task.Run(() => Subtitles.Parse(File.ReadAllText(subs))).ContinueWith(t =>
            {
                if (v == _version && t.IsCompletedSuccessfully) _cues = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnOpened()
    {
        int v = _version;
        var path = _path!;
        bool video = _media.HasVideo;
        _seek.Maximum = _media.NaturalDuration.HasTimeSpan ? Math.Max(0.1, _media.NaturalDuration.TimeSpan.TotalSeconds) : 1;
        _audioPanel.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        _audioTitle.Text = Path.GetFileName(path);
        _audioSub.Text = "";
        _art.Visibility = Visibility.Collapsed;
        var basic = video ? $"{_media.NaturalVideoWidth} × {_media.NaturalVideoHeight} · " : "";
        _infoLine.Text = basic + Fmt(_media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero) + " · Windows Media Foundation";
        UpdatePosition();
        Task.Run(() => (ShellProperties.Read(path, video ? ShellProperties.Group.Video : ShellProperties.Group.Audio), video ? null : ShellThumbnails.Get(path, 440)))
            .ContinueWith(t =>
            {
                if (v != _version || !t.IsCompletedSuccessfully) return;
                var (info, art) = t.Result;
                _details.Children.Clear();
                _details.Children.Add(new TextBlock { Text = "Details", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), Foreground = Theme.Brush("Fg1") });
                _details.Children.Add(Row("File size", FileOps.FormatSize(new FileInfo(path).Length)));
                foreach (var i in info.Items) _details.Children.Add(Row(i.Label, i.Value));
                if (info.VideoCodec != null) _infoLine.Text = basic + info.VideoCodec + " · " + Fmt(_media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero) + " · Windows Media Foundation";
                if (!video)
                {
                    string? Get(string l) => info.Items.FirstOrDefault(i => i.Label == l)?.Value;
                    if (Get("Title") is { } title) _audioTitle.Text = title;
                    _audioSub.Text = string.Join(" · ", new[] { Get("Artist") ?? Get("Album artist"), Get("Album"), Get("Year") }.Where(s => !string.IsNullOrEmpty(s)));
                    if (art != null) { _art.Source = art; _art.Visibility = Visibility.Visible; }
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static UIElement Row(string label, string value)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(new TextBlock { Text = label, Foreground = Theme.Brush("Fg2"), FontSize = 11.5 });
        sp.Children.Add(new TextBlock { Text = value, Foreground = Theme.Brush("Fg1"), TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    private void ShowFailure(string why)
    {
        _openTimeout.Stop();
        _timer.Stop();
        _playing = false;
        _failedPath = _path;
        Diagnostics.Info("Media preview", "Windows Media Foundation couldn't open the file: " + why, _path);
        // MediaElement keeps the failed file open until it is closed
        try { _media.Close(); } catch (InvalidOperationException) { }
        bool empty = why.StartsWith("The file is empty", StringComparison.Ordinal);
        // Media Foundation says "Cannot find the media file" for files that exist but aren't media it can parse
        if (why.Contains("Cannot find the media file", StringComparison.OrdinalIgnoreCase) && _failedPath != null && File.Exists(_failedPath))
            why = "Windows Media Foundation couldn't read it as audio or video.";
        _messageText.Text = $"Windows can't play this file.\n{why}" + (empty ? "" : "\n\nIt may be damaged, or need a codec that isn't installed.");
        _message.Visibility = Visibility.Visible;
        _infoLine.Text = "";
        Failed?.Invoke(this, why);
    }

    private void TogglePlay()
    {
        if (_path == null || _message.Visibility == Visibility.Visible) return;
        if (_playing) { _media.Pause(); _playing = false; _timer.Stop(); }
        else { _media.Play(); _playing = true; _timer.Start(); }
        UpdatePlayGlyph();
    }

    private void Seek(double seconds)
    {
        if (_path == null) return;
        _media.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _seek.Maximum));
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        var pos = _media.Position;
        if (!_dragging) _seek.Value = Math.Min(_seek.Maximum, pos.TotalSeconds);
        var dur = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _time.Text = $"{Fmt(pos)} / {Fmt(dur)}";
        _subtitle.Text = _cues.Count > 0 ? Subtitles.At(_cues, pos) ?? "" : "";
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void UpdatePlayGlyph() => ((TextBlock)((StackPanel)_play.Content).Children[0]).Text = _playing ? "" : "";

    private void UpdateMuteGlyph() => ((TextBlock)((StackPanel)_mute.Content).Children[0]).Text = _media.IsMuted || _volume.Value == 0 ? "" : "";

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        FullScreenRequested?.Invoke(this, _fullScreen);
    }

    /// <summary>Called by the host when it leaves fullscreen on its own (Esc).</summary>
    public void ExitedFullScreen() => _fullScreen = false;

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.Left: Seek(_media.Position.TotalSeconds - 5); e.Handled = true; break;
            case Key.Right: Seek(_media.Position.TotalSeconds + 5); e.Handled = true; break;
            case Key.M: _media.IsMuted = !_media.IsMuted; UpdateMuteGlyph(); e.Handled = true; break;
            case Key.F: ToggleFullScreen(); e.Handled = true; break;
            case Key.Escape when _fullScreen: ToggleFullScreen(); e.Handled = true; break;
        }
    }

    /// <summary>Stops playback and releases the file.</summary>
    public void Close()
    {
        ++_version;
        _openTimeout.Stop();
        _timer.Stop();
        _playing = false;
        UpdatePlayGlyph();
        _media.Stop();
        _media.Close();
        _media.Source = null;
        _path = null;
        _art.Source = null;
        _cues = new List<SubtitleCue>();
        _details.Children.Clear();
    }
}
