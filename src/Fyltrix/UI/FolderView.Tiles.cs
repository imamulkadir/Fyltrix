using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Fyltrix.Core;

namespace Fyltrix.UI;

public enum ViewMode { Details, Tiles }

/// <summary>
/// The thumbnail grid: the same ListView (selection, Ctrl/Shift-click, drag and drop, context menu, double-click,
/// inline rename all unchanged) with a virtualizing wrap panel, a tile template and grid-aware arrow keys.
/// </summary>
public sealed partial class FolderView
{
    public static readonly int[] TileSizes = { 48, 96, 160, 256 };

    public ViewMode Mode { get; private set; } = ViewMode.Details;
    public int TileSize { get; private set; } = 96;

    /// <summary>The user changed the tile size with Ctrl+wheel; the host remembers it.</summary>
    public event EventHandler<int>? TileSizeChanged;

    private ItemsPanelTemplate? _detailsPanel;
    private bool _tilesWired;

    internal VirtualizingWrapPanel? TilePanel => Mode == ViewMode.Tiles ? VisualTree.FindDescendant<VirtualizingWrapPanel>(List) : null;

    public void SetViewMode(ViewMode mode, int tileSize)
    {
        tileSize = Math.Clamp(tileSize, TileSizes[0], TileSizes[^1]);
        if (!_tilesWired)
        {
            _tilesWired = true;
            List.PreviewKeyDown += OnTileKey;
            List.PreviewMouseWheel += OnTileWheel;
        }
        if (mode == Mode && (mode == ViewMode.Details || tileSize == TileSize)) { TileSize = tileSize; return; }
        var selected = SelectedItems;
        var focused = FocusedItem;
        _detailsPanel ??= List.ItemsPanel;
        Mode = mode;
        TileSize = tileSize;
        // date groups: WPF groups in Details, the tile panel's own headings in the grid. Taken off while the panel changes
        // (WPF groups inside the tile panel, or headings in Details, would be wrong), put back afterwards.
        bool grouped = IsGroupedByDate;
        if (grouped)
        {
            CollectionViewSource.GetDefaultView(Items)?.GroupDescriptions.Clear();
            VirtualizingWrapPanel.SetGroupKey(List, null);
        }

        if (mode == ViewMode.Tiles)
        {
            double w = tileSize + 28, h = tileSize + (tileSize < 64 ? 40 : 52);
            var panel = new FrameworkElementFactory(typeof(VirtualizingWrapPanel));
            panel.SetValue(VirtualizingWrapPanel.ItemWidthProperty, w);
            panel.SetValue(VirtualizingWrapPanel.ItemHeightProperty, h);
            List.View = null;
            List.ItemsPanel = new ItemsPanelTemplate(panel);
            List.ItemTemplate = TileTemplate(tileSize);
            List.ItemContainerStyle = TileContainerStyle();
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
            ScrollViewer.SetCanContentScroll(List, true); // the panel scrolls itself (IScrollInfo)
        }
        else
        {
            List.ItemContainerStyle = null; // back to the theme's details row style
            List.ItemTemplate = null;
            List.ItemsPanel = _detailsPanel;
            List.View = _grid;
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Auto);
        }
        if (grouped) ApplyGrouping();

