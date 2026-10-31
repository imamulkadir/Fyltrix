using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.UI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Fyltrix.Preview;

/// <summary>
/// The preview surface: a WebView2 (PDF, HTML, Markdown, JSON, Word, Excel, images, code...)
/// plus a Windows preview-handler host for formats like PowerPoint and Outlook .msg.
/// </summary>
public sealed class PreviewPane : Grid
{
    private static Task<CoreWebView2Environment>? _envTask;
    public static AppSettings? Settings { get; set; }

    private WebView2 _web;
    private readonly Grid _body = new();
    /// <summary>Windows' own preview handlers (PowerPoint, Outlook .msg…) in a WinForms host. Made on first use: loading
    /// WinForms for it cost ~0.2 s at every startup.</summary>
    private WindowsFormsHost? _formsHost;
    private ShellPreviewHost? _shellHost;
    /// <summary>Made on first use: only videos WebView2 can't decode need it, and building it took ~1 s at startup.</summary>
    private NativeMediaPlayer? _native;
    private readonly Border _info = new();
    private readonly TextBlock _infoTitle = new();
    private readonly TextBlock _infoText = new();
    private readonly TextBlock _title = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly Button _btnScripts, _btnUnblock, _btnOpen, _btnPopOut;

    private bool _ready, _failed, _initializing;
    /// <summary>Bumped when the engine is released, so an initialisation still running for the old one stops.</summary>
    private int _engine;
    private string? _pendingUrl, _expectedUrl;
    private int _version;
    private string? _currentPath;
    private PreviewKind _currentKind;

    public event EventHandler? EscapePressed;
    public event EventHandler? SpacePressed;
    public event EventHandler<string>? PopOutRequested;
    public event EventHandler<string>? StatusMessage;
    /// <summary>-1 / +1 when the viewer asks for the previous / next file (arrow keys in the image viewer).</summary>
    public event EventHandler<int>? StepRequested;
    public event EventHandler<string>? ImageToolsRequested;
    /// <summary>The preview entered or left fullscreen (video/image fullscreen button, F, Esc).</summary>
    public event EventHandler<bool>? FullScreenChanged;
    /// <summary>The user scrolled the page (left, top in CSS pixels); only raised while <see cref="ReportsScroll"/> is on.</summary>
    public event EventHandler<Point>? Scrolled;

    /// <summary>Compare uses this to scroll two previews together: each page reports its scroll position and can be scrolled to one.</summary>
    public bool ReportsScroll { get; set; }

    public string? CurrentPath => _currentPath;
    internal WebView2 Web => _web;
    internal bool IsReady => _ready;

