using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>
/// The folder's own (background) menu must be reachable in a folder whose items fill the whole list: to the right of the
/// Details columns, between tiles, and from the toolbar's … button. Clicks are simulated at exact points (no real mouse).
/// </summary>
[Collection("UI")]
public class BackgroundMenuUiTests
{
    private static bool IsFolderMenu(ContextMenu m) =>
        MainWindowUiTests.AllItems(m).Any(i => (i.Header as string) == "New folder") && MainWindowUiTests.AllItems(m).Any(i => (i.Header as string) == "Paste");

    private static bool IsItemMenu(ContextMenu m) =>
        MainWindowUiTests.AllItems(m).Any(i => (i.Header as string) == "Rename") && !IsFolderMenu(m);

    private static TempDir Crowded()
    {
        var t = new TempDir();
        for (int i = 0; i < 400; i++) t.File($"file {i:000}.txt");
        for (int i = 0; i < 20; i++) t.Dir($"folder {i:00}");
        return t;
    }

    private static ListViewItem Row(FolderView view, int index)
    {
        Sta.WaitUntil(() => view.List.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem { ActualWidth: > 0 }, $"row {index}");
        return (ListViewItem)view.List.ItemContainerGenerator.ContainerFromIndex(index);
    }

    [Fact]
    public void Details_view_right_of_the_columns_is_background_even_beside_rows()
    {
        using var t = Crowded();
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                Sta.DoEvents(200);
                var list = view.List;
                var row = Row(view, 5);
                var rowItem = (FileItem)row.DataContext;
                var presenter = VisualTree.FindDescendant<GridViewRowPresenter>(row)!;
                double columnsRight = presenter.TranslatePoint(new Point(0, 0), list).X
                                      + ((GridView)list.View).Columns.Sum(c => c.ActualWidth);
                double y = row.TranslatePoint(new Point(0, row.ActualHeight / 2), list).Y;
                Assert.True(list.ActualWidth - columnsRight > 60, "the test window leaves space to the right of the columns");
                // the list is full: the last visible row reaches the bottom, so there is no blank area below the items
                Assert.True(VisualTree.FindDescendants<ListViewItem>(list).Max(r => r.TranslatePoint(new Point(0, r.ActualHeight), list).Y) >= list.ActualHeight - 30);

                // on the name: the item's menu
                Assert.Same(rowItem, view.RightClickForTest(new Point(60, y)));
                Assert.True(IsItemMenu(w.BuildContextMenu(view, view.SelectedItems)));

                // beside the same row, right of the last column: the folder's menu, selection cleared
                Assert.Null(view.RightClickForTest(new Point(columnsRight + 30, y)));
                Assert.Empty(view.SelectedItems);
                var folderMenu = w.BuildContextMenu(view, view.SelectedItems);
                Assert.True(IsFolderMenu(folderMenu));
                Assert.True(MainWindowUiTests.Item(folderMenu, "New").IsEnabled);

                // inside the last column (Tags, empty for this row) still counts as the row
                Assert.Same(rowItem, view.RightClickForTest(new Point(columnsRight - 10, y)));

                // multi-selection: right-click on one of the selected items keeps all of them
                list.SelectedItems.Clear();
                foreach (var i in new[] { 3, 4, 5 }) list.SelectedItems.Add(list.Items[i]);
                Assert.Same(rowItem, view.RightClickForTest(new Point(60, y)));
                Assert.Equal(3, view.SelectedItems.Count);
                // …and right-click on an unselected item selects just that one
                var other = Row(view, 8);
                Assert.Same(other.DataContext, view.RightClickForTest(new Point(60, other.TranslatePoint(new Point(0, other.ActualHeight / 2), list).Y)));
                Assert.Single(view.SelectedItems);

                // scrolled to the middle of the folder: still reachable without going to the end
                view.List.ScrollIntoView(view.List.Items[200]);
                Sta.DoEvents(200);
                var mid = VisualTree.FindDescendants<ListViewItem>(list).First(r => r.TranslatePoint(new Point(0, 0), list).Y > 40);
                Assert.Null(view.RightClickForTest(new Point(columnsRight + 30, mid.TranslatePoint(new Point(0, mid.ActualHeight / 2), list).Y)));

                // wider columns move the boundary with them
                var cols = ((GridView)list.View).Columns;
                cols[0].Width += 100;
                Sta.DoEvents(100);
                Assert.NotNull(view.RightClickForTest(new Point(columnsRight + 30, mid.TranslatePoint(new Point(0, mid.ActualHeight / 2), list).Y)));
                cols[0].Width -= 100;
                Sta.Render(w, "background-menu-details");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Grid_view_gaps_between_tiles_are_background()
    {
        using var t = Crowded();
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                view.SetViewMode(ViewMode.Tiles, 96);
                Sta.DoEvents(300);
                var list = view.List;
                var a = Row(view, 0);
                var b = Row(view, 1);
                double aRight = a.TranslatePoint(new Point(a.ActualWidth, 0), list).X;
                double bLeft = b.TranslatePoint(new Point(0, 0), list).X;
                double y = a.TranslatePoint(new Point(0, a.ActualHeight / 2), list).Y;
                Assert.Same(a.DataContext, view.RightClickForTest(new Point(aRight - a.ActualWidth / 2, y)));
                Assert.True(IsItemMenu(w.BuildContextMenu(view, view.SelectedItems)));
                // the tile's own border has a 3 px margin: the 6 px between two tiles is background
                double gap = aRight - 1.5;
                Assert.True(bLeft >= aRight - 0.5);
                Assert.Null(view.RightClickForTest(new Point(gap, y)));
                Assert.True(IsFolderMenu(w.BuildContextMenu(view, view.SelectedItems)));
                Sta.Render(w, "background-menu-tiles");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Toolbar_more_button_opens_the_folder_menu_without_touching_the_selection()
    {
        using var t = Crowded();
        Sta.Run(() =>
        {
            IdeCatalog.SetDetectedForTest(Array.Empty<IdeEntry>());
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                view.List.SelectedItems.Add(view.List.Items[2]);
                Assert.NotNull(w.FolderMenuButton);
                Assert.Equal("More actions for this folder", System.Windows.Automation.AutomationProperties.GetName(w.FolderMenuButton!));
                // the button's menu is the background right-click menu (same builder), whatever is selected
                var menu = view.BuildBackgroundMenu();
                Assert.True(IsFolderMenu(menu));
                Assert.Single(view.SelectedItems);
                // and it respects the Settings choices: a New type hidden there is hidden here too
                var templates = Core.Templates.FileTemplates.Load();
                templates.Single(x => x.Id == "md").Enabled = false;
                Core.Templates.FileTemplates.Save(templates);
                w.ReloadTemplates();
                var newItems = MainWindowUiTests.AllItems(MainWindowUiTests.Item(view.BuildBackgroundMenu(), "New")).Select(m => m.Header as string).ToList();
                Assert.DoesNotContain("Markdown", newItems);
                Assert.Contains("Text document", newItems);
            }
            finally { w.Close(); }
        });
    }
}
