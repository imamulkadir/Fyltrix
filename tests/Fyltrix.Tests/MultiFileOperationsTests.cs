using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class MultiFileOperationsTests
{
    private static Button Button(MainWindow window, string id) =>
        (Button)window.ToolbarForTest.Single(b => b.Id == id).Element;
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [Theory]
    [InlineData("keyboard")] [InlineData("toolbar")] [InlineData("menu")]
    public void Cut_keeps_the_complete_mixed_selection_even_when_one_file_is_open(string route)
    {
        using var temp = new TempDir();
        temp.File("data/a.txt"); temp.File("data/b.txt"); temp.Dir("data/folder");
        var previous = MainWindow.ClipboardFactory;
        var clipboard = new MemoryFileClipboard();
        MainWindow.ClipboardFactory = () => clipboard;
        try
        {
            Sta.Run(() =>
            {
                var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
                try
                {
                    var view = window.View!;
                    view.SelectPaths(new[] { temp["data/a.txt"], temp["data/b.txt"], temp["data/folder"] });
                    Sta.DoEvents(100);
                    Assert.Equal(3, view.SelectedItems.Count);
                    using var locked = new FileStream(temp["data/b.txt"], FileMode.Open, FileAccess.Read, FileShare.Read);
                    Assert.False(FileAccessPolicy.Deletable(temp["data/b.txt"]));
                    window.OnSelectionChanged(view);
                    Assert.True(Button(window, "cut").IsEnabled);
                    Assert.True(Button(window, "delete").IsEnabled);
                    var menu = window.BuildContextMenu(view, view.SelectedItems);
                    Assert.True(MainWindowUiTests.Item(menu, "Cut").IsEnabled);
                    Assert.True(MainWindowUiTests.Item(menu, "Delete").IsEnabled);
                    if (route == "keyboard") window.HandleFileShortcut(Key.X, true, false);
                    else if (route == "toolbar") Click(Button(window, "cut"));
                    else Click(MainWindowUiTests.Item(menu, "Cut"));
                    var paths = Assert.IsType<string[]>(clipboard.Data!.GetData(DataFormats.FileDrop));
                    Assert.Equal(view.SelectedItems.Select(i => i.FullPath).Order(), paths.Order());
                    Assert.Equal(3, paths.Length);
                    Assert.All(view.SelectedItems, item => Assert.True(item.IsCut));
                    Assert.Equal(3, view.SelectedItems.Count);
                    Assert.All(paths, path => Assert.True(FileOps.Exists(path))); // Cut never moves immediately
                }
                finally { window.Close(); }
            });
        }
        finally { MainWindow.ClipboardFactory = previous; }
    }

    // This store only moves test-owned paths. It never accesses Windows' real Recycle Bin.
    private sealed class IsolatedBin
    {
        private readonly TempDir _temp;
        internal readonly Dictionary<string, string> Saved = new();
        internal readonly List<string[]> Requests = new();
        internal IsolatedBin(TempDir temp) => _temp = temp;
        internal bool Delete(IList<string> paths, bool permanent, IntPtr owner)
        {
            Requests.Add(paths.ToArray());
            bool ok = true;
            foreach (var path in paths)
            {
                Assert.StartsWith(_temp["data"] + Path.DirectorySeparatorChar, Path.GetFullPath(path));
                try
                {
                    if (permanent)
                    {
                        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                        else File.Delete(path);
                    }
                    else
                    {
                        string target = Path.Combine(_temp.Dir("bin"), Guid.NewGuid().ToString("N"));
                        if (Directory.Exists(path)) Directory.Move(path, target);
                        else File.Move(path, target);
                        Saved.Add(path, target);
                    }
                }
                catch (IOException) { ok = false; }
                catch (UnauthorizedAccessException) { ok = false; }
            }
            return ok;
        }
        internal string? Restore(string path, DateTime when) => RecycleBin.Restore(path,
            Saved.TryGetValue(path, out var target)
                ? new RecycleBin.Entry(target + ".metadata", target, path, when, Directory.Exists(target)) : null);
    }

    [Theory]
    [InlineData("keyboard", false, true)] [InlineData("toolbar", false, true)] [InlineData("menu", false, true)]
    [InlineData("keyboard", true, true)] [InlineData("menu", true, true)]
    [InlineData("keyboard", true, false)] [InlineData("menu", true, false)]
    public void Delete_uses_the_whole_selection_and_recycle_undo_restores_each_item(string route, bool permanent, bool confirm)
    {
        using var temp = new TempDir();
        var paths = new[] { temp.File("data/a.txt", "alpha"), temp.File("data/b.txt", "beta"), temp.Dir("data/folder") };
        temp.File("data/folder/child.txt", "child");
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            var bin = new IsolatedBin(temp);
            try
            {
                window.DeleteFilesForTest = bin.Delete;
                window.RestoreDeletedForTest = bin.Restore;
                int confirmations = 0;
                window.ConfirmPermanentDeleteForTest = count => { Assert.Equal(3, count); confirmations++; return confirm; };
                var view = window.View!;
                view.SelectPaths(paths);
                Assert.Equal(3, view.SelectedItems.Count);
                if (route == "keyboard") window.HandleFileShortcut(Key.Delete, false, permanent);
                else if (route == "toolbar") Click(Button(window, "delete"));
                else Click(MainWindowUiTests.Item(window.BuildContextMenu(view, view.SelectedItems), permanent ? "Delete permanently" : "Delete"));
                Assert.Equal(permanent ? 1 : 0, confirmations);
                if (permanent && !confirm)
                {
                    Assert.Empty(bin.Requests);
                    Assert.All(paths, path => Assert.True(FileOps.Exists(path)));
                    Assert.False(Button(window, "undo").IsEnabled);
                    return;
                }
                Assert.Equal(paths.Order(), Assert.Single(bin.Requests).Order());
                Assert.All(paths, path => Assert.False(FileOps.Exists(path)));
                Assert.Contains("3 items", window.OperationStatusForTest);
                Assert.Equal(!permanent, Button(window, "undo").IsEnabled);
                if (!permanent)
                {
                    Click(Button(window, "undo"));
                    Assert.Equal("alpha", File.ReadAllText(paths[0]));
                    Assert.Equal("beta", File.ReadAllText(paths[1]));
                    Assert.Equal("child", File.ReadAllText(temp["data/folder/child.txt"]));
                    Assert.False(Button(window, "undo").IsEnabled);
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("keyboard")] [InlineData("toolbar")] [InlineData("menu")]
    public void Partial_delete_reports_actual_completion_and_undo_only_restores_deleted_paths(string route)
    {
        using var temp = new TempDir();
        var paths = new[] { temp.File("data/a.txt"), temp.File("data/locked.txt"), temp.Dir("data/folder") };
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            var bin = new IsolatedBin(temp);
            try
            {
                window.DeleteFilesForTest = bin.Delete;
                window.RestoreDeletedForTest = bin.Restore;
                window.View!.SelectPaths(paths);
                using var locked = new FileStream(paths[1], FileMode.Open, FileAccess.Read, FileShare.Read);
                window.OnSelectionChanged(window.View);
                if (route == "keyboard") window.HandleFileShortcut(Key.Delete, false, false);
                else if (route == "toolbar") Click(Button(window, "delete"));
                else Click(MainWindowUiTests.Item(window.BuildContextMenu(window.View, window.View.SelectedItems), "Delete"));
                Assert.Equal(paths.Order(), Assert.Single(bin.Requests).Order());
                Assert.True(File.Exists(paths[1]));
                Assert.False(File.Exists(paths[0])); Assert.False(Directory.Exists(paths[2]));
                Assert.Contains("2 items deleted; 1 remaining", window.OperationStatusForTest);
                Click(Button(window, "undo"));
                Assert.All(paths, path => Assert.True(FileOps.Exists(path)));
                Assert.Equal(2, bin.Saved.Count);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Invalid_selection_does_not_delete_any_items_and_confirmation_keeps_its_original_tab()
    {
        using var temp = new TempDir();
        var paths = new[] { temp.File("data/a.txt"), temp.File("data/b.txt") };
        var other = temp.File("other/unselected.txt", "keep");
        Sta.Run(() =>
        {
            var window = MainWindowUiTests.OpenWindow(temp, temp["data"]);
            var bin = new IsolatedBin(temp);
            try
            {
                window.DeleteFilesForTest = bin.Delete;
                var original = window.View!;
                original.SelectPaths(paths);
                File.Delete(paths[1]);
                window.HandleFileShortcut(Key.Delete, false, false);
                Assert.Empty(bin.Requests); Assert.True(File.Exists(paths[0]));
                File.WriteAllText(paths[1], "restored fixture");
                window.ConfirmPermanentDeleteForTest = count =>
                {
                    Assert.Equal(2, count);
                    window.AddTabForTest(temp["other"]);
                    return true;
                };
                window.HandleFileShortcut(Key.Delete, false, true);
                Assert.Equal(paths.Order(), Assert.Single(bin.Requests).Order());
                Assert.All(paths, path => Assert.False(FileOps.Exists(path)));
                Assert.Equal("keep", File.ReadAllText(other));
                Assert.Equal(temp["other"], window.View!.CurrentPath);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Partial_cut_paste_keeps_skipped_items_cut_and_preserves_a_replaced_clipboard(bool replaceClipboard)
    {
        using var temp = new TempDir();
        var source = temp.Dir("data"); var target = temp.Dir("target");
        var moved = temp.File("data/a.txt", "move");
        var skipped = temp.File("data/b.txt", "source");
        temp.File("target/b.txt", "existing");
        var previous = MainWindow.ClipboardFactory;
        var clipboard = new MemoryFileClipboard();
        MainWindow.ClipboardFactory = () => clipboard;
        try
        {
            Sta.Run(() =>
            {
                var window = MainWindowUiTests.OpenWindow(temp, source);
                try
                {
                    window.ConflictForTest = _ =>
                    {
                        if (replaceClipboard) clipboard.Write(new DataObject(DataFormats.UnicodeText, "new clipboard"));
                        return (ConflictChoice.Skip, false);
                    };
                    window.View!.SelectPaths(new[] { moved, skipped });
                    window.HandleFileShortcut(Key.X, true, false);
                    window.View.Navigate(target);
                    Sta.WaitUntil(() => window.View.StartupListingReady && window.View.CurrentPath == target && Button(window, "paste").IsEnabled, "destination ready");
                    window.HandleFileShortcut(Key.V, true, false);
                    Sta.WaitUntil(() => window.PasteTask.IsCompleted, "paste completed");
                    window.PasteTask.GetAwaiter().GetResult();
                    Assert.False(File.Exists(moved)); Assert.Equal("move", File.ReadAllText(temp["target/a.txt"]));
                    Assert.Equal("source", File.ReadAllText(skipped)); Assert.Equal("existing", File.ReadAllText(temp["target/b.txt"]));
                    if (replaceClipboard) Assert.Equal("new clipboard", clipboard.Data!.GetData(DataFormats.UnicodeText));
                    else
                    {
                        Assert.Equal(new[] { skipped }, Assert.IsType<string[]>(clipboard.Data!.GetData(DataFormats.FileDrop)));
                        Assert.Contains("1 item remains cut", window.OperationStatusForTest);
                    }
                    Click(Button(window, "undo"));
                    Assert.Equal("move", File.ReadAllText(moved));
                    Assert.Equal("existing", File.ReadAllText(temp["target/b.txt"]));
                }
                finally { window.Close(); }
            });
        }
        finally { MainWindow.ClipboardFactory = previous; }
    }

    [Fact]
    public void Cut_and_paste_moves_all_files_and_folders_and_undo_moves_all_back()
    {
        using var temp = new TempDir();
        var source = temp.Dir("data"); var target = temp.Dir("target");
        var paths = new[] { temp.File("data/a.txt", "alpha"), temp.File("data/b.txt", "beta"), temp.Dir("data/folder") };
        temp.File("data/folder/child.txt", "child");
        var previous = MainWindow.ClipboardFactory;
        var clipboard = new MemoryFileClipboard();
        MainWindow.ClipboardFactory = () => clipboard;
        try
        {
            Sta.Run(() =>
            {
                var window = MainWindowUiTests.OpenWindow(temp, source);
                try
                {
                    window.View!.SelectPaths(paths);
                    window.HandleFileShortcut(Key.X, true, false);
                    window.AddTabForTest(target);
                    Sta.WaitUntil(() => window.View!.CurrentPath == target && window.View.StartupListingReady && Button(window, "paste").IsEnabled, "paste destination ready");
                    window.HandleFileShortcut(Key.V, true, false);
                    Sta.WaitUntil(() => window.PasteTask.IsCompleted, "multi-item paste");
                    window.PasteTask.GetAwaiter().GetResult();
                    Assert.All(paths, path => Assert.False(FileOps.Exists(path)));
                    Assert.Equal("alpha", File.ReadAllText(temp["target/a.txt"]));
                    Assert.Equal("beta", File.ReadAllText(temp["target/b.txt"]));
                    Assert.Equal("child", File.ReadAllText(temp["target/folder/child.txt"]));
                    Assert.Null(clipboard.Data);
                    Click(Button(window, "undo"));
                    Assert.All(paths, path => Assert.True(FileOps.Exists(path)));
                    Assert.Equal("child", File.ReadAllText(temp["data/folder/child.txt"]));
                }
                finally { window.Close(); }
            });
        }
        finally { MainWindow.ClipboardFactory = previous; }
    }

    [Fact]
    public void Cancelling_a_conflict_preserves_all_pending_cut_files()
    {
        using var temp = new TempDir();
        var source = temp.Dir("data"); var target = temp.Dir("target");
        var paths = new[] { temp.File("data/a.txt", "a"), temp.File("data/b.txt", "source") };
        temp.File("target/b.txt", "keep");
        var previous = MainWindow.ClipboardFactory;
        var clipboard = new MemoryFileClipboard();
        MainWindow.ClipboardFactory = () => clipboard;
        try
        {
            Sta.Run(() =>
            {
                var window = MainWindowUiTests.OpenWindow(temp, source);
                try
                {
                    window.ConflictForTest = _ => (ConflictChoice.Cancel, false);
                    window.View!.SelectPaths(paths);
                    window.HandleFileShortcut(Key.X, true, false);
                    window.AddTabForTest(target);
                    Sta.WaitUntil(() => window.View!.CurrentPath == target && window.View.StartupListingReady && Button(window, "paste").IsEnabled, "cancel destination ready");
                    window.HandleFileShortcut(Key.V, true, false);
                    Sta.WaitUntil(() => window.PasteTask.IsCompleted, "cancelled paste");
                    window.PasteTask.GetAwaiter().GetResult();
                    Assert.All(paths, path => Assert.True(FileOps.Exists(path)));
                    Assert.False(File.Exists(temp["target/a.txt"]));
                    Assert.Equal("keep", File.ReadAllText(temp["target/b.txt"]));
                    Assert.Equal(paths.Order(), Assert.IsType<string[]>(clipboard.Data!.GetData(DataFormats.FileDrop)).Order());
                    Assert.False(Button(window, "undo").IsEnabled);
                    Assert.Contains("Move cancelled", window.OperationStatusForTest);
                }
                finally { window.Close(); }
            });
        }
        finally { MainWindow.ClipboardFactory = previous; }
    }
}
