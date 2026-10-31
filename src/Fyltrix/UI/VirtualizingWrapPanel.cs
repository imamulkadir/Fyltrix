using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Fyltrix.UI;

/// <summary>
/// A wrap panel for equally sized tiles that only creates containers for the rows on screen (plus one row above and
/// below) and recycles them while scrolling, so a folder with tens of thousands of items stays fast.
/// Vertical scrolling only; the number of columns follows the width.
/// Optional groups (<see cref="GroupKeyProperty"/> on the ItemsControl, items already sorted by group): each group starts a new
/// row under a heading drawn by the panel, like Explorer's "Today / Yesterday / Last week" in Downloads.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }

    /// <summary>Set on the ItemsControl: the group name of an item (null = no groups). Consecutive items with the same name form a group.</summary>
    public static readonly DependencyProperty GroupKeyProperty = DependencyProperty.RegisterAttached("GroupKey", typeof(Func<object, string>), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(null, (d, _) =>
        {
            if (VisualTree.FindDescendant<VirtualizingWrapPanel>(d) is { } p) { p._groupsDirty = true; p.InvalidateMeasure(); p.InvalidateVisual(); }
        }));
    public static void SetGroupKey(DependencyObject d, Func<object, string>? value) => d.SetValue(GroupKeyProperty, value);
    public static Func<object, string>? GetGroupKey(DependencyObject d) => (Func<object, string>?)d.GetValue(GroupKeyProperty);

    /// <summary>Height of a group heading band.</summary>
    public const double HeaderHeight = 38;

    /// <summary>A run of items [Start, Start + Count) under one heading; Top is where its heading starts.</summary>
    internal sealed record Group(string Name, int Start, int Count) { public double Top; public int Rows; }

    private List<Group> _groups = new();
    private bool _groupsDirty = true;
    private Func<object, string>? _groupsKey;
    private bool HasHeaders => _groupsKey != null;
    internal IReadOnlyList<Group> Groups => _groups;
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private Size _extent, _viewport;
    private Point _offset;
    private int _columns = 1;
    private int _first, _count; // realized item range

    /// <summary>Columns in the current layout (the grid's keyboard navigation moves by this many for Up/Down).</summary>
    public int Columns => _columns;
    public (int first, int count) Realized => (_first, _count);

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size available)
    {
        _ = InternalChildren; // WPF only hands out ItemContainerGenerator after the children collection has been created
        var owner = ItemsControl.GetItemsOwner(this);
        int count = ItemCount;
        double width = double.IsInfinity(available.Width) ? ItemWidth * 4 : available.Width;
        _columns = Math.Max(1, (int)(width / ItemWidth));
        double height = Layout(owner, count);
        var viewport = new Size(width, double.IsInfinity(available.Height) ? height : available.Height);
        var extent = new Size(width, height);
        UpdateScrollInfo(viewport, extent);

        // the items on screen plus one row above and below
        int first = Math.Min(count, IndexAtY(_offset.Y - ItemHeight));
        int last = Math.Min(count - 1, LastIndexAtY(_offset.Y + viewport.Height + ItemHeight));

        var generator = ItemContainerGenerator;
        if (owner == null || generator == null || count == 0)
        {
            RemoveAll();
            _first = _count = 0;
            return viewport;
        }
        CleanUp(first, last);

        var start = generator.GeneratorPositionFromIndex(first);
        int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (int i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out bool isNew);
                if (isNew || !InternalChildren.Contains(child))
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
        _first = first;
        _count = last >= first ? last - first + 1 : 0;
        return viewport;
    }

    protected override Size ArrangeOverride(Size final)
    {
        var generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            var p = Position(index);
            child.Arrange(new Rect(p.X, p.Y - _offset.Y, ItemWidth, ItemHeight));
        }
        return final;
    }

    // ---------------------------------------------------------------- group layout

    /// <summary>Works out the groups (when the items or the key changed) and where each starts; returns the total height.</summary>
    private double Layout(ItemsControl? owner, int count)
    {
        var key = owner == null ? null : GetGroupKey(owner);
        if (_groupsDirty || !ReferenceEquals(key, _groupsKey) || _groups.Sum(g => g.Count) != count)
        {
            _groupsDirty = false;
            _groupsKey = key;
            _groups = new List<Group>();
            if (key == null || owner == null) { if (count > 0) _groups.Add(new Group("", 0, count)); }
            else
            {
                var items = owner.Items;
                int start = 0;
                string? name = null;
                for (int i = 0; i < count; i++)
                {
                    var n = key(items[i]);
                    if (i > 0 && n != name) { _groups.Add(new Group(name!, start, i - start)); start = i; }
                    name = n;
                }
                if (count > 0) _groups.Add(new Group(name!, start, count - start));
            }
        }
        double y = 0, header = HasHeaders ? HeaderHeight : 0;
        bool moved = false;
        foreach (var g in _groups)
        {
            moved |= g.Top != y || g.Rows != (g.Count + _columns - 1) / _columns;
            g.Top = y;
            g.Rows = (g.Count + _columns - 1) / _columns;
            y += header + g.Rows * ItemHeight;
        }
        if (moved && HasHeaders) InvalidateVisual();
        return y;
    }

    private int GroupIndexOf(int index)
    {
        int lo = 0, hi = _groups.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_groups[mid].Start <= index) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Top-left of an item in panel coordinates (before scrolling).</summary>
    internal Point Position(int index)
    {
        if (_groups.Count == 0) return new Point(0, 0);
        var g = _groups[GroupIndexOf(index)];
        int local = index - g.Start;
        return new Point(local % _columns * ItemWidth, g.Top + (HasHeaders ? HeaderHeight : 0) + local / _columns * ItemHeight);
    }

    /// <summary>The first item of the first row that reaches below y.</summary>
    private int IndexAtY(double y)
    {
        double header = HasHeaders ? HeaderHeight : 0;
        foreach (var g in _groups)
        {
            if (y >= g.Top + header + g.Rows * ItemHeight) continue;
            int row = Math.Max(0, (int)Math.Floor((y - g.Top - header) / ItemHeight));
            return g.Start + Math.Min(g.Count - 1, row * _columns);
        }
        return Math.Max(0, ItemCount - 1);
    }

    /// <summary>The last item of the last row that starts above y.</summary>
    private int LastIndexAtY(double y)
    {
        double header = HasHeaders ? HeaderHeight : 0;
        int last = 0;
        foreach (var g in _groups)
        {
            if (y < g.Top + header) break;
            int row = Math.Min(g.Rows - 1, (int)Math.Floor((y - g.Top - header) / ItemHeight));
            last = g.Start + Math.Min(g.Count - 1, (row + 1) * _columns - 1);
        }
        return last;
    }

    /// <summary>Keyboard Up/Down: the same column in the row above/below, crossing into the neighbouring group as Explorer does;
    /// -1 when there is nowhere to go.</summary>
    public int IndexAbove(int index) => Step(index, -1);
    public int IndexBelow(int index) => Step(index, +1);

    private int Step(int index, int dir)
    {
        if (_groups.Count == 0 || index < 0) return -1;
        int gi = GroupIndexOf(index);
        var g = _groups[gi];
        int local = index - g.Start, row = local / _columns, col = local % _columns;
        int nextRow = row + dir;
        if (nextRow >= 0 && nextRow < g.Rows) return g.Start + Math.Min(g.Count - 1, nextRow * _columns + col);
        int ng = gi + dir;
        if (ng < 0 || ng >= _groups.Count) return -1;
        var n = _groups[ng];
        int r = dir > 0 ? 0 : n.Rows - 1;
        return n.Start + Math.Min(n.Count - 1, r * _columns + col);
    }

    /// <summary>Group headings: name, count and a line, as in Details view. They are drawn, not items, so a click there is background.</summary>
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!HasHeaders) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var plain = new Typeface("Segoe UI");
        var line = new Pen(Theme.Brush("Line"), 1);
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        foreach (var g in _groups)
        {
            double top = g.Top - _offset.Y;
            if (top + HeaderHeight < 0 || top > _viewport.Height) continue;
            var name = new FormattedText(g.Name, culture, FlowDirection.LeftToRight, bold, 13, Theme.Brush("Accent"), dip);
            var count = new FormattedText($" ({g.Count})", culture, FlowDirection.LeftToRight, plain, 13, Theme.Brush("Fg3"), dip);
            double textY = top + 14;
            dc.DrawText(name, new Point(8, textY));
            dc.DrawText(count, new Point(8 + name.WidthIncludingTrailingWhitespace, textY));
            double lineX = 8 + name.WidthIncludingTrailingWhitespace + count.WidthIncludingTrailingWhitespace + 10;
            double lineY = Math.Round(textY + name.Height / 2) + 0.5;
            if (lineX < ActualWidth - 8) dc.DrawLine(line, new Point(lineX, lineY), new Point(ActualWidth - 8, lineY));
        }
    }

    /// <summary>Gives back the containers outside [first, last] (recycled for reuse).</summary>
    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var recycler = generator as IRecyclingItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(pos);
            if (index >= first && index <= last && index >= 0) continue;
            if (InternalChildren[i] is UIElement e && e.IsKeyboardFocusWithin) continue; // keep the focused tile alive
            if (recycler != null && VirtualizingPanel.GetVirtualizationMode(ItemsControl.GetItemsOwner(this)) == VirtualizationMode.Recycling) recycler.Recycle(pos, 1);
            else generator.Remove(pos, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void RemoveAll()
    {
        if (InternalChildren.Count == 0) return;
        var generator = ItemContainerGenerator;
        if (generator != null) generator.RemoveAll();
        RemoveInternalChildRange(0, InternalChildren.Count);
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.Position.Index >= 0 && args.Position.Index < InternalChildren.Count)
                    RemoveInternalChildRange(args.Position.Index, Math.Min(args.ItemUICount, InternalChildren.Count - args.Position.Index));
                break;
            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }
        _groupsDirty = true;
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;
        if (_groups.Count == 0) return;
        double top = Position(index).Y, bottom = top + ItemHeight;
        // an item in the first row of a group brings its heading into view too
        if (HasHeaders && index - _groups[GroupIndexOf(index)].Start < _columns) top -= HeaderHeight;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
    }

    // ---------------------------------------------------------------- IScrollInfo (vertical)

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        bool changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;
        double max = Math.Max(0, extent.Height - viewport.Height);
        if (_offset.Y > max) { _offset.Y = max; changed = true; }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (Math.Abs(offset - _offset.Y) < 0.01) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
        if (HasHeaders) InvalidateVisual(); // headings move with the scroll position
    }

    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_offset.Y - ItemHeight / 3);
    public void LineDown() => SetVerticalOffset(_offset.Y + ItemHeight / 3);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - Math.Max(48, ItemHeight * 0.6));
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + Math.Max(48, ItemHeight * 0.6));
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual) && !(visual is DependencyObject d && InternalChildren[i].IsAncestorOf(d))) continue;
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0) BringIndexIntoView(index);
            break;
        }
        return rectangle;
    }
}
