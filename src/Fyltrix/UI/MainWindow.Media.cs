using System.Windows;
using Fyltrix.Core;
using Fyltrix.Core.Imaging;

namespace Fyltrix.UI;

/// <summary>Preview fullscreen and the image editor.</summary>
public sealed partial class MainWindow
{
    private (WindowState state, WindowStyle style, ResizeMode resize, GridLength sidebar, GridLength list, double listMin, GridLength preview, bool previewVisible)? _fullScreen;

    /// <summary>Fills the screen with the preview pane (video/image fullscreen), hiding everything else; restores it exactly afterwards.</summary>
    private void SetPreviewFullScreen(bool on)
    {
        if (on == (_fullScreen != null)) return;
        if (on)
        {
            _fullScreen = (WindowState, WindowStyle, ResizeMode, _sidebarCol.Width, _listCol.Width, _listCol.MinWidth, _previewCol.Width, Settings.PreviewVisible);
            foreach (var e in _chrome) e.Visibility = Visibility.Collapsed;
            _sidebarCol.Width = new GridLength(0);
            _sidebarCol.MinWidth = 0;
            _sidebarSplitCol.Width = new GridLength(0);
            _listCol.MinWidth = 0;
            _listCol.Width = new GridLength(0);
            _previewSplitCol.Width = new GridLength(0);
            _previewCol.Width = new GridLength(1, GridUnitType.Star);
            WindowState = WindowState.Normal;
            RemoveWindowChrome(); // full screen covers the whole monitor (taskbar included), which the custom frame would prevent
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            var s = _fullScreen!.Value;
            _fullScreen = null;
            foreach (var e in _chrome) e.Visibility = Visibility.Visible;
            _sidebarCol.MinWidth = 120;
            _sidebarCol.Width = s.sidebar;
            _sidebarSplitCol.Width = new GridLength(4);
            _listCol.Width = s.list;
            _listCol.MinWidth = s.listMin;
            _previewSplitCol.Width = new GridLength(s.previewVisible ? 4 : 0);
            _previewCol.Width = s.preview;
            WindowState = WindowState.Normal;
            WindowStyle = s.style;
            ResizeMode = s.resize;
            ApplyWindowChrome();
            WindowState = s.state;
            UpdateWindowFrame();
            _preview.FullScreenEnded();
        }
    }

    private void OpenImageTools(string path)
    {
        if (!ImageService.CanDecode(path) && !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("This file type can't be edited.");
            return;
        }
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) { SetStatus("SVG is a vector format; open it in an editor to change it."); return; }
        var w = new ImageToolsWindow(this, path);
        w.Saved += (_, saved) =>
        {
            _undo.Push(new CreatedUndo($"Create '{Path.GetFileName(saved)}'", new[] { saved }));
            var dir = Path.GetDirectoryName(saved)!;
            RefreshFolders(new[] { dir });
            if (View != null && string.Equals(View.CurrentPath.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                _ = View.Refresh(new[] { saved });
            SetStatus($"Saved '{Path.GetFileName(saved)}'.");
        };
        w.Show();
    }
}
