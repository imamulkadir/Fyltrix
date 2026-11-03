using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>Background-only selection. Item clicks and native file dragging keep their existing handlers.</summary>
public sealed partial class FolderView
{
    private enum MarqueeState { Idle, Pending, Selecting }
    private MarqueeState _marqueeState;
    private readonly Canvas _marqueeLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Rectangle _marqueeRectangle = new() { Visibility = Visibility.Collapsed, StrokeThickness = 1 };
    private DispatcherTimer? _marqueeTimer;
    private ScrollViewer? _marqueeScroll;
    private ScrollContentPresenter? _marqueeViewport;
    private FileItem[] _marqueeItems = Array.Empty<FileItem>();
    private Rect[] _marqueeBounds = Array.Empty<Rect>();
    private Dictionary<FileItem, int> _marqueeIndices = new();
    private HashSet<FileItem> _marqueeInitial = new(), _marqueeApplied = new();
    private Point _marqueeStart, _marqueePointer, _marqueeAnchor;
    private Point _marqueeOrigin;
    private Rect _marqueeLastRectangle = Rect.Empty;
    private ModifierKeys _marqueeModifiers;
    private bool _marqueeCapture, _marqueeUpdating;
    internal bool MarqueePending => _marqueeState != MarqueeState.Idle;
    internal bool MarqueeVisible => _marqueeRectangle.Visibility == Visibility.Visible;
    internal bool MarqueeAutoScrolling => _marqueeTimer?.IsEnabled == true;

