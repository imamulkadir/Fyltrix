using System.Windows;
using Fyltrix.Core;
using Fyltrix.Core.Tools;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class ToolsUiTests
{
    [Fact]
    public void Batch_rename_window_previews_renames_and_undo_restores()
    {
        using var t = new TempDir();
        foreach (var n in new[] { "b.txt", "a.txt", "c.txt" }) t.File("d/" + n, n);
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["d"]);
            try
            {
                var view = w.View!;
                foreach (var i in view.Items) view.List.SelectedItems.Add(i);
                var dlg = new BatchRenameWindow(w, view.SelectedItems.Select(i => i.FullPath).OrderBy(p => p).ToList());
                RenameResult? done = null;
                dlg.Renamed += (_, r) => done = r;
                Sta.ShowOffscreen(dlg);
                dlg.SetTemplate("Note {n:00}");
                Sta.Render(dlg, "batch-rename");
                dlg.DoRename();
                Assert.NotNull(done);
                Assert.Equal(new[] { "Note 01.txt", "Note 02.txt", "Note 03.txt" }, Directory.GetFiles(t["d"]).Select(Path.GetFileName).OrderBy(n => n));
                Assert.Equal("a.txt", File.ReadAllText(t["d/Note 01.txt"]));
            }
            finally { w.Close(); }
            var restored = BatchRename.Revert(Directory.GetFiles(t["d"]).OrderBy(p => p).Zip(new[] { "a.txt", "b.txt", "c.txt" }, (to, from) => (t["d/" + from], to)).ToList());
            Assert.Empty(restored);
            Assert.Equal("b.txt", File.ReadAllText(t["d/b.txt"]));
        });
    }

    [Fact]
    public void Duplicates_window_scans_selects_and_protects_last_copy()
    {
        using var t = new TempDir();
        var same = new string('s', 5000);
        t.File("x/one.txt", same); t.File("x/two.txt", same); t.File("x/three.txt", new string('o', 5000));
        Sta.Run(() =>
        {
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var w = new DuplicatesWindow(owner, new[] { t["x"] });
            Sta.ShowOffscreen(w);
            try
            {
                var scan = w.ScanAsync();
                Sta.WaitUntil(() => scan.IsCompleted, "scan");
                Assert.Equal(3, w.Rows.Count); // header + 2 files
                w.SelectAllBut(f => f.OrderBy(x => x.Path).First());
                Assert.Single(w.Rows, r => r.Checked);
                foreach (var r in w.Rows) r.Checked = !r.IsHeader;
                Sta.Render(w, "duplicates");
                // every copy selected: the window refuses (a dialog explains) and nothing is deleted
                var blocker = Task.Run(async () => { await Task.Delay(800); CloseMessageBoxes(); });
                w.RemoveSelected(confirm: false);
                Assert.True(File.Exists(t["x/one.txt"]) && File.Exists(t["x/two.txt"]));
            }
            finally { w.Close(); owner.Close(); }
        });
    }

    [Fact]
    public void Folder_size_window_measures_and_drills_down()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t.File("r/big/a.bin"), new byte[40_000]);
        File.WriteAllBytes(t.File("r/small/b.bin"), new byte[10_000]);
        Sta.Run(() =>
        {
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var w = new FolderSizeWindow(owner, t["r"]);
            Sta.ShowOffscreen(w);
            try
            {
                Sta.WaitUntil(() => w.Current != null, "analysis");
                Assert.Equal(50_000, w.Current!.Size);
                Assert.Equal("big", w.Current.Children[0].Name);
                Sta.DoEvents(200);
                Sta.Render(w, "folder-sizes");
                w.ShowNode(w.Current.Children[0]);
                Assert.Equal(40_000, w.Current.Size);
            }
            finally { w.Close(); owner.Close(); }
        });
    }

    [Fact]
    public void Checksum_window_computes_and_compares()
    {
        using var t = new TempDir();
        var f = t.File("abc.txt", "abc");
        Sta.Run(() =>
        {
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var w = new ChecksumWindow(owner, new[] { f });
            Sta.ShowOffscreen(w);
            try
            {
                Sta.WaitUntil(() => w.Rows.Count == 1, "hash");
                w.SetExpected("BA7816BF 8F01CFEA 414140DE 5DAE2223 B00361A3 96177A9C B410FF61 F20015AD");
                Assert.StartsWith("✓ Match", w.Verdict);
                w.SetExpected("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ae");
                Assert.StartsWith("✗ No match", w.Verdict);
                w.SetExpected("900150983cd24fb0d6963f7d28e17f72");
                Assert.Contains("Tick MD5", w.Verdict);
            }
            finally { w.Close(); owner.Close(); }
        });
    }

    [Fact]
    public void Advanced_search_and_labels_in_the_file_list()
    {
        using var t = new TempDir();
        t.File("p/notes/todo.txt", "buy milk");
        t.File("p/notes/done.txt", "nothing");
        t.File("p/readme.md", "milk is mentioned");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["p"]);
            try
            {
                var view = w.View!;
                view.StartSearch(new SearchCriteria { Content = "milk", Type = ItemTypeFilter.FilesOnly }, "containing milk");
                Sta.WaitUntil(() => view.IsSearchResults && !view.IsSearching, "search done");
                Assert.Equal(new[] { "readme.md", "todo.txt" }, view.Items.Select(i => i.Name).OrderBy(n => n));

                // label one result via the context menu and see the dot appear
                var todo = view.Items.Single(i => i.Name == "todo.txt");
                var menu = w.BuildContextMenu(view, new List<FileItem> { todo });
                MainWindowUiTests.Click(MainWindowUiTests.Item(menu, "Green"));
                Sta.WaitUntil(() => todo.HasLabel, "label shown");
                MainWindowUiTests.Click(MainWindowUiTests.Item(w.BuildContextMenu(view, new List<FileItem> { todo }), "No label"));
                Sta.WaitUntil(() => !todo.HasLabel, "label removed");
                Sta.Render(w, "search-results");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Pdf_tools_window_edits_pages_into_a_new_file()
    {
        using var t = new TempDir();
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            for (int i = 0; i < 3; i++) { var p = doc.AddPage(); p.Width = PdfSharp.Drawing.XUnit.FromPoint(100 + i); }
            doc.Save(t["d.pdf"]);
        }
        Sta.Run(() =>
        {
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var w = new PdfToolsWindow(owner, t["d.pdf"]);
            var created = new List<string>();
            w.FilesCreated += (_, f) => created.AddRange(f);
            Sta.ShowOffscreen(w);
            try
            {
                Sta.WaitUntil(() => w.Pages.Count == 3 && w.Pages.All(p => p.Thumb != null), "thumbnails", 20000);
                w.Select(0);
                w.MoveSelected(+1);                 // 1,0,2
                w.RotateSelected(90);
                w.Select(2);
                w.RemoveSelected();                 // 1,0
                Sta.Render(w, "pdf-tools");
                var output = w.SaveComposition(t["out.pdf"]);
                Assert.Equal(t["out.pdf"], output);
                Assert.Equal(new[] { t["out.pdf"] }, created);
                using var d = PdfSharp.Pdf.IO.PdfReader.Open(t["out.pdf"], PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                Assert.Equal(new[] { 1, 0 }, d.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => (int)Math.Round(p.Width.Point) - 100));
                Assert.Equal(new[] { 0, 90 }, d.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => p.Rotate));
            }
            finally { w.Close(); owner.Close(); }
        });
    }

    private static void CloseMessageBoxes()
    {
        // MessageBox windows have class #32770; send Enter to the one owned by this process
        var pid = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (owner == pid && cls.ToString() == "#32770") PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
