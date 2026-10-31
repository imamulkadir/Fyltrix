using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>Drives the real MainWindow in-process (off-screen) to check UI wiring end to end.</summary>
[Collection("UI")]
public class MainWindowUiTests
{
    internal static MainWindow OpenWindow(TempDir t, string startFolder, Action<AppSettings>? configure = null)
    {
        AppSettings.Folder = t.Dir("_settings");
        var settings = new AppSettings { PreviewVisible = false, WindowWidth = 1300, WindowHeight = 800 };
        configure?.Invoke(settings);
        var w = new MainWindow(settings, startFolder);
        Sta.ShowOffscreen(w);
        Sta.WaitUntil(() => w.View != null && w.View.CurrentPath == startFolder && w.View.Items.Count > 0, "folder listing");
        return w;
    }

    internal static IEnumerable<MenuItem> AllItems(ItemsControl menu) =>
        menu.Items.OfType<MenuItem>().SelectMany(mi => new[] { mi }.Concat(AllItems(mi)));

    internal static MenuItem Item(ContextMenu menu, string header) =>
        AllItems(menu).FirstOrDefault(mi => (mi.Header as string) == header)
        ?? throw new Xunit.Sdk.XunitException($"Menu item '{header}' not found. Have: {string.Join(" | ", AllItems(menu).Select(m => m.Header))}");

    internal static void Click(MenuItem mi) => mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void MakeData(TempDir t)
    {
        t.File("src/Project/README.md", "# Project");
        t.File("src/Project/notes.txt", "just notes");
        t.File("src/Project/src/main.cs", "class P {}");
        ZipFile.CreateFromDirectory(t["src/Project"], t["sample.zip"], CompressionLevel.Optimal, includeBaseDirectory: true);
        t.File("hello.txt", "hello");
    }

    [Fact]
    public void Browse_extract_and_compress_through_the_UI()
    {
        using var t = new TempDir();
        MakeData(t);
        Sta.Run(() =>
        {
            var w = OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                var zip = view.Items.Single(i => i.Name == "sample.zip");

                // open the archive like a folder
                w.OpenItem(view, zip);
                Sta.WaitUntil(() => view.IsArchive && view.Items.Any(i => i.Name == "Project"), "archive root listing");
                Assert.Equal(t["sample.zip"], view.ArchiveFile);
                Assert.False(view.CanModify);
                w.OpenItem(view, view.Items.Single(i => i.Name == "Project"));
                Sta.WaitUntil(() => view.Items.Any(i => i.Name == "notes.txt"), "inner folder listing");
                Assert.Equal(new[] { "README.md", "notes.txt", "src" }, view.Items.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                Assert.All(view.Items, i => Assert.True(i.IsVirtual));
                Sta.Render(w, "archive-inner-folder");

                // context menu inside the archive: read-only actions only
                var notes = view.Items.Single(i => i.Name == "notes.txt");
                var menu = w.BuildContextMenu(view, new List<FileItem> { notes });
                var headers = AllItems(menu).Select(m => m.Header as string).ToList();
                Assert.Contains("Copy", headers);
                Assert.DoesNotContain("Delete", headers);
                Assert.DoesNotContain("Rename", headers);

                Click(Item(menu, $"Extract to '{Path.GetFileName(t.Path)}'"));
                Sta.WaitUntil(() => File.Exists(t["notes.txt"]), "extracted file");
                Assert.Equal("just notes", File.ReadAllText(t["notes.txt"]));
                Assert.StartsWith("Extract 'sample.zip'", w.UndoStack.NextDescription);

                // back out of the archive: up twice lands on the folder with the archive selected
                view.GoUp();
                Sta.WaitUntil(() => view.IsArchive && view.ArchiveInner == "", "archive root");
                view.GoUp();
                Sta.WaitUntil(() => !view.IsArchive && view.CurrentPath == t.Path, "parent folder");
                Sta.WaitUntil(() => view.SelectedItems.Any(i => i.Name == "sample.zip"), "archive selected after going up");

                // archive submenu on the zip; compress on a plain file
                var zipMenu = w.BuildContextMenu(view, new List<FileItem> { view.Items.Single(i => i.Name == "sample.zip") });
                Item(zipMenu, "Extract here");
                Item(zipMenu, "Extract to 'sample\\'");
                Click(Item(zipMenu, "Extract to 'sample\\'"));
                Sta.WaitUntil(() => File.Exists(t["sample/Project/src/main.cs"]), "extract to named folder");

                var hello = view.Items.Single(i => i.Name == "hello.txt");
                var fileMenu = w.BuildContextMenu(view, new List<FileItem> { hello });
                Click(Item(fileMenu, "Add to 'hello.zip'"));
                Sta.WaitUntil(() => File.Exists(t["hello.zip"]), "compressed archive");
                using (var z = ZipFile.OpenRead(t["hello.zip"])) Assert.Equal("hello.txt", z.Entries.Single().FullName);

                // undo removes the created archive (to the Recycle Bin)
                Assert.StartsWith("Create 'hello.zip'", w.UndoStack.NextDescription);
                Sta.Render(w, "after-compress");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Archive_path_typed_in_address_bar_style_navigation_works()
    {
        using var t = new TempDir();
        MakeData(t);
        Sta.Run(() =>
        {
            var w = OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                view.Navigate(t["sample.zip/Project/src"]);
                Sta.WaitUntil(() => view.Items.Any(i => i.Name == "main.cs"), "deep archive path");
                Assert.Equal("Project/src", view.ArchiveInner);

                view.Navigate(t["sample.zip/Nope"]);
                Sta.WaitUntil(() => view.LoadError != null, "error for missing inner folder");
                Assert.Contains("not a folder", view.LoadError);
            }
            finally { w.Close(); }
        });
    }
}
