using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fyltrix.Core;
using Fyltrix.Core.Media;

namespace Fyltrix.UI;

/// <summary>
/// The picture of one tile: shows the item's small icon at once, asks <see cref="ThumbnailService"/> for the real
/// thumbnail only while the tile is on screen, and cancels the request when the tile is recycled or scrolled away.
/// </summary>
public sealed class ThumbnailImage : Grid
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(FileItem), typeof(ThumbnailImage),
        new PropertyMetadata(null, (d, _) => ((ThumbnailImage)d).Reload()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(ThumbnailImage),
        new PropertyMetadata(96.0, (d, _) => ((ThumbnailImage)d).Reload()));

    public FileItem? Item { get => (FileItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private readonly Image _icon = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _thumb = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private ThumbnailRequest? _pending;

    /// <summary>True once a real thumbnail (or large icon) is showing.</summary>
    public bool HasThumbnail => _thumb.Source != null;

    public ThumbnailImage()
    {
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_thumb, BitmapScalingMode.HighQuality);
        Children.Add(_icon);
        Children.Add(_thumb);
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => Cancel();
    }

    private void Cancel()
    {
        _pending?.Cancel();
        _pending = null;
    }

    private void Reload()
    {
        Cancel();
        var item = Item;
        _thumb.Source = null;
        _icon.Source = item?.Icon;
        double s = Size;
        _icon.Width = _icon.Height = Math.Min(s, Math.Max(16, s / 2.5));
        _icon.Visibility = Visibility.Visible;
        if (item == null || !IsLoaded || item.IsVirtual) return; // archive entries have no file on disk to thumbnail

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pixels = (int)Math.Ceiling(s * dpi);
        if (ThumbnailService.Shared.TryGetCached(item.FullPath, pixels, item.Modified, item.Size) is { } hit) { Show(hit); return; }
        bool video = !item.IsDirectory && FileKinds.KindFor(item.FullPath, false) is PreviewKind.Media or PreviewKind.NativeMedia && !FileKinds.IsAudio(item.FullPath);
        var request = new ThumbnailRequest
        {
            Path = item.FullPath, Size = pixels, Modified = item.Modified, Length = item.Size, IsVideo = video,
            Done = bmp => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(Item, item) && bmp != null) Show(bmp); }),
        };
        _pending = request;
        ThumbnailService.Shared.Request(request);
    }

    private void Show(BitmapSource bmp)
    {
        _pending = null;
        _thumb.Source = bmp;
        // never blow a small icon up past its real size (blurry); real thumbnails fill the tile
        double s = Size, dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _thumb.MaxWidth = Math.Min(s, bmp.PixelWidth / dpi * 1.0001);
        _thumb.MaxHeight = Math.Min(s, bmp.PixelHeight / dpi * 1.0001);
        _icon.Visibility = Visibility.Collapsed;
    }
}
