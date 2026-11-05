using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class NumberedRenameUiTests
{
    private static Button Button(MainWindow window, string id) => (Button)window.ToolbarForTest.Single(b => b.Id == id).Element;
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Theory]
    [InlineData("keyboard", 0, false)] [InlineData("toolbar", 48, false)]
    [InlineData("menu", 96, true)] [InlineData("palette", 160, true)] [InlineData("keyboard", 256, false)]
    public void Rename_routes_share_visible_order_numbered_names_and_undo(string route, int tileSize, bool grouped)
    {
        using var temp = new TempDir();
        temp.File("data/z.jpg", "z"); temp.File("data/a.png", "a"); temp.File("data/b.txt", "b");
        File.SetLastWriteTime(temp["data/z.jpg"], DateTime.Now.AddDays(-8));
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"], settings => settings.DefaultSortAscending = false);
            try
            {
                var view = window.View!;
                if (tileSize != 0) window.SetView(tileSize);
                if (grouped) { view.GroupByDateFor = _ => true; view.RefreshGrouping(); }
                var visible = view.List.Items.Cast<FileItem>().ToArray();
                view.SelectPaths(visible.AsEnumerable().Reverse().Select(i => i.FullPath));
                window.NumberedRenamePromptForTest = (count, initial) =>
                {
                    Assert.Equal(3, count);
                    Assert.Equal(Path.GetFileNameWithoutExtension(visible[0].Name), initial);
                    return "Holiday";
                };
                window.NumberedRenameErrorForTest = message => Assert.Fail(message);
                Assert.True(Button(window, "rename").IsEnabled);
                var menu = window.BuildContextMenu(view, view.SelectedItems);
                Assert.True(MainWindowUiTests.Item(menu, "Rename").IsEnabled);
                Assert.True(MainWindowUiTests.Item(menu, "Batch rename…").IsEnabled);
                Assert.NotEqual("F2", MainWindowUiTests.Item(menu, "Batch rename…").InputGestureText);
                Assert.Contains(window.PaletteEntries(""), entry => entry.Title == "Batch rename…");
                if (route == "keyboard") window.HandleFileShortcut(Key.F2, false, false);
                else if (route == "toolbar") Click(Button(window, "rename"));
                else if (route == "menu") MainWindowUiTests.Click(MainWindowUiTests.Item(menu, "Rename"));
                else window.PaletteEntries("").Single(entry => entry.Title == "Rename").Run();
                for (int i = 0; i < visible.Length; i++)
                {
                    var target = temp[$"data/Holiday ({i + 1}){visible[i].Extension}"];
                    Assert.Equal(Path.GetFileNameWithoutExtension(visible[i].Name), File.ReadAllText(target));
                    Assert.False(File.Exists(visible[i].FullPath));
                }
                Sta.WaitUntil(() => view.StartupListingReady && view.SelectedItems.Count == 3 &&
                    view.SelectedItems.All(i => i.Name.StartsWith("Holiday", StringComparison.Ordinal)), "renamed selection");
                Assert.DoesNotContain(Application.Current.Windows.Cast<Window>(), w => w is BatchRenameWindow);
                Click(Button(window, "undo"));
                Sta.WaitUntil(() => visible.All(i => File.Exists(i.FullPath)), "numbered rename undo");
                Assert.False(Button(window, "undo").IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Cancellation_invalid_names_and_navigation_do_not_change_the_original_files()
    {
        using var temp = new TempDir();
        var paths = new[] { temp.File("data/a.txt", "a"), temp.File("data/b.txt", "b") };
        temp.File("other/keep.txt", "keep");
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                var view = window.View!;
                view.SelectPaths(paths);
                var errors = new List<string>();
                window.NumberedRenameErrorForTest = errors.Add;
                window.NumberedRenamePromptForTest = (_, _) => null;
                window.HandleFileShortcut(Key.F2, false, false);
                Assert.Empty(errors); Assert.False(Button(window, "undo").IsEnabled);
                window.NumberedRenamePromptForTest = (_, _) => "CON";
                window.HandleFileShortcut(Key.F2, false, false);
                Assert.Contains("reserved", Assert.Single(errors));
                errors.Clear();
                window.NumberedRenamePromptForTest = (_, _) =>
                {
                    view.Navigate(temp["other"]);
                    Sta.WaitUntil(() => view.StartupListingReady && view.CurrentPath == temp["other"], "navigation during prompt");
                    return "Holiday";
                };
                window.HandleFileShortcut(Key.F2, false, false);
                Assert.Contains("no longer available", Assert.Single(errors));
                Assert.All(paths, path => Assert.True(File.Exists(path)));
                Assert.Equal("keep", File.ReadAllText(temp["other/keep.txt"]));
                Assert.False(Button(window, "undo").IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Single_item_F2_still_starts_inline_editing()
    {
        using var temp = new TempDir();
        temp.File("data/a.txt", "a");
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                window.View!.SelectPaths(new[] { temp["data/a.txt"] });
                window.NumberedRenamePromptForTest = (_, _) => throw new Exception("Single rename must stay inline");
                window.HandleFileShortcut(Key.F2, false, false);
                Assert.True(Assert.Single(window.View.SelectedItems).IsEditing);
                Assert.True(File.Exists(temp["data/a.txt"]));
                TextBox? box = null;
                Sta.WaitUntil(() => (box = VisualTree.FindDescendant<TextBox>(window.View.List, b => Equals(b.Tag, "rename") && b.IsVisible)) != null, "inline editor");
                box!.Text = "renamed.txt";
                var item = Assert.Single(window.View.SelectedItems);
                item.IsEditing = false;
                window.CommitRename(window.View, item, box.Text);
                Assert.Equal("a", File.ReadAllText(temp["data/renamed.txt"]));
                Click(Button(window, "undo"));
                Assert.Equal("a", File.ReadAllText(temp["data/a.txt"]));
            }
            finally { window.View!.SelectedItems.ToList().ForEach(i => i.IsEditing = false); window.Close(); }
        });
    }

    [Fact]
    public void Failed_numbered_rename_reports_rollback_without_creating_undo()
    {
        using var temp = new TempDir();
        var paths = new[] { temp.File("data/a.txt", "a"), temp.File("data/b.txt", "b") };
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                window.View!.SelectPaths(paths);
                var errors = new List<string>();
                window.NumberedRenameErrorForTest = errors.Add;
                window.NumberedRenamePromptForTest = (_, _) => "Holiday";
                using var locked = new FileStream(paths[1], FileMode.Open, FileAccess.Read, FileShare.Read);
                window.HandleFileShortcut(Key.F2, false, false);
                Assert.Contains("rolled back", Assert.Single(errors));
                Assert.Equal("a", File.ReadAllText(paths[0])); Assert.Equal("b", File.ReadAllText(paths[1]));
                Assert.False(Button(window, "undo").IsEnabled);
                Assert.Empty(Directory.GetFiles(temp["data"], ".fl-rename-*"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Closing_the_owner_during_the_prompt_aborts_before_renaming()
    {
        using var temp = new TempDir();
        var paths = new[] { temp.File("data/a.txt", "a"), temp.File("data/b.txt", "b") };
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            try
            {
                window.View!.SelectPaths(paths);
                window.NumberedRenameErrorForTest = message => Assert.Fail(message);
                window.NumberedRenamePromptForTest = (_, _) => { window.Close(); return "Holiday"; };
                window.HandleFileShortcut(Key.F2, false, false);
                Assert.All(paths, path => Assert.True(File.Exists(path)));
            }
            finally { if (window.IsVisible) window.Close(); }
        });
    }
}
