using System.Windows;
using System.Windows.Input;
using Fyltrix.Preview;

namespace Fyltrix.UI;

/// <summary>Space-bar Quick View: a large preview that follows the selection in the main window.</summary>
public sealed class QuickViewWindow : Window
{
    private readonly PreviewPane _pane = new(showHeader: true, showPopOut: false);

    /// <summary>Raised with -1 / +1 when the user presses Left/Up or Right/Down inside Quick View.</summary>
    public event EventHandler<int>? StepRequested;
    public event EventHandler<string>? ImageToolsRequested;

    private (WindowState state, WindowStyle style, ResizeMode resize)? _beforeFullScreen;

    public QuickViewWindow(Window owner)
    {
        Owner = owner;
        Title = "Quick View";
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Theme.Brush("Bg1");
        Content = _pane;

        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1300, wa.Width * 0.75);
        Height = Math.Min(950, wa.Height * 0.85);
        Left = owner.WindowState == WindowState.Maximized
            ? wa.Left + (wa.Width - Width) / 2
            : owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2);
        Top = owner.WindowState == WindowState.Maximized
            ? wa.Top + (wa.Height - Height) / 2
            : owner.Top + Math.Max(0, (owner.ActualHeight - Height) / 2);

        SourceInitialized += (_, _) => Shell.UseDarkTitleBar(this);
        _pane.EscapePressed += (_, _) => { if (_beforeFullScreen != null) SetFullScreen(false); else Close(); };
        _pane.SpacePressed += (_, _) => Close();
        _pane.StepRequested += (_, d) => StepRequested?.Invoke(this, d);
        _pane.ImageToolsRequested += (_, p) => ImageToolsRequested?.Invoke(this, p);
        _pane.FullScreenChanged += (_, on) => SetFullScreen(on);
        PreviewKeyDown += (_, e) =>
        {
            bool inPlayer = Keyboard.FocusedElement is DependencyObject d && VisualTree.FindAncestor<NativeMediaPlayer>(d) != null;
            if (inPlayer && e.Key is Key.Space or Key.Left or Key.Right) return;
            if (e.Key == Key.Escape && _beforeFullScreen != null) { e.Handled = true; SetFullScreen(false); _pane.FullScreenEnded(); }
            else if (e.Key is Key.Escape or Key.Space) { e.Handled = true; Close(); }
            else if (e.Key is Key.Left or Key.Up) { e.Handled = true; StepRequested?.Invoke(this, -1); }
            else if (e.Key is Key.Right or Key.Down) { e.Handled = true; StepRequested?.Invoke(this, +1); }
        };
        Closed += (_, _) => _pane.DisposeWeb();
    }

    public void ShowPath(string? path)
    {
        Title = path == null ? "Quick View" : "Quick View – " + Path.GetFileName(path.TrimEnd('\\'));
        _pane.Show(path, delayMs: 40);
    }

    public void Release(IEnumerable<string> paths) => _pane.ReleaseIfShowing(paths);

    private void SetFullScreen(bool on)
    {
        if (on == (_beforeFullScreen != null)) return;
        if (on)
        {
            _beforeFullScreen = (WindowState, WindowStyle, ResizeMode);
            WindowState = WindowState.Normal; // re-maximizing after the style change covers the taskbar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            var (state, style, resize) = _beforeFullScreen!.Value;
            _beforeFullScreen = null;
            WindowState = WindowState.Normal;
            WindowStyle = style;
            ResizeMode = resize;
            WindowState = state;
        }
    }
}
