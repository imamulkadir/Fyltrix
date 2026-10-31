using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Fyltrix.Core;
using Fyltrix.Core.Imaging;

namespace Fyltrix.UI;

/// <summary>
/// Rotate, flip, crop, resize and convert an image. Always saves to a new file; the original is never changed.
/// </summary>
public sealed class ImageToolsWindow : Window
{
    private readonly string _source;
    private BitmapSource? _image;
    private readonly Stack<BitmapSource> _history = new();
    private readonly Image _view = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent };
    private readonly Grid _surface = new();
    private readonly Rectangle _cropRect = new() { Stroke = Theme.Brush("Accent"), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 90, 169, 255)), Visibility = Visibility.Collapsed };
    private readonly TextBlock _dims = new() { Foreground = Theme.Brush("Fg2"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _width = new() { Width = 80 }, _height = new() { Width = 80 }, _name = new();
    private readonly CheckBox _lockAspect = new() { Content = "Keep proportions", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _format;
    private readonly Slider _quality = new() { Minimum = 10, Maximum = 100, Value = 92, Width = 160 };
    private readonly TextBlock _qualityText = new() { Foreground = Theme.Brush("Fg2"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _qualityRow = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _note = new() { Foreground = Theme.Brush("Fg3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _applyCrop, _undoBtn, _save;
    private bool _cropMode, _syncing;
    private Point? _dragStart;

    public event EventHandler<string>? Saved;

    public ImageToolsWindow(Window owner, string path)
    {
        _source = path;
        Owner = owner;
        Title = "Edit image – " + System.IO.Path.GetFileName(path);
        Width = 1150;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Bg1");
        Foreground = Theme.Brush("Fg1");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        SourceInitialized += (_, _) => Shell.UseDarkTitleBar(this);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        // ---------- image surface (Viewbox maps screen to pixel coordinates for cropping) ----------
        _surface.Children.Add(_view);
        _surface.Children.Add(_overlay);
        _overlay.Children.Add(_cropRect);
        RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);
        var viewbox = new Viewbox { Child = _surface, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16) };
        var stage = new Border { Background = Theme.Brush("Bg0"), Child = viewbox };
        root.Children.Add(stage);
        _overlay.MouseLeftButtonDown += OnCropDown;
        _overlay.MouseMove += OnCropMove;
        _overlay.MouseLeftButtonUp += (_, _) => { _dragStart = null; _overlay.ReleaseMouseCapture(); };

        // ---------- tools ----------
        var tools = new StackPanel { Margin = new Thickness(16) };
        tools.Children.Add(Dialogs.Heading("Edit image"));
        tools.Children.Add(_dims);

        tools.Children.Add(Dialogs.Label("Rotate and flip"));
        var row1 = new WrapPanel();
        row1.Children.Add(Tool("", "Rotate left", () => Apply(b => ImageService.Rotate(b, -90))));
        row1.Children.Add(Tool("", "Rotate right", () => Apply(b => ImageService.Rotate(b, 90))));
        row1.Children.Add(Tool("", "Flip horizontal", () => Apply(b => ImageService.Flip(b, true))));
        row1.Children.Add(Tool("", "Flip vertical", () => Apply(b => ImageService.Flip(b, false))));
        tools.Children.Add(row1);

        tools.Children.Add(Dialogs.Label("Crop"));
        var row2 = new WrapPanel();
        var cropBtn = Tool("", "Select area", ToggleCrop);
        _applyCrop = Tool("", "Apply crop", ApplyCrop);
        _applyCrop.IsEnabled = false;
        row2.Children.Add(cropBtn);
        row2.Children.Add(_applyCrop);
        tools.Children.Add(row2);

        tools.Children.Add(Dialogs.Label("Resize (pixels)"));
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        sizeRow.Children.Add(_width);
        sizeRow.Children.Add(new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("Fg2") });
        sizeRow.Children.Add(_height);
        var applySize = Dialogs.Button("Apply");
        applySize.Click += (_, _) => ApplyResize();
        sizeRow.Children.Add(applySize);
        tools.Children.Add(sizeRow);
        tools.Children.Add(_lockAspect);
        var pct = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var p in new[] { 75, 50, 25 })
        {
            var b = Dialogs.Button(p + "%");
            b.MinWidth = 54;
            b.Margin = new Thickness(0, 0, 6, 0);
            int percent = p;
            b.Click += (_, _) => { if (_image != null) Apply(i => ImageService.Resize(i, i.PixelWidth * percent / 100, i.PixelHeight * percent / 100)); };
            pct.Children.Add(b);
        }
        tools.Children.Add(pct);
        _width.TextChanged += (_, _) => SyncAspect(fromWidth: true);
        _height.TextChanged += (_, _) => SyncAspect(fromWidth: false);

        // ---------- output ----------
        var ext = System.IO.Path.GetExtension(path);
        var srcFormat = ImageService.FormatFromExtension(ext) ?? ImageFormat.Png;
        _format = Dialogs.Combo(new[]
        {
            ("PNG — lossless", ImageFormat.Png), ("JPEG — smaller, lossy", ImageFormat.Jpeg), ("WebP — lossy or lossless", ImageFormat.WebP),
            ("TIFF — lossless", ImageFormat.Tiff), ("BMP", ImageFormat.Bmp), ("GIF (256 colours)", ImageFormat.Gif), ("ICO — Windows icon", ImageFormat.Ico),
        }, srcFormat);
        tools.Children.Add(Dialogs.Field("Save as", _format));
        _qualityRow.Children.Add(_quality);
        _qualityRow.Children.Add(_qualityText);
        tools.Children.Add(Dialogs.Field("Quality", _qualityRow));
        _quality.ValueChanged += (_, _) => UpdateOutputNote();
        _format.SelectionChanged += (_, _) => { UpdateOutputNote(); UpdateName(); };
        tools.Children.Add(_note);
        tools.Children.Add(Dialogs.Field("File name (saved next to the original)", _name));

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        _undoBtn = Dialogs.Button("Undo");
        _undoBtn.Margin = new Thickness(0, 0, 6, 0);
        _undoBtn.Click += (_, _) => { if (_history.Count > 0) { _image = _history.Pop(); ShowImage(); } };
        var reset = Dialogs.Button("Reset");
        reset.Margin = new Thickness(0, 0, 6, 0);
        reset.Click += (_, _) => { if (_history.Count > 0) { var first = _history.Last(); _history.Clear(); _image = first; ShowImage(); } };
        _save = Dialogs.Button("Save as new file", primary: true);
        _save.Margin = new Thickness(0, 0, 6, 0);
        _save.Click += async (_, _) => await SaveAsync();
        buttons.Children.Add(_save);
        buttons.Children.Add(_undoBtn);
        buttons.Children.Add(reset);
        tools.Children.Add(buttons);

        var side = new ScrollViewer { Content = tools, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Brush("Bg2") };
        Grid.SetColumn(side, 1);
        root.Children.Add(side);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (_cropMode) ToggleCrop(); else Close(); e.Handled = true; }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { _undoBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); e.Handled = true; }
        };
        UpdateOutputNote();
        UpdateName();
        Loaded += async (_, _) => await LoadAsync();
    }

    internal BitmapSource? Current => _image;

    private static Button Tool(string glyph, string text, Action a)
    {
        var b = Theme.SmallButton(glyph, text, text);
        b.Margin = new Thickness(0, 0, 6, 6);
        b.Background = Theme.Brush("Bg3");
        b.Click += (_, _) => a();
        return b;
    }

    private async Task LoadAsync()
    {
        _dims.Text = "Loading…";
        try
        {
            _image = await Task.Run(() => ImageService.Load(_source));
            ShowImage();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _dims.Text = "This image can't be opened for editing: " + ex.Message;
            _save.IsEnabled = false;
        }
    }

    private void ShowImage()
    {
        if (_image == null) return;
        _view.Source = _image;
        _surface.Width = _image.PixelWidth;
        _surface.Height = _image.PixelHeight;
        _overlay.Width = _image.PixelWidth;
        _overlay.Height = _image.PixelHeight;
        _dims.Text = $"{_image.PixelWidth} × {_image.PixelHeight} px" + (_history.Count > 0 ? $" · {_history.Count} change{(_history.Count == 1 ? "" : "s")}" : "");
        _syncing = true;
        _width.Text = _image.PixelWidth.ToString();
        _height.Text = _image.PixelHeight.ToString();
        _syncing = false;
        _undoBtn.IsEnabled = _history.Count > 0;
        if (_cropMode) ToggleCrop();
    }

    /// <summary>Applies an edit; keeps up to 10 earlier states for Undo.</summary>
    internal void Apply(Func<BitmapSource, BitmapSource> edit)
    {
        if (_image == null) return;
        _history.Push(_image);
        if (_history.Count > 10)
        {
            var keep = _history.Take(10).Reverse().ToList();
            _history.Clear();
            foreach (var k in keep) _history.Push(k);
        }
        _image = edit(_image);
        ShowImage();
    }

    private void SyncAspect(bool fromWidth)
    {
        if (_syncing || _image == null || _lockAspect.IsChecked != true) return;
        _syncing = true;
        double ratio = (double)_image.PixelHeight / _image.PixelWidth;
        if (fromWidth && int.TryParse(_width.Text, out var w) && w > 0) _height.Text = Math.Max(1, (int)Math.Round(w * ratio)).ToString();
        if (!fromWidth && int.TryParse(_height.Text, out var h) && h > 0) _width.Text = Math.Max(1, (int)Math.Round(h / ratio)).ToString();
        _syncing = false;
    }

    private void ApplyResize()
    {
        if (!int.TryParse(_width.Text, out var w) || !int.TryParse(_height.Text, out var h) || w < 1 || h < 1 || w > 30000 || h > 30000)
        {
            Dialogs.Error(this, "Resize", "Enter a width and height between 1 and 30000 pixels.");
            return;
        }
        Apply(b => ImageService.Resize(b, w, h));
    }

    private void ToggleCrop()
    {
        _cropMode = !_cropMode;
        _overlay.Cursor = _cropMode ? Cursors.Cross : null;
        _cropRect.Visibility = Visibility.Collapsed;
        _applyCrop.IsEnabled = false;
    }

    private void OnCropDown(object sender, MouseButtonEventArgs e)
    {
        if (!_cropMode) return;
        _dragStart = e.GetPosition(_overlay);
        _overlay.CaptureMouse();
        Canvas.SetLeft(_cropRect, _dragStart.Value.X);
        Canvas.SetTop(_cropRect, _dragStart.Value.Y);
        _cropRect.Width = _cropRect.Height = 0;
        _cropRect.Visibility = Visibility.Visible;
    }

    private void OnCropMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } s || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_overlay);
        p.X = Math.Clamp(p.X, 0, _overlay.Width);
        p.Y = Math.Clamp(p.Y, 0, _overlay.Height);
        Canvas.SetLeft(_cropRect, Math.Min(s.X, p.X));
        Canvas.SetTop(_cropRect, Math.Min(s.Y, p.Y));
        _cropRect.Width = Math.Abs(p.X - s.X);
        _cropRect.Height = Math.Abs(p.Y - s.Y);
        _applyCrop.IsEnabled = _cropRect.Width >= 2 && _cropRect.Height >= 2;
        _dims.Text = $"{_image?.PixelWidth} × {_image?.PixelHeight} px · selection {(int)_cropRect.Width} × {(int)_cropRect.Height}";
    }

    private void ApplyCrop()
    {
        if (_cropRect.Visibility != Visibility.Visible) return;
        var r = new Int32Rect((int)Canvas.GetLeft(_cropRect), (int)Canvas.GetTop(_cropRect), (int)_cropRect.Width, (int)_cropRect.Height);
        Apply(b => ImageService.Crop(b, r));
    }

    internal void SetCropRect(Int32Rect r)
    {
        if (!_cropMode) ToggleCrop();
        Canvas.SetLeft(_cropRect, r.X);
        Canvas.SetTop(_cropRect, r.Y);
        _cropRect.Width = r.Width;
        _cropRect.Height = r.Height;
        _cropRect.Visibility = Visibility.Visible;
        _applyCrop.IsEnabled = true;
    }

    internal void ApplyCropForTest() => ApplyCrop();

    private ImageFormat SelectedFormat => Dialogs.Selected<ImageFormat>(_format);

    private void UpdateOutputNote()
    {
        var f = SelectedFormat;
        bool lossy = f == ImageFormat.Jpeg || (f == ImageFormat.WebP && _quality.Value < 100);
        _qualityRow.IsEnabled = f is ImageFormat.Jpeg or ImageFormat.WebP;
        _qualityText.Text = f == ImageFormat.WebP && _quality.Value >= 100 ? "lossless" : $"{(int)_quality.Value}";
        _note.Text = f switch
        {
            ImageFormat.Jpeg => "JPEG is lossy: every save loses some detail. Transparency becomes white.",
            ImageFormat.WebP => lossy ? "Lossy WebP. Set quality to 100 for lossless." : "Lossless WebP: no detail is lost.",
            ImageFormat.Gif => "GIF keeps only 256 colours; photos will look banded.",
            ImageFormat.Ico => "Saves sizes 16–256 px in one icon file.",
            ImageFormat.Bmp => "Uncompressed; transparency becomes white.",
            _ => "Lossless: no detail is lost.",
        };
    }

    private void UpdateName()
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(_source);
        _name.Text = stem + " (edited)" + ImageService.Extension(SelectedFormat);
    }

    internal async Task<string?> SaveAsync()
    {
        using var updateWork = UpdateWork.Track();
        if (_image == null) return null;
        var name = _name.Text.Trim();
        var err = FileOps.ValidateName(name);
        if (err != null) { Dialogs.Error(this, "Save", err); return null; }
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_source)!, name);
        if (FileOps.Exists(target)) { Dialogs.Error(this, "Save", $"'{name}' already exists. Choose another name."); return null; }
        var img = _image;
        var format = SelectedFormat;
        int quality = (int)_quality.Value;
        _save.IsEnabled = false;
        try
        {
            await Task.Run(() => ImageService.Save(img, target, format, quality, metadataFrom: _source));
            Saved?.Invoke(this, target);
            _dims.Text = $"Saved {name} ({FileOps.FormatSize(new FileInfo(target).Length)}).";
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or DllNotFoundException or TypeInitializationException)
        {
            Dialogs.Error(this, "Save", "The image couldn't be saved: " + ex.Message);
            return null;
        }
        finally { _save.IsEnabled = true; }
    }

    internal void SelectFormat(ImageFormat f)
    {
        foreach (ComboBoxItem i in _format.Items) if (i.Tag is ImageFormat x && x == f) _format.SelectedItem = i;
    }
}