        // keep what was selected, and keep it in view
        List.SelectedItems.Clear();
        foreach (var i in selected) List.SelectedItems.Add(i);
        var anchor = focused ?? selected.FirstOrDefault();
        if (anchor != null)
        {
            List.ScrollIntoView(anchor);
            if (List.IsKeyboardFocusWithin) FocusItem(anchor);
        }
    }

    private Style TileContainerStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.MarginProperty, new Thickness(3));
        bd.SetValue(Border.PaddingProperty, new Thickness(4));
        bd.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        bd.AppendChild(cp);
        var template = new ControlTemplate(typeof(ListViewItem)) { VisualTree = bd };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush("Hover"), "Bd"));
        var selected = new MultiTrigger();
        selected.Conditions.Add(new Condition(ListBoxItem.IsSelectedProperty, true));
        selected.Conditions.Add(new Condition(Selector.IsSelectionActiveProperty, true));
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush("Sel"), "Bd"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("Accent"), "Bd"));
        var selectedInactive = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedInactive.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush("SelInactive"), "Bd"));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush("Fg3"), "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(selectedInactive);
        template.Triggers.Add(selected);
        template.Triggers.Add(focus);

        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.Brush("Fg1")));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(FileItem.Tip))));
        return style;
    }

    private DataTemplate TileTemplate(int size)
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(DockPanel.LastChildFillProperty, true);
        root.SetBinding(UIElement.OpacityProperty, new Binding(nameof(FileItem.ItemOpacity)));

        // name under the picture: up to two lines, then an ellipsis (full name in the tooltip)
        var nameCell = new FrameworkElementFactory(typeof(Grid));
        nameCell.SetValue(DockPanel.DockProperty, Dock.Bottom);
        nameCell.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileItem.DisplayName)));
        text.SetValue(TextBlock.TextWrappingProperty, size < 64 ? TextWrapping.NoWrap : TextWrapping.Wrap);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(TextBlock.FontSizeProperty, size < 64 ? 11.5 : 12.5);
        text.SetValue(FrameworkElement.MaxHeightProperty, size < 64 ? 18.0 : 36.0);
        text.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.IsEditing)) { Converter = new BoolToVisibility { Invert = true } });
        nameCell.AppendChild(text);
        var box = new FrameworkElementFactory(typeof(TextBox));
        box.SetValue(FrameworkElement.TagProperty, "rename");
        box.SetValue(TextBox.TextAlignmentProperty, TextAlignment.Center);
        box.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
        box.SetValue(Control.PaddingProperty, new Thickness(1, 0, 1, 0));
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(FileItem.EditName)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.IsEditing)) { Converter = new BoolToVisibility() });
        box.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRenameKey));
        box.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnRenameLostFocus));
        nameCell.AppendChild(box);
        // drives (This PC): the usage bar under the name (free space and totals in the tooltip)
        var bar = UsageBar();
        bar.SetValue(DockPanel.DockProperty, Dock.Bottom);
        bar.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 0));
        bar.SetValue(FrameworkElement.HeightProperty, 5.0);
        bar.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.HasUsage)) { Converter = new BoolToVisibility() });
        root.AppendChild(bar);
        root.AppendChild(nameCell);

        var picture = new FrameworkElementFactory(typeof(Grid));
        picture.SetValue(FrameworkElement.WidthProperty, (double)size);
        picture.SetValue(FrameworkElement.HeightProperty, (double)size);
        picture.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        var thumb = new FrameworkElementFactory(typeof(ThumbnailImage));
        thumb.SetValue(ThumbnailImage.SizeProperty, (double)size);
        thumb.SetBinding(ThumbnailImage.ItemProperty, new Binding());
        picture.AppendChild(thumb);
        var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        dot.SetValue(FrameworkElement.WidthProperty, 10.0);
        dot.SetValue(FrameworkElement.HeightProperty, 10.0);
        dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        dot.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Theme.Brush("Bg1"));
        dot.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5);
        dot.SetBinding(System.Windows.Shapes.Shape.FillProperty, new Binding(nameof(FileItem.LabelBrush)));
        dot.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.HasLabel)) { Converter = new BoolToVisibility() });
        picture.AppendChild(dot);
        root.AppendChild(picture);
        return new DataTemplate { VisualTree = root };
    }

    /// <summary>Arrow keys move across and down the grid; Shift extends, Ctrl moves focus only. Home/End/PageUp/PageDown too.</summary>
    private void OnTileKey(object sender, KeyEventArgs e)
    {
        if (Mode != ViewMode.Tiles || e.OriginalSource is TextBox) return;
        var items = List.Items;
        if (items.Count == 0) return;
        var panel = TilePanel;
        int cols = panel?.Columns ?? 1;
        int rowsPerPage = panel == null ? 1 : Math.Max(1, (int)(panel.ViewportHeight / panel.ItemHeight));
        int current = Keyboard.FocusedElement is ListViewItem lvi && ItemsControl.ItemsControlFromItemContainer(lvi) == List
            ? List.ItemContainerGenerator.IndexFromContainer(lvi)
            : _tileCurrent >= 0 && _tileCurrent < items.Count && List.SelectedItems.Contains(items[_tileCurrent]) ? _tileCurrent : List.SelectedIndex;
        if (current < 0) current = 0;
        int target = e.Key switch
        {
            Key.Left => current - 1,
            Key.Right => current + 1,
            Key.Up => panel?.IndexAbove(current) ?? current - cols,
            Key.Down => panel?.IndexBelow(current) ?? current + cols,
            Key.Home => 0,
            Key.End => items.Count - 1,
            Key.PageUp => Repeat(current, rowsPerPage, i => panel?.IndexAbove(i) ?? i - cols),
            Key.PageDown => Repeat(current, rowsPerPage, i => panel?.IndexBelow(i) ?? i + cols),
            _ => int.MinValue,
        };
        if (target == int.MinValue) return;
        e.Handled = true;
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right && (target < 0 || target >= items.Count)) return;
        target = Math.Clamp(target, 0, items.Count - 1);
        var item = (FileItem)items[target];
        var mods = TestModifiers ?? Keyboard.Modifiers;
        _tileCurrent = target;
        if (mods.HasFlag(ModifierKeys.Shift))
        {
            int anchor = _tileAnchor >= 0 && _tileAnchor < items.Count ? _tileAnchor : current;
            List.SelectedItems.Clear();
            for (int i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++) List.SelectedItems.Add(items[i]);
            _tileAnchor = anchor;
        }
        else if (!mods.HasFlag(ModifierKeys.Control))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
            _tileAnchor = target;
        }
        List.ScrollIntoView(item);
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromIndex(target) is ListViewItem c) c.Focus();
    }

    /// <summary>Takes up to n steps, stopping at the first or last row.</summary>
    private static int Repeat(int from, int n, Func<int, int> step)
    {
        int at = from;
        for (int k = 0; k < n; k++)
        {
            int next = step(at);
            if (next < 0) break;
            at = next;
        }
        return at;
    }

    private int _tileAnchor = -1, _tileCurrent = -1;

    /// <summary>Tests set this instead of holding real modifier keys.</summary>
    internal static ModifierKeys? TestModifiers;

    /// <summary>Ctrl+wheel steps through the tile sizes (like Explorer).</summary>
    private void OnTileWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        int i = Array.IndexOf(TileSizes, TileSize);
        if (Mode == ViewMode.Details)
        {
            if (e.Delta > 0) { SetViewMode(ViewMode.Tiles, TileSizes[0]); TileSizeChanged?.Invoke(this, TileSize); }
            return;
        }
        if (i < 0) i = 1;
        int next = e.Delta > 0 ? i + 1 : i - 1;
        if (next < 0) { SetViewMode(ViewMode.Details, TileSize); TileSizeChanged?.Invoke(this, 0); return; }
        next = Math.Min(next, TileSizes.Length - 1);
        SetViewMode(ViewMode.Tiles, TileSizes[next]);
        TileSizeChanged?.Invoke(this, TileSize);
    }
}
