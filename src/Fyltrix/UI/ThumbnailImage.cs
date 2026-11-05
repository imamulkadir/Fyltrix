using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fyltrix.Core;
using Fyltrix.Core.Media;

namespace Fyltrix.UI;

/// <summary>
/// The picture of one tile: shows a sharp placeholder, asks <see cref="ThumbnailService"/> for the real
/// thumbnail only while the tile is on screen, and cancels the request when the tile is recycled or scrolled away.
/// </summary>
public sealed class ThumbnailImage : Grid
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(FileItem), typeof(ThumbnailImage),
        new PropertyMetadata(null, (d, _) => ((ThumbnailImage)d).Reload()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(ThumbnailImage),
        new PropertyMetadata(96.0, (d, _) => ((ThumbnailImage)d).Reload()));
    public static readonly DependencyProperty ImageFilesOnlyProperty = DependencyProperty.Register(nameof(ImageFilesOnly), typeof(bool), typeof(ThumbnailImage),
        new PropertyMetadata(false, (d, _) => ((ThumbnailImage)d).Reload()));
    public static readonly DependencyProperty IsInViewportProperty = DependencyProperty.RegisterAttached("IsInViewport", typeof(bool), typeof(ThumbnailImage),
        new PropertyMetadata(true, (d, _) =>
        {
            var image = d as ThumbnailImage ?? d.GetValue(ImageOwnerProperty) as ThumbnailImage;
            if (image?._pending != null) image._pending.IsVisible = GetIsInViewport(d);
        }));
    private static readonly DependencyProperty ImageOwnerProperty = DependencyProperty.RegisterAttached("ImageOwner", typeof(ThumbnailImage), typeof(ThumbnailImage));
    public static bool GetIsInViewport(DependencyObject item) => (bool)item.GetValue(IsInViewportProperty);
    public static void SetIsInViewport(DependencyObject item, bool value)
    { if (GetIsInViewport(item) != value) item.SetValue(IsInViewportProperty, value); }

    public FileItem? Item { get => (FileItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>Compact Details rows retain Shell icons except for image-file thumbnails.</summary>
    public bool ImageFilesOnly { get => (bool)GetValue(ImageFilesOnlyProperty); set => SetValue(ImageFilesOnlyProperty, value); }

    private readonly Image _icon = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _thumb = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _placeholder = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private static Brush? _placeholderBrush;
    private static ImageSource? _filePlaceholder, _folderPlaceholder;
    private ThumbnailRequest? _pending;
    private int _version;
    private ListViewItem? _container;
    internal ThumbnailService Service { get; set; } = ThumbnailService.Shared;

    /// <summary>True once a real thumbnail (or large icon) is showing.</summary>
    public bool HasThumbnail => _thumb.Source != null;

    public ThumbnailImage()
    {
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_thumb, BitmapScalingMode.HighQuality);
        Children.Add(_placeholder);
        Children.Add(_icon);
        Children.Add(_thumb);
        _placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        Loaded += (_, _) =>
        {
            _container = VisualTree.FindAncestor<ListViewItem>(this);
            _container?.SetValue(ImageOwnerProperty, this);
            Reload();
        };
        Unloaded += (_, _) =>
        {
            Cancel();
            _thumb.Source = null;
            if (_container?.GetValue(ImageOwnerProperty) == this) _container.ClearValue(ImageOwnerProperty);
            _container = null;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { Cancel(); _thumb.Source = null; }
            else if (IsLoaded) Reload();
        };
    }

    private void Cancel()
    {
        _version++;
        _pending?.Cancel();
        _pending = null;
    }

    private void Reload()
    {
        Cancel();
        var item = Item;
        _thumb.Source = null;
        _icon.Source = item?.Icon;
        double s = Size, dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _placeholder.Source = Placeholder(item?.IsDirectory == true);
        _placeholder.Width = _placeholder.Height = Math.Clamp(s / 2.5, 16, 96);
        _placeholder.Visibility = Visibility.Visible;
        _icon.Visibility = Visibility.Collapsed;
        if (item?.Icon is BitmapSource icon && icon.PixelWidth / dpi >= s / 2.5)
        {
            _icon.Width = Math.Min(s / 2.5, icon.PixelWidth / dpi);
            _icon.Height = Math.Min(s / 2.5, icon.PixelHeight / dpi);
            _icon.Visibility = Visibility.Visible;
            _placeholder.Visibility = Visibility.Collapsed;
        }
        if (ImageFilesOnly)
        {
            _icon.Width = _icon.Height = s;
            _icon.Visibility = item?.Icon != null ? Visibility.Visible : Visibility.Collapsed;
            _placeholder.Visibility = item?.Icon != null ? Visibility.Collapsed : Visibility.Visible;
            if (item == null || item.IsDirectory || FileKinds.KindFor(item.FullPath, false) != PreviewKind.Image) return;
        }
        if (item == null || !IsLoaded || !IsVisible || item.IsVirtual) return; // hidden tabs and archive entries do not start disk work

        int pixels = PixelsFor(s, dpi), version = _version;
        if (Service.TryGetCached(item.FullPath, pixels, item.Modified, item.Size) is { } hit) { Show(hit); return; }
        bool video = !item.IsDirectory && FileKinds.KindFor(item.FullPath, false) is PreviewKind.Media or PreviewKind.NativeMedia && !FileKinds.IsAudio(item.FullPath);
        ThumbnailRequest? request = null;
        request = new ThumbnailRequest
        {
            Path = item.FullPath, Size = pixels, Modified = item.Modified, Length = item.Size, IsVideo = video,
            IsVisible = GetIsInViewport((DependencyObject?)_container ?? this),
            Done = bmp => Dispatcher.BeginInvoke(() =>
            {
                if (version == _version && IsLoaded && ReferenceEquals(_pending, request) && ReferenceEquals(Item, item) && bmp != null) Show(bmp);
            }),
        };
        _pending = request;
        Service.Request(request);
    }

    internal static int PixelsFor(double size, double dpi) => (int)Math.Clamp(Math.Ceiling(size * dpi), 1, 2048);

    private static ImageSource Placeholder(bool folder)
    {
        var brush = Theme.Brush("Fg2");
        if (!ReferenceEquals(brush, _placeholderBrush))
        {
            _placeholderBrush = brush;
            ImageSource Make(string glyph)
            {
                var font = Theme.Icon(glyph).FontFamily;
                var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 96, brush, 1);
                var image = new DrawingImage(new GeometryDrawing(brush, null, text.BuildGeometry(new Point())));
                image.Freeze(); return image;
            }
            _filePlaceholder = Make("\uE8A5"); _folderPlaceholder = Make("\uE8B7");
        }
        return (folder ? _folderPlaceholder : _filePlaceholder)!;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); Reload(); }

    private void Show(BitmapSource bmp)
    {
        _pending = null;
        _thumb.Source = bmp;
        // never blow a small icon up past its real size (blurry); real thumbnails fill the tile
        double s = Size, dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _thumb.MaxWidth = Math.Min(s, bmp.PixelWidth / dpi * 1.0001);
        _thumb.MaxHeight = Math.Min(s, bmp.PixelHeight / dpi * 1.0001);
        _icon.Visibility = Visibility.Collapsed;
        _placeholder.Visibility = Visibility.Collapsed;
    }
}
