using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>
/// Group by date modified (Today, Yesterday, Last week…) like Explorer's Downloads folder, in Details and in the thumbnail grid.
/// Details uses WPF's CollectionView grouping; the grid's own virtualizing panel draws the headings itself (WPF grouping would
/// replace that panel). Groups are in date order; the column sort applies inside each group.
/// </summary>
public sealed partial class FolderView
{
    /// <summary>Asked for each folder shown: should it be grouped by date? (MainWindow: Settings.GroupByDateFolders.)</summary>
    public Func<string, bool>? GroupByDateFor { get; set; }

    /// <summary>True while the list shows date groups.</summary>
    public bool IsGroupedByDate { get; private set; }

    private DateTime _groupNow = DateTime.Now;
    private DispatcherTimer? _midnight;
    private bool _groupStyleAdded;

    /// <summary>Whether this folder may be grouped at all (a real folder, not search results, an archive or This PC).</summary>
    public bool CanGroupByDate => !IsSearchResults && !IsArchive && !IsThisPC;

    private bool WantsDateGroups => CanGroupByDate && GroupByDateFor?.Invoke(CurrentPath) == true;

    /// <summary>Group rank first when grouping (newest group first; oldest first when sorted by date ascending), then the column sort.</summary>
    private int GroupRankCompare(FileItem a, FileItem b)
    {
        if (!WantsDateGroups) return 0;
        int r = DateGroups.Rank(a.Modified, _groupNow).CompareTo(DateGroups.Rank(b.Modified, _groupNow));
        return Sort == SortColumn.Modified && Ascending ? -r : r;
    }

    /// <summary>Called after the items are set: adds or removes the date groups.</summary>
    private void ApplyGrouping()
    {
        if (!_groupStyleAdded)
        {
            _groupStyleAdded = true;
            List.GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeaderTemplate() });
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(List, true);
        }
        var view = CollectionViewSource.GetDefaultView(Items);
        if (view == null) return;
        view.GroupDescriptions.Clear();
        IsGroupedByDate = WantsDateGroups;
        var now = _groupNow;
        if (IsGroupedByDate && Mode == ViewMode.Details) view.GroupDescriptions.Add(new DateGroupDescription(now));
        VirtualizingWrapPanel.SetGroupKey(List, IsGroupedByDate && Mode == ViewMode.Tiles
            ? item => item is FileItem f ? DateGroups.Name(f.Modified, now) : ""
            : null);
        ScheduleMidnightRegroup();
    }

    /// <summary>Re-sorts and regroups the current items (grouping switched on/off, view mode changed, a new day began).</summary>
    public void RefreshGrouping()
    {
        if (IsSearchResults || Items.Count == 0) { ApplyGrouping(); return; }
        var selected = SelectedItems.Select(i => i.FullPath).ToList();
        SetItems(SortItems(Items.ToList()), selected, scrollToSelection: selected.Count > 0);
    }

    /// <summary>"Today" becomes "Yesterday" at midnight: regroup then (only while grouped).</summary>
    private void ScheduleMidnightRegroup()
    {
        _midnight?.Stop();
        if (!IsGroupedByDate) return;
        var wait = _groupNow.Date.AddDays(1) - DateTime.Now + TimeSpan.FromSeconds(2);
        if (wait < TimeSpan.FromSeconds(2)) wait = TimeSpan.FromSeconds(2);
        _midnight ??= new DispatcherTimer();
        _midnight.Interval = wait;
        _midnight.Tick -= OnMidnight;
        _midnight.Tick += OnMidnight;
        _midnight.Start();
    }

    private void OnMidnight(object? sender, EventArgs e)
    {
        _midnight?.Stop();
        if (IsGroupedByDate) RefreshGrouping();
    }

    private static DataTemplate GroupHeaderTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 12, 8, 4));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(CollectionViewGroup.Name)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.ForegroundProperty, Theme.Brush("Accent"));
        name.SetValue(DockPanel.DockProperty, Dock.Left);
        var count = new FrameworkElementFactory(typeof(TextBlock));
        count.SetBinding(TextBlock.TextProperty, new Binding(nameof(CollectionViewGroup.ItemCount)) { StringFormat = " ({0})" });
        count.SetValue(TextBlock.ForegroundProperty, Theme.Brush("Fg3"));
        count.SetValue(DockPanel.DockProperty, Dock.Left);
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.HeightProperty, 1.0);
        line.SetValue(Border.BackgroundProperty, Theme.Brush("Line"));
        line.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 1, 0, 0));
        row.AppendChild(name);
        row.AppendChild(count);
        row.AppendChild(line);
        return new DataTemplate { VisualTree = row };
    }

    /// <summary>Puts each item in its date group, using one "now" for the whole list.</summary>
    private sealed class DateGroupDescription(DateTime now) : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, CultureInfo culture) =>
            item is FileItem f ? DateGroups.Name(f.Modified, now) : "";
    }
}