    private void InitializeMarquee()
    {
        var accent = Theme.Brush("Accent");
        _marqueeRectangle.Stroke = accent;
        var fill = accent.Clone();
        fill.Opacity = .18;
        _marqueeRectangle.Fill = fill;
        _marqueeLayer.Children.Add(_marqueeRectangle);
        Children.Add(_marqueeLayer);
        List.LostMouseCapture += (_, e) => { if (ReferenceEquals(e.OriginalSource, List)) CancelMarquee(); };
        List.MouseLeave += (_, _) => { if (_marqueeState == MarqueeState.Pending) CancelMarquee(); };
        List.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && MarqueePending) { CancelMarquee(); e.Handled = true; }
        };
        Unloaded += (_, _) => CancelMarquee();
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            if (_marqueeState == MarqueeState.Selecting)
            {
                _marqueeOrigin = MarqueeOrigin();
                UpdateMarquee();
            }
        }));
        List.SizeChanged += (_, _) => CancelMarquee();
    }

    // Tests call these same gesture methods with capture=false; no desktop input/capture is synthesized.
    internal bool BeginMarquee(Point point, ModifierKeys modifiers, bool capture = true)
    {
        CancelMarquee();
        var source = List.InputHitTest(point) as DependencyObject;
        if (IsChrome(source) || VisualTree.FindAncestor<TextBox>(source) != null ||
            ItemAt(source, point) != null || Items.Any(i => i.IsEditing)) return false;
        _marqueeScroll = VisualTree.FindDescendant<ScrollViewer>(List);
        // GridView has a second ScrollContentPresenter for column headers; use the listing viewport.
        _marqueeViewport = _marqueeScroll == null ? null : VisualTree.FindDescendants<ScrollContentPresenter>(_marqueeScroll)
            .OrderByDescending(p => p.ActualHeight).FirstOrDefault();
        if (_marqueeViewport == null || !MarqueeViewport().Contains(point)) return false;
        List.UpdateLayout();
        _marqueeItems = List.Items.Cast<FileItem>().ToArray();
        _marqueeIndices = _marqueeItems.Select((item, index) => (item, index)).ToDictionary(p => p.item, p => p.index);
        _marqueeBounds = new Rect[_marqueeItems.Length];
        if (!BuildMarqueeGeometry()) { ClearMarquee(); return false; }
        _marqueeInitial = SelectedItems.ToHashSet();
        _marqueeApplied = new HashSet<FileItem>(_marqueeInitial);
        _marqueeModifiers = modifiers;
        _marqueeCapture = capture;
        _marqueeStart = _marqueePointer = point;
        _marqueeOrigin = MarqueeOrigin();
        _marqueeAnchor = point - (Vector)_marqueeOrigin;
        _marqueeState = MarqueeState.Pending;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) ApplyMarqueeSelection(new());
        List.Focus();
        return true;
    }

    private Rect MarqueeViewport() => _marqueeViewport == null ? Rect.Empty :
        new Rect(_marqueeViewport.TranslatePoint(new Point(), List), _marqueeViewport.RenderSize);

    private bool BuildMarqueeGeometry()
    {
        if (_marqueeItems.Length == 0) return true;
        if (TilePanel is { } panel)
        {
            for (int i = 0; i < _marqueeItems.Length; i++)
                _marqueeBounds[i] = new Rect(panel.Position(i), new Size(panel.ItemWidth, panel.ItemHeight));
            return true;
        }
        var rows = VisualTree.FindDescendants<ListViewItem>(List).Where(r => r.ActualHeight > 0 && r.DataContext is FileItem).ToArray();
        var row = rows.FirstOrDefault();
        if (row == null) return false;
        double height = row.ActualHeight, width = _grid.Columns.Sum(c => c.ActualWidth), y = 0;
        double stride = row.DesiredSize.Height;
        // Layout rounding/margins can make row pitch differ from ActualHeight. Sample adjacent
        // rows in the same group instead of accumulating a pixel error over a long scroll.
        for (int i = 1; i < rows.Length; i++)
        {
            var a = (FileItem)rows[i - 1].DataContext;
            var b = (FileItem)rows[i].DataContext;
            if (_marqueeIndices[b] != _marqueeIndices[a] + 1 ||
                (IsGroupedByDate && DateGroups.Name(a.Modified, _groupNow) != DateGroups.Name(b.Modified, _groupNow))) continue;
            double delta = rows[i].TranslatePoint(new Point(), List).Y - rows[i - 1].TranslatePoint(new Point(), List).Y;
            if (delta > 0) { stride = delta; break; }
        }
        if (IsGroupedByDate && CollectionViewSource.GetDefaultView(Items).Groups is { } groups)
        {
            // Measure the existing header template once, without realizing offscreen groups/items.
            var header = (FrameworkElement)List.GroupStyle[0].HeaderTemplate.LoadContent();
            header.SetValue(TextElement.FontSizeProperty, List.FontSize);
            header.Measure(new Size(Math.Max(1, List.ActualWidth), double.PositiveInfinity));
            double dpi = VisualTreeHelper.GetDpi(List).DpiScaleY;
            double headerHeight = Math.Round(header.DesiredSize.Height * dpi) / dpi;
            foreach (CollectionViewGroup group in groups)
            {
                y += headerHeight;
                foreach (FileItem item in group.Items)
                {
                    if (_marqueeIndices.TryGetValue(item, out int index)) _marqueeBounds[index] = new Rect(0, y, width, height);
                    y += stride;
                }
            }
        }
        else for (int i = 0; i < _marqueeItems.Length; i++) _marqueeBounds[i] = new Rect(0, i * stride, width, height);
        return true;
    }

    private Point MarqueeOrigin()
    {
        if (TilePanel is { } panel)
            return panel.TranslatePoint(new Point(0, -panel.VerticalOffset), List);
        // A realized row calibrates logical WPF scrolling to pixels, including grouped headers.
        // This traversal occurs only on layout/scroll changes; pointer moves reuse the cached origin.
        foreach (var row in VisualTree.FindDescendants<ListViewItem>(List))
        {
            if (row.ActualHeight <= 0 || row.DataContext is not FileItem item || !_marqueeIndices.TryGetValue(item, out int index)) continue;
            var presenter = VisualTree.FindDescendant<GridViewRowPresenter>(row);
            if (presenter == null) continue;
            var p = row.TranslatePoint(new Point(), List);
            return new Point(presenter.TranslatePoint(new Point(), List).X, p.Y - _marqueeBounds[index].Y);
        }
        return MarqueeViewport().TopLeft;
    }

    internal void MoveMarquee(Point point)
    {
        if (!MarqueePending) return;
        _marqueePointer = point;
        if (_marqueeState == MarqueeState.Pending)
        {
            if (Math.Abs(point.X - _marqueeStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - _marqueeStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (_marqueeCapture && !List.CaptureMouse()) { CancelMarquee(); return; }
            _marqueeState = MarqueeState.Selecting;
            _marqueeRectangle.Visibility = Visibility.Visible;
        }
        try { UpdateMarquee(); }
        catch { CancelMarquee(); throw; }
    }

    private void UpdateMarquee()
    {
        if (_marqueeState != MarqueeState.Selecting) return;
        var viewport = MarqueeViewport();
        if (viewport.IsEmpty || viewport.Width <= 0 || viewport.Height <= 0) { CancelMarquee(); return; }
        var origin = _marqueeOrigin;
        var end = new Point(Math.Clamp(_marqueePointer.X, viewport.Left, viewport.Right), Math.Clamp(_marqueePointer.Y, viewport.Top, viewport.Bottom));
        var rectangle = new Rect(_marqueeAnchor, end - (Vector)origin);
        var visible = new Rect(_marqueeAnchor + (Vector)origin, end);
        visible.Intersect(viewport);
        var overlayPoint = visible.IsEmpty ? new Point() : List.TranslatePoint(visible.TopLeft, _marqueeLayer);
        Canvas.SetLeft(_marqueeRectangle, overlayPoint.X);
        Canvas.SetTop(_marqueeRectangle, overlayPoint.Y);
        _marqueeRectangle.Width = visible.IsEmpty ? 0 : visible.Width;
        _marqueeRectangle.Height = visible.IsEmpty ? 0 : visible.Height;
        if (rectangle != _marqueeLastRectangle)
        {
            var hits = new HashSet<FileItem>();
            // Bounds are ordered by row top. Skip unrelated rows with binary search, including offscreen rows.
            int lo = 0, hi = _marqueeBounds.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (_marqueeBounds[mid].Bottom <= rectangle.Top) lo = mid + 1; else hi = mid; }
            for (int i = lo; i < _marqueeBounds.Length && _marqueeBounds[i].Top < rectangle.Bottom; i++)
            {
                var intersection = Rect.Intersect(_marqueeBounds[i], rectangle);
                if (!intersection.IsEmpty && intersection.Width > 0 && intersection.Height > 0) hits.Add(_marqueeItems[i]);
            }
            // Shift adds the rectangle to the original selection; it never changes Shift-click/range behavior.
            if (_marqueeModifiers.HasFlag(ModifierKeys.Shift)) hits.UnionWith(_marqueeInitial);
            else if (_marqueeModifiers.HasFlag(ModifierKeys.Control)) hits.SymmetricExceptWith(_marqueeInitial);
            ApplyMarqueeSelection(hits);
            _marqueeLastRectangle = rectangle;
        }
        bool edge = _marqueeScroll != null && (
            (_marqueePointer.Y < viewport.Top + 24 && _marqueeScroll.VerticalOffset > 0) ||
            (_marqueePointer.Y > viewport.Bottom - 24 && _marqueeScroll.VerticalOffset < _marqueeScroll.ScrollableHeight) ||
            (_marqueePointer.X < viewport.Left + 24 && _marqueeScroll.HorizontalOffset > 0) ||
            (_marqueePointer.X > viewport.Right - 24 && _marqueeScroll.HorizontalOffset < _marqueeScroll.ScrollableWidth));
        if (edge)
        {
            _marqueeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) => ScrollMarquee(), Dispatcher);
            _marqueeTimer.Start();
        }
        else _marqueeTimer?.Stop();
    }

    private void ApplyMarqueeSelection(HashSet<FileItem> desired)
    {
        if (_marqueeApplied.SetEquals(desired)) return;
        _marqueeUpdating = true;
        try
        {
            foreach (var item in _marqueeApplied) if (!desired.Contains(item)) List.SelectedItems.Remove(item);
            foreach (var item in desired) if (!_marqueeApplied.Contains(item)) List.SelectedItems.Add(item);
            _marqueeApplied = desired;
        }
        finally { _marqueeUpdating = false; }
        _host.OnSelectionChanged(this);
    }

    internal void ScrollMarquee()
    {
        if (_marqueeState != MarqueeState.Selecting || _marqueeScroll == null) return;
        if (_marqueeCapture && Mouse.LeftButton != MouseButtonState.Pressed) { CancelMarquee(); return; }
        var viewport = MarqueeViewport();
        if (_marqueePointer.Y < viewport.Top + 24) _marqueeScroll.LineUp();
        else if (_marqueePointer.Y > viewport.Bottom - 24) _marqueeScroll.LineDown();
        if (_marqueePointer.X < viewport.Left + 24) _marqueeScroll.LineLeft();
        else if (_marqueePointer.X > viewport.Right - 24) _marqueeScroll.LineRight();
    }

    internal void FinishMarquee()
    {
        if (!MarqueePending) return;
        ClearMarquee();
    }

    internal void CancelMarquee()
    {
        if (!MarqueePending) return;
        try { ApplyMarqueeSelection(new HashSet<FileItem>(_marqueeInitial.Where(_marqueeIndices.ContainsKey))); }
        finally { ClearMarquee(); }
    }

    private void ClearMarquee()
    {
        _marqueeState = MarqueeState.Idle;
        _marqueeLastRectangle = Rect.Empty;
        _marqueeTimer?.Stop();
        _marqueeRectangle.Visibility = Visibility.Collapsed;
        bool release = _marqueeCapture && List.IsMouseCaptured;
        _marqueeCapture = false;
        if (release) List.ReleaseMouseCapture();
        _marqueeItems = Array.Empty<FileItem>();
        _marqueeBounds = Array.Empty<Rect>();
        _marqueeIndices.Clear();
        _marqueeInitial.Clear();
        _marqueeApplied.Clear();
        _marqueeScroll = null;
        _marqueeViewport = null;
        _dragItem = _pendingSingle = null;
    }
}