    public PreviewPane(bool showHeader = true, bool showPopOut = true)
    {
        Background = Theme.Brush("Bg1");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---------- header ----------
        _title.FontWeight = FontWeights.SemiBold;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(10, 0, 8, 0);
        _title.Foreground = Theme.Brush("Fg1");

        _btnScripts = Theme.SmallButton("\uE943", "Scripts off", "HTML preview runs with JavaScript off for safety. Click to allow scripts for HTML previews.");
        _btnScripts.Click += (_, _) => ToggleScripts();
        _btnUnblock = Theme.SmallButton("\uEA18", "Unblock", "This file is marked as downloaded from the internet. Unblock removes that mark (same as Properties → Unblock).");
        _btnUnblock.Click += (_, _) => UnblockCurrent();
        _btnOpen = Theme.SmallButton("\uE8A7", "Open", "Open with the default app");
        _btnOpen.Click += (_, _) => { if (_currentPath != null) Shell.Open(_currentPath); };
        _btnPopOut = Theme.SmallButton("\uE8A0", "", "Open in Quick View (Space)");
        _btnPopOut.Click += (_, _) => { if (_currentPath != null) PopOutRequested?.Invoke(this, _currentPath); };
        _actions.Children.Add(_btnScripts);
        _actions.Children.Add(_btnUnblock);
        _actions.Children.Add(_btnOpen);
        if (showPopOut) _actions.Children.Add(_btnPopOut);
        _actions.Margin = new Thickness(0, 0, 6, 0);

        var header = new DockPanel { Height = 38, Background = Theme.Brush("Bg2"), LastChildFill = true };
        DockPanel.SetDock(_actions, Dock.Right);
        header.Children.Add(_actions);
        header.Children.Add(_title);
        var headerBorder = new Border { Child = header, BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
        if (!showHeader) headerBorder.Visibility = Visibility.Collapsed;
        SetRow(headerBorder, 0);
        Children.Add(headerBorder);

        // ---------- body ----------
        var body = _body;
        SetRow(body, 1);
        Children.Add(body);

        _web = NewWeb();
        body.Children.Add(_web);


        _infoTitle.FontSize = 15;
        _infoTitle.FontWeight = FontWeights.SemiBold;
        _infoTitle.Foreground = Theme.Brush("Fg1");
        _infoTitle.TextWrapping = TextWrapping.Wrap;
        _infoTitle.TextAlignment = TextAlignment.Center;
        _infoText.Foreground = Theme.Brush("Fg2");
        _infoText.TextWrapping = TextWrapping.Wrap;
        _infoText.TextAlignment = TextAlignment.Center;
        _infoText.Margin = new Thickness(0, 6, 0, 0);
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        infoStack.Children.Add(_infoTitle);
        infoStack.Children.Add(_infoText);
        _info.Child = infoStack;
        _info.Background = Theme.Brush("Bg1");
        body.Children.Add(_info);

        ShowInfo("No file selected", "Select a file to preview it here.");
        UpdateActions();
        // The preview engine (WebView2: a browser process and its helpers, ~130 MB) starts only when the pane is visible,
        // not when it is merely created (a hidden preview pane used to start it anyway).
        Loaded += (_, _) => { if (IsVisible) _ = InitAsync(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = InitAsync(); };
        Unloaded += (_, _) => { _shellHost?.Unload(); _native?.Close(); };
    }

    /// <summary>WebView2's profile folder. Tests use their own, so they never share (and wait on) the browser process of a
    /// Fyltrix the user has open.</summary>
    internal static string WebDataFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileLens", "WebView2");

    private static Task<CoreWebView2Environment> GetEnvironment()
    {
        return _envTask ??= CoreWebView2Environment.CreateAsync(null, WebDataFolder);
    }

    private void ShellHost()
    {
        if (_formsHost != null) return;
        _shellHost = new ShellPreviewHost();
        _formsHost = new WindowsFormsHost { Child = _shellHost, Visibility = Visibility.Collapsed };
        _body.Children.Insert(_body.Children.IndexOf(_web) + 1, _formsHost); // where it used to be: above the web view
    }

    private NativeMediaPlayer NativePlayer()
    {
        if (_native != null) return _native;
        _native = new NativeMediaPlayer { Visibility = Visibility.Collapsed };
        _native.FullScreenRequested += (_, on) => FullScreenChanged?.Invoke(this, on);
        _body.Children.Insert(_body.Children.IndexOf(_info), _native); // under the info overlay, as before
        return _native;
    }

    internal NativeMediaPlayer NativePlayerForTest => NativePlayer();

    private static WebView2 NewWeb() => new()
    {
        DefaultBackgroundColor = Theme.DrawingColor("Bg1"),
        Visibility = Visibility.Collapsed,
    };

    /// <summary>Shuts the preview engine down and gives its memory back (the preview pane was hidden). It starts again the
    /// next time the pane is shown.</summary>
    public void ReleaseEngine()
    {
        if (!_ready && !_initializing) return;
        _engine++;
        _scrollPoll?.Stop();
        _pendingUrl = null;
        _expectedUrl = null;
        _ready = false;
        _initializing = false;
        var old = _web;
        _web = NewWeb();
        _body.Children.Insert(_body.Children.IndexOf(old), _web);
        _body.Children.Remove(old);
        try { old.Dispose(); } catch { }
    }

    internal bool IsEngineStarted => _ready || _initializing;

    private async Task InitAsync()
    {
        if (_ready || _failed || _initializing) return;
        _initializing = true;
        int engine = _engine;
        var web = _web;
        try
        {
            var env = await GetEnvironment();
            await web.EnsureCoreWebView2Async(env);
            if (engine != _engine) return; // released while starting
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            try { core.Profile.PreferredColorScheme = Theme.IsLight ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark; } catch { }
            PreviewServer.Attach(core);
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.WebMessageReceived += (_, e) =>
            {
                string? msg = null;
                try { msg = e.TryGetWebMessageAsString(); } catch { }
                switch (msg)
                {
                    case "escape": EscapePressed?.Invoke(this, EventArgs.Empty); break;
                    case "space": SpacePressed?.Invoke(this, EventArgs.Empty); break;
                    case "prev": StepRequested?.Invoke(this, -1); break;
                    case "next": StepRequested?.Invoke(this, +1); break;
                    case "copy-image": CopyImage(); break;
                    case "image-tools": if (_currentPath != null) ImageToolsRequested?.Invoke(this, _currentPath); break;
                    case "pdf-native": // the page-by-page PDF view couldn't open it (e.g. password): use the standard viewer
                        if (_currentPath != null && _currentKind == PreviewKind.Pdf) Navigate(PreviewUrls.FileUrl(_currentPath));
                        break;
                    case { } m when m.StartsWith("scroll:", StringComparison.Ordinal):
                        if (ReportsScroll) RaiseScrolled(m[7..]);
                        break;
                    case { } m when m.StartsWith("media-unsupported", StringComparison.Ordinal):
                        // ignore a late message from the page of a file that is no longer shown
                        var from = m.Length > 18 ? m[18..] : null;
                        if (_currentPath != null && _currentKind == PreviewKind.Media && (from == null || from == PreviewUrls.FileUrl(_currentPath)))
                        {
                            Diagnostics.Info("Media preview", "WebView2 can't decode the file; using Windows Media Foundation", _currentPath);
                            ShowNative(_currentPath);
                        }
                        break;
                }
            };
            core.NavigationCompleted += (_, e) => { if (ReportsScroll && e.IsSuccess) OnScrollPageLoaded(core); };
            core.ContainsFullScreenElementChanged += (_, _) => FullScreenChanged?.Invoke(this, core.ContainsFullScreenElement);
            core.ProcessFailed += (_, _) => StatusMessage?.Invoke(this, "The preview engine restarted. Select the file again if the preview is blank.");
            _ready = true;
            _initializing = false;
            if (_pendingUrl != null) { var u = _pendingUrl; _pendingUrl = null; Navigate(u); }
            else if (_web.Visibility != Visibility.Visible)
            {
                // Warm up while nothing is shown: the first page on the preview origin starts the page process and compiles
                // viewer.js (~0.7 s), which otherwise lands on the first file the user selects. The pane stays hidden.
                var warm = PreviewUrls.ViewerUrl(new Dictionary<string, string> { ["mode"] = "msg", ["title"] = "" });
                _expectedUrl = warm;
                core.Navigate(warm);
            }
        }
        catch (Exception ex) when (engine != _engine) { Diagnostics.Info("Preview", "engine released while starting: " + ex.Message); }
        catch (Exception ex)
        {
            _failed = true;
            bool missing = ex is WebView2RuntimeNotFoundException || ex.GetType().Name.Contains("RuntimeNotFound");
            ShowInfo(missing ? "Microsoft Edge WebView2 Runtime not found" : "The preview engine couldn't start",
                missing
                    ? $"{ProductBranding.Name} uses the WebView2 runtime that ships with Microsoft Edge. Install the 'Evergreen' runtime from Microsoft (a per-user install works without admin), then restart {ProductBranding.Name}."
                    : ex.Message);
        }
        finally { if (engine == _engine) _initializing = false; }
    }

    /// <summary>Finds the page's main scroller, reports when the user scrolls it and lets the host scroll it. Positions set by the
    /// host are not reported back (that would bounce between the two panes). On a page whose own scripts are off (HTML) the
    /// injected functions still run but its event listeners don't fire, so there the pane polls <c>__flPos</c> instead.</summary>
    private const string ScrollScript = """
        (() => {
          if (window.__flScroll) return; window.__flScroll = true;
          let target = null, placed = null, queued = false;
          const scrollable = e => e && (e.scrollHeight > e.clientHeight + 1 || e.scrollWidth > e.clientWidth + 1);
          const area = e => e === document.scrollingElement ? innerWidth * innerHeight : e.clientWidth * e.clientHeight;
          const main = () => {
            if (target && target.isConnected && scrollable(target)) return target;
            const doc = document.scrollingElement;
            if (scrollable(doc)) return target = doc;
            let best = null, most = 0;
            for (const e of document.body ? document.body.querySelectorAll('*') : []) {
              if (e.clientHeight < 50 || !scrollable(e)) continue;
              const s = getComputedStyle(e);
              if (!/auto|scroll/.test(s.overflowY + s.overflowX)) continue;
              if (area(e) > most) { most = area(e); best = e; }
            }
            return target = best || doc;
          };
          const pos = () => { const t = main(); return t ? Math.round(t.scrollLeft) + ',' + Math.round(t.scrollTop) : ''; };
          document.addEventListener('scroll', ev => {
            const t = ev.target === document ? document.scrollingElement : ev.target;
            if (!(t instanceof Element)) return;
            const m = main();
            if (t !== m) { if (m && area(t) <= area(m)) return; target = t; } // a code block or table scrolling sideways is not the page
            if (queued) return;
            queued = true;
            requestAnimationFrame(() => {
              queued = false;
              const p = pos();
              if (p === placed) { placed = null; return; } // where the host just put it, not the user scrolling
              placed = null;
              if (window.chrome && chrome.webview) chrome.webview.postMessage('scroll:' + p);
            });
          }, { capture: true, passive: true });
          window.__flPos = pos;
          window.__flScrollTo = (x, y) => {
            const t = main();
            if (!t) return '';
            t.scrollLeft = x; t.scrollTop = y;
            return placed = pos();
          };
        })();
        """;

    private System.Windows.Threading.DispatcherTimer? _scrollPoll;
    private string? _polledPos;
    private int _scrollMoves;
    private bool _pollBusy;

    private void OnScrollPageLoaded(CoreWebView2 core)
    {
        _ = core.ExecuteScriptAsync(ScrollScript);
        _polledPos = "0,0"; // a freshly loaded page is at the top
        if (core.Settings.IsScriptEnabled) { _scrollPoll?.Stop(); return; }
        _scrollPoll ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(80), System.Windows.Threading.DispatcherPriority.Background, async (_, _) => await PollScroll(), Dispatcher);
        _scrollPoll.Start();
    }

    private async Task PollScroll()
    {
        if (_pollBusy || !_ready || !ReportsScroll || _web.Visibility != Visibility.Visible) return;
        _pollBusy = true;
        try
        {
            int moves = _scrollMoves;
            var p = await ScrollScriptCall("window.__flPos ? __flPos() : ''");
            if (moves != _scrollMoves || p == null) return; // the host moved it meanwhile; ScrollTo recorded where to
            if (p == _polledPos) return;
            _polledPos = p;
            RaiseScrolled(p);
        }
        finally { _pollBusy = false; }
    }

    private async Task<string?> ScrollScriptCall(string script)
    {
        try
        {
            var json = await _web.CoreWebView2.ExecuteScriptAsync(script);
            return System.Text.Json.JsonSerializer.Deserialize<string?>(json) is { Length: > 0 } s ? s : null;
        }
        catch { return null; } // navigated away or disposed
    }

    private void RaiseScrolled(string pos)
    {
        var xy = pos.Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (xy.Length == 2 && double.TryParse(xy[0], System.Globalization.NumberStyles.Float, inv, out var x)
            && double.TryParse(xy[1], System.Globalization.NumberStyles.Float, inv, out var y))
            Scrolled?.Invoke(this, new Point(x, y));
    }

    /// <summary>Scrolls the page's main scroller to a position reported by another pane's <see cref="Scrolled"/>.</summary>
    public async void ScrollTo(Point p)
    {
        if (!_ready || _web.Visibility != Visibility.Visible) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int moves = ++_scrollMoves;
        var now = await ScrollScriptCall($"window.__flScrollTo ? __flScrollTo({p.X.ToString(inv)}, {p.Y.ToString(inv)}) : ''");
        if (moves == _scrollMoves && now != null) _polledPos = now;
    }

    /// <summary>Raises <see cref="Scrolled"/> with where the page is now (e.g. when syncing is switched on).</summary>
    public async void RequestScrollReport()
    {
        if (!_ready || _web.Visibility != Visibility.Visible) return;
        if (await ScrollScriptCall("window.__flPos ? __flPos() : ''") is { } p) RaiseScrolled(p);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri == "about:blank") return;
        if (_expectedUrl != null && SameUrl(e.Uri, _expectedUrl)) return;
        // Page-initiated redirects (meta refresh etc.): just block them.
        if (!e.IsUserInitiated) { e.Cancel = true; return; }
        // A link was clicked inside a preview: never navigate the preview away; open it outside instead.
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private static bool SameUrl(string a, string b)
    {
        if (Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub))
            return string.Equals(ua.GetLeftPart(UriPartial.Query), ub.GetLeftPart(UriPartial.Query), StringComparison.OrdinalIgnoreCase);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenExternal(string url)
    {
        var local = PreviewUrls.PathFromUrl(url);
        if (local != null) { Shell.Open(local); return; }
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            if (url.StartsWith(PreviewUrls.Origin, StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }

    private void Navigate(string url)
    {
        if (_failed) { _web.Visibility = Visibility.Collapsed; _info.Visibility = Visibility.Visible; return; }
        HideNative();
        if (_formsHost != null) _formsHost.Visibility = Visibility.Collapsed;
        _info.Visibility = Visibility.Collapsed;
        _web.Visibility = Visibility.Visible;
        if (!_ready)
        {
            _pendingUrl = url;
            ShowInfoKeepWeb("Loading preview…");
            if (IsVisible) _ = InitAsync(); // after ReleaseEngine, the first preview starts it again
            return;
        }
        // only rendered HTML can run its own scripts; SVG goes through the viewer as an <img>, where scripts never run
        bool scripts = _currentKind != PreviewKind.Html || (Settings?.HtmlScripts ?? false);
        _web.CoreWebView2.Settings.IsScriptEnabled = scripts;
        _expectedUrl = url;
        _web.CoreWebView2.Navigate(url);
    }

    private void ShowInfoKeepWeb(string title)
    {
        _info.Visibility = Visibility.Visible;
        _infoTitle.Text = title;
        _infoText.Text = "";
    }

    private void ShowInfo(string title, string text)
    {
        _pendingUrl = null;
        _shellHost?.Unload();
        HideNative();
        if (_formsHost != null) _formsHost.Visibility = Visibility.Collapsed;
        _web.Visibility = Visibility.Collapsed;
        if (_ready) { _expectedUrl = "about:blank"; _web.CoreWebView2.Navigate("about:blank"); }
        _info.Visibility = Visibility.Visible;
        _infoTitle.Text = title;
        _infoText.Text = text;
    }

    private void UpdateActions()
    {
        bool file = _currentPath != null && File.Exists(_currentPath);
        _btnOpen.Visibility = _currentPath != null ? Visibility.Visible : Visibility.Collapsed;
        _btnPopOut.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        _btnScripts.Visibility = file && _currentKind == PreviewKind.Html ? Visibility.Visible : Visibility.Collapsed;
        bool scripts = Settings?.HtmlScripts ?? false;
        Theme.SetButtonText(_btnScripts, scripts ? "Scripts on" : "Scripts off");
        bool motw = false;
        if (file) { try { motw = Native.HasMarkOfTheWeb(_currentPath!); } catch { } }
        _btnUnblock.Visibility = motw ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleScripts()
    {
        if (Settings == null) return;
        Settings.HtmlScripts = !Settings.HtmlScripts;
        if (Settings.HtmlScripts)
            StatusMessage?.Invoke(this, "Scripts are now allowed in HTML previews. Only do this for files you trust.");
        Reload();
    }

    private void UnblockCurrent()
    {
        if (_currentPath == null) return;
        if (Native.Unblock(_currentPath)) StatusMessage?.Invoke(this, $"Unblocked '{Path.GetFileName(_currentPath)}'.");
        else StatusMessage?.Invoke(this, "Couldn't unblock the file.");
        UpdateActions();
    }

    public void Reload()
    {
        var p = _currentPath;
        _currentPath = null;
        if (p != null) Show(p, delayMs: 0);
    }

    /// <summary>Shows a file or folder. Rapid calls (arrow keys) are debounced.</summary>
    public async void Show(string? path, int delayMs = 90)
    {
        int v = ++_version;
        if (delayMs > 0) await Task.Delay(delayMs);
        // back on the UI thread even when called before WPF's message loop installed its synchronisation context
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => { if (v == _version) Show(path, 0); }); return; }
        if (v != _version) return;
        if (path != null && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)) return;
        Load(path);
    }

    /// <summary>Shows a title and message instead of a file (e.g. while an archive entry is being extracted).</summary>
    public void ShowMessage(string title, string text)
    {
        ++_version;
        _shellHost?.Unload();
        _currentPath = null;
        _currentKind = PreviewKind.None;
        _title.Text = title;
        _title.ToolTip = null;
        ShowInfo(title, text);
        UpdateActions();
    }

    public void Clear()
    {
        ++_version;
        _currentPath = null;
        _title.Text = "";
        ShowInfo("No file selected", "Select a file to preview it here.");
        UpdateActions();
    }

    /// <summary>Lets go of a file that is about to be renamed, moved or deleted.</summary>
    public bool ReleaseIfShowing(IEnumerable<string> paths)
    {
        if (_currentPath == null) return false;
        foreach (var p in paths)
        {
            var a = p.TrimEnd('\\');
            if (_currentPath.Equals(a, StringComparison.OrdinalIgnoreCase) ||
                _currentPath.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                return true;
            }
        }
        return false;
    }

    private void Load(string? path)
    {
        _shellHost?.Unload();
        _currentPath = path;
        if (path == null) { Clear(); return; }

        _title.Text = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
        _title.ToolTip = path;

        if (Directory.Exists(path))
        {
            _currentKind = PreviewKind.None;
            UpdateActions();
            ShowInfo(_title.Text, path);
            int v = _version;
            Task.Run(() =>
            {
                try
                {
                    var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
                    int dirs = Directory.EnumerateDirectories(path, "*", opts).Take(100000).Count();
                    int files = Directory.EnumerateFiles(path, "*", opts).Take(100000).Count();
                    return $"{dirs} folder{(dirs == 1 ? "" : "s")}, {files} file{(files == 1 ? "" : "s")}\n{path}";
                }
                catch (Exception ex) { return ex.Message; }
            }).ContinueWith(t =>
            {
                if (v == _version && _currentPath == path) _infoText.Text = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        if (!File.Exists(path)) { ShowInfo("File not found", path); UpdateActions(); return; }

        _currentKind = FileKinds.KindFor(path, false);
        UpdateActions();
        var name = Path.GetFileName(path);
        var src = PreviewUrls.FileUrl(path);

        switch (_currentKind)
        {
            case PreviewKind.Pdf when ReportsScroll:
                // Compare: Edge's PDF viewer can't be scrolled from outside, so draw the pages in the viewer instead
                Navigate(PreviewUrls.ViewerUrl(new Dictionary<string, string>
                {
                    ["mode"] = "view",
                    ["kind"] = "pdfpages",
                    ["name"] = name,
                    ["api"] = PreviewUrls.ApiUrl("pdfpages", path),
                    ["page"] = PreviewUrls.ApiUrl("pdfpage", path),
                }));
                break;
            case PreviewKind.Pdf:
            case PreviewKind.Html:
                Navigate(src);
                break;
            case PreviewKind.NativeMedia:
                ShowNative(path);
                break;
            case PreviewKind.ShellHandler:
                ShowShellHandler(path);
                break;
            default:
                var q = new Dictionary<string, string>
                {
                    ["mode"] = "view",
                    ["src"] = src,
                    ["kind"] = KindName(_currentKind),
                    ["name"] = name,
                    ["lang"] = FileKinds.LanguageFor(path),
                };
                if (_currentKind == PreviewKind.Archive) q["api"] = PreviewUrls.ApiUrl("archive", path);
                if (_currentKind is PreviewKind.Image or PreviewKind.Svg)
                {
                    q["props"] = PreviewUrls.ApiUrl("props", path, "g=image");
                    if (Core.Imaging.ImageService.NeedsTranscode.Contains(Path.GetExtension(path))) q["render"] = PreviewUrls.ApiUrl("image", path);
                }
                if (_currentKind == PreviewKind.Media)
                {
                    bool audio = FileKinds.IsAudio(path);
                    q["props"] = PreviewUrls.ApiUrl("props", path, audio ? "g=audio" : "g=video");
                    q["thumb"] = PreviewUrls.ApiUrl("thumb", path, "s=512");
                    if (!audio && Core.Media.Subtitles.FindSidecar(path) is { } subs) q["subs"] = PreviewUrls.FileUrl(subs);
                }
                Navigate(PreviewUrls.ViewerUrl(q));
                break;
        }
    }

    private void ShowShellHandler(string path)
    {
        _pendingUrl = null;
        _web.Visibility = Visibility.Collapsed;
        _info.Visibility = Visibility.Collapsed;
        ShellHost();
        _formsHost!.Visibility = Visibility.Visible;
        // the WinForms panel needs a real size before the handler attaches
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_currentPath != path) return;
            var err = _shellHost!.Show(path);
            if (err != null)
            {
                if (_formsHost != null) _formsHost.Visibility = Visibility.Collapsed;
                ShowInfo("No preview available", err + "\nYou can still open it with the Open button.");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ShowDiff(string left, string right)
    {
        _shellHost?.Unload();
        _currentPath = null;
        _currentKind = PreviewKind.Text;
        _title.Text = "Text compare";
        UpdateActions();
        var q = new Dictionary<string, string>
        {
            ["mode"] = "diff",
            ["a"] = PreviewUrls.FileUrl(left),
            ["b"] = PreviewUrls.FileUrl(right),
            ["an"] = Path.GetFileName(left),
            ["bn"] = Path.GetFileName(right),
        };
        Navigate(PreviewUrls.ViewerUrl(q));
    }

    private static string KindName(PreviewKind k) => k switch
    {
        PreviewKind.Markdown => "markdown",
        PreviewKind.Json => "json",
        PreviewKind.Code => "code",
        PreviewKind.Text => "text",
        PreviewKind.Csv => "csv",
        PreviewKind.Docx => "docx",
        PreviewKind.Xlsx => "xlsx",
        PreviewKind.Image or PreviewKind.Svg => "image",
        PreviewKind.Media => "media",
        PreviewKind.Archive => "archive",
        _ => "auto",
    };

    private void ShowNative(string path)
    {
        _pendingUrl = null;
        _shellHost?.Unload();
        if (_formsHost != null) _formsHost.Visibility = Visibility.Collapsed;
        _info.Visibility = Visibility.Collapsed;
        if (_ready) { _expectedUrl = "about:blank"; _web.CoreWebView2.Navigate("about:blank"); }
        _web.Visibility = Visibility.Collapsed;
        var native = NativePlayer();
        native.Visibility = Visibility.Visible;
        native.Open(path);
        native.Focus();
    }

    private void HideNative()
    {
        if (_native == null || _native.Visibility != Visibility.Visible && _native.CurrentPath == null) return;
        _native.Close();
        _native.Visibility = Visibility.Collapsed;
    }

    /// <summary>Called by the host after it left fullscreen by itself (e.g. Esc on the window).</summary>
    public void FullScreenEnded()
    {
        _native?.ExitedFullScreen();
        if (_ready && _web.CoreWebView2.ContainsFullScreenElement) _ = _web.CoreWebView2.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()");
    }

    /// <summary>Copies the current image to the clipboard as a picture (for pasting into documents) and as a file.</summary>
    private async void CopyImage()
    {
        var path = _currentPath;
        if (path == null) return;
        try
        {
            var bmp = await Task.Run(() => Core.Imaging.ImageService.Load(path, 16384));
            var data = new DataObject();
            data.SetImage(bmp);
            var files = new System.Collections.Specialized.StringCollection { path };
            data.SetFileDropList(files);
            Clipboard.SetDataObject(data, true);
            StatusMessage?.Invoke(this, $"Copied the image ({bmp.PixelWidth} × {bmp.PixelHeight}).");
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            StatusMessage?.Invoke(this, "Couldn't copy the image: " + ex.Message);
        }
    }

    public void DisposeWeb()
    {
        _scrollPoll?.Stop();
        _native?.Close();
        _shellHost?.Unload();
        try { _web.Dispose(); } catch { }
    }
}
