using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class ToolbarBehaviorTests
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    private static Button Button(MainWindow window, string id) => (Button)window.ToolbarForTest.Single(b => b.Id == id).Element;
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Notify(MainWindow window) => SendMessage(new WindowInteropHelper(window).Handle, 0x031D, IntPtr.Zero, IntPtr.Zero);
    private static BitmapSource Bitmap()
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 3, 2, 1, 255, 30, 20, 10, 128, 60, 50, 40, 255, 90, 80, 70, 0 }, 8);
        bitmap.Freeze();
        return bitmap;
    }
    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
    private static void WaitPaste(MainWindow window)
    {
        Sta.WaitUntil(() => window.PasteTask.IsCompleted, "paste completion");
        window.PasteTask.GetAwaiter().GetResult();
    }
    private static void WithClipboard(Action<MemoryFileClipboard> test)
    {
        var previous = MainWindow.ClipboardFactory;
        var clipboard = new MemoryFileClipboard();
        MainWindow.ClipboardFactory = () => clipboard;
        try { test(clipboard); }
        finally { MainWindow.ClipboardFactory = previous; }
    }

    [Fact]
    public void Selection_rules_match_toolbar_menus_and_shortcuts_and_reject_stale_items()
    {
        using var t = new TempDir();
        t.File("a.json", "{}"); t.File("b.xml", "<root/>"); t.File("c.txt", "text");
        t.File("picture.png", "placeholder"); t.File("binary.bin", "\0\0\0"); t.Dir("folder");
        Sta.Run(() => WithClipboard(clipboard =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                Sta.WaitUntil(() => Button(w, "new").IsEnabled, "destination access check");
                void Select(params string[] names) => view.SelectPaths(names.Select(n => t[n]));
                void State(bool copy, bool rename, bool quick, bool compare)
                {
                    Assert.Equal(copy, Button(w, "copy").IsEnabled);
                    Assert.Equal(copy, Button(w, "cut").IsEnabled);
                    Assert.Equal(copy, Button(w, "delete").IsEnabled);
                    Assert.Equal(rename, Button(w, "rename").IsEnabled);
                    Assert.Equal(quick, Button(w, "quickview").IsEnabled);
                    Assert.Equal(compare, Button(w, "compare").IsEnabled);
                    Assert.Equal(compare, w.PaletteEntries("").Any(e => e.Title == "Compare files"));
                }
                Select(); State(false, false, false, false);
                Assert.False(Button(w, "paste").IsEnabled);
                Select("folder"); State(true, true, false, false);
                Assert.True(MainWindowUiTests.Item(w.BuildContextMenu(view, view.SelectedItems), "Rename").IsEnabled);
                // Routed click raised programmatically still cannot execute a disabled command.
                Click(Button(w, "quickview"));
                w.HandleFileShortcut(Key.Space, false, false);
                Assert.DoesNotContain(Application.Current.Windows.Cast<Window>(), win => win is QuickViewWindow);
                Select("a.json"); State(true, true, true, false);
                Select("a.json", "b.xml"); State(true, false, false, true);
                var menu = w.BuildContextMenu(view, view.SelectedItems);
                Assert.True(MainWindowUiTests.Item(menu, "Compare these two").IsEnabled);
                Assert.False(MainWindowUiTests.Item(menu, "Quick view").IsEnabled);
                Select("a.json", "picture.png"); State(true, false, false, false);
                Assert.Contains("incompatible", Button(w, "compare").ToolTip.ToString());
                Click(Button(w, "compare")); w.HandleFileShortcut(Key.D, true, false);
                Assert.DoesNotContain(Application.Current.Windows.Cast<Window>(), win => win is CompareWindow);
                Select("folder", "a.json"); State(true, false, false, false);
                Select("a.json", "b.xml", "c.txt"); State(true, false, false, false);
                Select("binary.bin"); State(true, true, true, false); // existing hex preview remains available
                Select("a.json");
                File.Delete(t["a.json"]);
                Click(Button(w, "copy")); w.HandleFileShortcut(Key.X, true, false);
                w.HandleFileShortcut(Key.F2, false, false); w.HandleFileShortcut(Key.Delete, false, false);
                Assert.Null(clipboard.Data);
                Assert.DoesNotContain(view.SelectedItems, i => i.IsEditing);
                w.OnSelectionChanged(view); State(false, false, false, false);
            }
            finally { w.Close(); }
        }));
    }

    [Theory]
    [InlineData("a.json", "b.xml", true)]
    [InlineData("a.txt", "b.md", true)]
    [InlineData("a.pdf", "b.pdf", true)]
    [InlineData("a.png", "b.jpg", true)]
    [InlineData("a.svg", "b.png", true)]
    [InlineData("a.docx", "b.docm", true)]
    [InlineData("a.xlsx", "b.ods", true)]
    [InlineData("a.mp4", "b.avi", true)]
    [InlineData("a.json", "b.png", false)]
    [InlineData("a.pdf", "b.docx", false)]
    [InlineData("a.bin", "b.bin", false)]
    public void Comparison_uses_supported_families(string a, string b, bool expected)
    {
        using var t = new TempDir();
        t.File(a, "\0\0\0"); t.File(b, "\0\0\0");
        Assert.Equal(expected, CompareWindow.Compatible(t[a], t[b]));
    }

    [Fact]
    public void Bitmap_paste_works_by_button_and_CtrlV_without_pixel_loss_or_overwrites()
    {
        using var t = new TempDir(); t.File("seed.txt");
        Sta.Run(() => WithClipboard(clipboard =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                clipboard.Image = Bitmap();
                clipboard.Data = new DataObject(DataFormats.Bitmap, clipboard.Image);
                Notify(w);
                Sta.WaitUntil(() => Button(w, "paste").IsEnabled, "image paste enabled");
                Click(Button(w, "paste")); WaitPaste(w);
                Assert.True(w.HandleFileShortcut(Key.V, true, false)); WaitPaste(w);
                var paths = Directory.GetFiles(t.Path, "Screenshot *.png");
                Assert.Equal(2, paths.Length);
                foreach (var path in paths)
                {
                    using var input = File.OpenRead(path);
                    var saved = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    var normalized = new FormatConvertedBitmap(saved, PixelFormats.Bgra32, null, 0);
                    var expected = new byte[16]; var actual = new byte[16];
                    clipboard.Image.CopyPixels(expected, 8, 0); normalized.CopyPixels(actual, 8, 0);
                    Assert.Equal(expected, actual);
                    Assert.Equal(2, saved.PixelWidth); Assert.Equal(2, saved.PixelHeight);
                }
                Assert.Equal(2, w.View!.Items.Count(i => i.Name.StartsWith("Screenshot ")));
                Assert.Single(w.View.SelectedItems);
                Assert.EndsWith(".png", w.View.SelectedItems[0].Name);
                // Atomic filename conflict handling, even for concurrent pastes with the exact same timestamp.
                var stamp = new DateTime(2026, 10, 30, 22, 45, 30);
                var bytes = Png(clipboard.Image);
                var first = ClipboardImageFile.Save(t.Path, bytes, null, stamp);
                var second = ClipboardImageFile.Save(t.Path, bytes, null, stamp);
                Assert.NotEqual(first, second); Assert.Equal(bytes, File.ReadAllBytes(first));
                Sta.Render(w, "toolbar-labels");
            }
            finally { w.Close(); }
        }));
    }

    [Fact]
    public void Png_bytes_are_preserved_and_file_drops_take_priority_with_copy_and_cut_intact()
    {
        using var t = new TempDir(); var source = t.Dir("source space & ü"); var target = t.Dir("target # space");
        File.WriteAllText(Path.Combine(source, "a.txt"), "original"); File.WriteAllText(Path.Combine(target, "seed.txt"), "seed");
        Sta.Run(() => WithClipboard(clipboard =>
        {
            var w = MainWindowUiTests.OpenWindow(t, target);
            try
            {
                var bytes = Png(Bitmap());
                var data = new DataObject(); data.SetData("PNG", new MemoryStream(bytes)); clipboard.Data = data;
                Notify(w); Sta.WaitUntil(() => Button(w, "paste").IsEnabled, "PNG enabled");
                w.HandleFileShortcut(Key.V, true, false); WaitPaste(w);
                Assert.Equal(bytes, File.ReadAllBytes(Directory.GetFiles(target, "Screenshot *.png").Single()));
                var files = new StringCollection(); files.Add(Path.Combine(source, "a.txt")); data.SetFileDropList(files);
                Notify(w); Click(Button(w, "paste")); WaitPaste(w);
                Assert.Equal("original", File.ReadAllText(Path.Combine(target, "a.txt")));
                Assert.True(File.Exists(Path.Combine(source, "a.txt")));
                Assert.Single(Directory.GetFiles(target, "Screenshot *.png"));
                // A second real file is cut through the same keyboard command as production, then pasted.
                w.View!.Navigate(source); Sta.WaitUntil(() => w.View.CurrentPath == source && w.View.Items.Any(i => i.Name == "a.txt"), "source");
                w.View.SelectPaths(new[] { Path.Combine(source, "a.txt") }); w.HandleFileShortcut(Key.X, true, false);
                var moved = t.Dir("move target");
                w.View.Navigate(moved); Sta.WaitUntil(() => w.View.CurrentPath == moved && w.View.Items.Count == 0 && Button(w, "paste").IsEnabled, "move target");
                w.HandleFileShortcut(Key.V, true, false); WaitPaste(w);
                Assert.False(File.Exists(Path.Combine(source, "a.txt")));
                Assert.Equal("original", File.ReadAllText(Path.Combine(moved, "a.txt")));
                Assert.Null(clipboard.Data);
            }
            finally { w.Close(); }
        }));
    }

    [Fact]
    public void Clipboard_errors_and_navigation_update_states_without_using_the_real_clipboard()
    {
        using var t = new TempDir(); t.File("seed.txt"); var other = t.Dir("other"); File.WriteAllText(Path.Combine(other, "seed.txt"), "");
        Sta.Run(() => WithClipboard(clipboard =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                clipboard.Data = new DataObject(DataFormats.Bitmap, Bitmap());
                Notify(w); Sta.WaitUntil(() => Button(w, "paste").IsEnabled, "paste enabled");
                clipboard.Busy = true; Notify(w);
                Assert.False(Button(w, "paste").IsEnabled);
                w.HandleFileShortcut(Key.V, true, false); Assert.Empty(Directory.GetFiles(t.Path, "Screenshot *.png"));
                clipboard.Busy = false; Notify(w); Assert.True(Button(w, "paste").IsEnabled);
                w.View!.Navigate(FolderView.ThisPC);
                Assert.False(Button(w, "paste").IsEnabled); Assert.False(Button(w, "new").IsEnabled);
                Assert.True(Button(w, "settings").IsEnabled); Assert.True(Button(w, "view").IsEnabled);
                w.AddTabForTest(other);
                Sta.WaitUntil(() => w.View!.CurrentPath == other && Button(w, "paste").IsEnabled, "new tab clipboard state");
                clipboard.Data = new DataObject(DataFormats.UnicodeText, "text is not a file"); Notify(w);
                Assert.False(Button(w, "paste").IsEnabled);
            }
            finally { w.Close(); }
        }));
    }

    [Fact]
    public void Write_denied_directory_disables_new_and_paste_without_creating_probe_files()
    {
        using var t = new TempDir(); var target = t.Dir("read only"); File.WriteAllText(Path.Combine(target, "seed.txt"), "");
        var dir = new DirectoryInfo(target); var original = dir.GetAccessControl();
        var security = dir.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny));
        try
        {
            dir.SetAccessControl(security);
            Assert.False(FileAccessPolicy.WritableDirectory(target));
            Sta.Run(() => WithClipboard(clipboard =>
            {
                clipboard.Data = new DataObject(DataFormats.Bitmap, Bitmap());
                var w = MainWindowUiTests.OpenWindow(t, target);
                try
                {
                    Sta.DoEvents(150);
                    Assert.False(Button(w, "new").IsEnabled); Assert.False(Button(w, "paste").IsEnabled);
                    Assert.False(MainWindowUiTests.Item(w.BuildContextMenu(w.View!, new List<FileItem>()), "New").IsEnabled);
                    Click(Button(w, "newfolder")); w.HandleFileShortcut(Key.V, true, false);
                    Assert.Single(Directory.GetFileSystemEntries(target));
                }
                finally { w.Close(); }
            }));
        }
        finally { dir.SetAccessControl(original); }
    }

    [Fact]
    public void Secondary_labels_persist_primary_labels_remain_and_right_controls_and_tabs_align()
    {
        using var t = new TempDir(); t.File("seed.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => { s.HiddenToolbarButtons.Add("settings"); s.ToolbarExtras.Add("New tab"); s.ShowSecondaryToolbarLabels = true; s.ShowFileOperationLabels = true; });
            try
            {
                var dlg = new SettingsWindow(w, w.Settings, templates: new List<Core.Templates.FileTemplate>(), host: w.SettingsHostFor());
                dlg.SelectTab("Toolbar");
                var page = (StackPanel)((ScrollViewer)dlg.Tabs.SelectedContent).Content;
                var check = page.Children.OfType<CheckBox>().Single(c => (c.Content as TextBlock)?.Text == "Show labels for secondary toolbar buttons");
                Assert.True(check.IsChecked); check.IsChecked = false;
                Assert.True(dlg.TrySave()); dlg.Close(); w.ApplySettings(relist: false, refilter: false);
                Assert.False(AppSettings.Load().ShowSecondaryToolbarLabels);
                Assert.Equal(Visibility.Visible, Button(w, "settings").Visibility);
                foreach (var (id, element) in w.ToolbarForTest.Where(b => b.Id != null))
                {
                    var button = (Button)element; var panel = (StackPanel)button.Content;
                    Assert.Equal(w.Settings.ShowToolbarLabel(id!) ? Visibility.Visible : Visibility.Collapsed, panel.Children[1].Visibility);
                    Assert.NotNull(button.ToolTip); Assert.True(button.MinWidth >= 34);
                }
                var shortFolder = t.Dir("A"); var longFolder = t.Dir(new string('L', 100));
                w.AddTabForTest(shortFolder); w.AddTabForTest(longFolder);
                Sta.DoEvents(150); w.UpdateLayout();
                var headers = VisualTree.FindDescendants<Border>(w).Where(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Tab: ")).ToList();
                foreach (var header in headers)
                {
                    var close = VisualTree.FindDescendants<Button>(header).Single();
                    double right = close.TranslatePoint(new Point(close.ActualWidth, 0), header).X;
                    Assert.InRange(header.ActualWidth - right, 5, 8);
                    var text = VisualTree.FindDescendants<TextBlock>(header).First(b => b.TextTrimming == TextTrimming.CharacterEllipsis);
                    Assert.True(text.TranslatePoint(new Point(text.ActualWidth, 0), header).X <= close.TranslatePoint(new Point(), header).X);
                    Assert.NotNull(header.ToolTip);
                }
                double X(string id) => Button(w, id).TranslatePoint(new Point(), (UIElement)w.Content).X;
                Assert.True(X("compare") < X("settings")); Assert.True(X("settings") < X("preview"));
                Sta.Render(w, "toolbar-icons-and-tab-close");
                w.Width = 700; Sta.DoEvents(100);
                Assert.True(Button(w, "settings").IsVisible); Assert.True(Button(w, "settings").ActualWidth >= 30);
                // Render a fresh narrow window: WPF's off-screen VisualBrush retains stale bounds after a resize.
                var narrow = MainWindowUiTests.OpenWindow(t, t.Path, s => { s.WindowWidth = 700; s.ShowSecondaryToolbarLabels = false; });
                try
                {
                    Sta.DoEvents(100);
                    var settings = Button(narrow, "settings");
                    Assert.True(settings.TranslatePoint(new Point(settings.ActualWidth, 0), (UIElement)narrow.Content).X <= narrow.Width);
                    Sta.Render(narrow, "toolbar-narrow");
                }
                finally { narrow.Close(); }
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Native_DIB_and_DIBV5_decode_without_changing_pixels()
    {
        Sta.Run(() =>
        {
            foreach (bool v5 in new[] { false, true })
            {
                int header = v5 ? 124 : 40;
                var dib = new byte[header + 16];
                void I(int offset, int value) => BitConverter.GetBytes(value).CopyTo(dib, offset);
                I(0, header); I(4, 2); I(8, -2); // top-down 2×2 bitmap
                BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
                BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
                I(16, v5 ? 3 : 0); I(20, 16);
                if (v5) { I(40, 0x00ff0000); I(44, 0x0000ff00); I(48, 0x000000ff); I(52, unchecked((int)0xff000000)); I(56, 0x73524742); }
                var expected = new byte[] { 3, 2, 1, 255, 30, 20, 10, 255, 60, 50, 40, 255, 90, 80, 70, 255 };
                expected.CopyTo(dib, header);
                var decoded = ClipboardImageFile.DecodeDib(dib);
                var normalized = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
                var actual = new byte[16]; normalized.CopyPixels(actual, 8, 0);
                Assert.Equal(expected, actual);
            }
        });
    }

    [Fact]
    public void Invalid_clipboard_data_does_not_create_files_and_busy_access_recovers_with_bounded_retry()
    {
        using var t = new TempDir(); t.File("seed.txt");
        Sta.Run(() => WithClipboard(clipboard =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var corrupt = new DataObject(); corrupt.SetData("PNG", new MemoryStream(new byte[] { 1, 2, 3 }));
                clipboard.Data = corrupt; Notify(w);
                w.HandleFileShortcut(Key.V, true, false); WaitPaste(w);
                Assert.Empty(Directory.GetFiles(t.Path, "Screenshot *.png"));
                var empty = new DataObject(); empty.SetData(DataFormats.FileDrop, Array.Empty<string>());
                clipboard.Data = empty; Notify(w); Assert.False(Button(w, "paste").IsEnabled);
                clipboard.Data = new DataObject(DataFormats.Bitmap, Bitmap()); clipboard.Busy = true; Notify(w);
                Assert.False(Button(w, "paste").IsEnabled);
                clipboard.Busy = false;
                Sta.WaitUntil(() => Button(w, "paste").IsEnabled, "bounded retry after clipboard is released");
            }
            finally { w.Close(); }
        }));
    }

    [Fact]
    public void Settings_button_uses_existing_dialog_and_preview_still_toggles()
    {
        using var t = new TempDir(); t.File("seed.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                int opened = 0;
                w.SettingsDialogForTest = dialog =>
                {
                    opened++;
                    Assert.Same(w, dialog.Owner);
                    Assert.Equal("Settings", dialog.Title);
                    Assert.Equal(8, dialog.Tabs.Items.Count);
                    dialog.Close();
                    return false; // intercept the modal display so the real desktop is never touched
                };
                Click(Button(w, "settings")); Assert.Equal(1, opened);
                Assert.False(w.Settings.PreviewVisible);
                Click(Button(w, "preview")); Assert.True(w.Settings.PreviewVisible);
                Click(Button(w, "preview")); Assert.False(w.Settings.PreviewVisible);
            }
            finally { w.Close(); }
        });
    }
}
