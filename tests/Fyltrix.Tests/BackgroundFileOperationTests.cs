using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>
/// Copy/move run off the UI thread, so Fyltrix stays usable while files transfer (like Explorer). Each transfer is held
/// open on its shell thread by a test gate while the window is used. In-memory clipboard, temporary folders, off-screen
/// windows; nothing is shown on the desktop and no focus is taken.
/// </summary>
[Collection("UI")]
public class BackgroundFileOperationTests
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private static void Notify(MainWindow w) => SendMessage(new WindowInteropHelper(w).Handle, 0x031D, IntPtr.Zero, IntPtr.Zero); // WM_CLIPBOARDUPDATE

    private static DataObject Files(bool cut, params string[] paths)
    {
        var list = new StringCollection(); list.AddRange(paths);
        var data = new DataObject(); data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        return data;
    }

    private static void WithClipboard(Action<MemoryFileClipboard> test)
    {
        var previous = MainWindow.ClipboardFactory;
        var clipboard = new MemoryFileClipboard();
        MainWindow.ClipboardFactory = () => clipboard;
        try { test(clipboard); }
        finally { MainWindow.ClipboardFactory = previous; }
    }

    /// <summary>Holds each transfer at its start on the shell thread until released; records where it ran.</summary>
    private sealed class Gate : IDisposable
    {
        public readonly ManualResetEventSlim Started = new(), Release = new();
        public int ThreadId; public ApartmentState Apartment;
        public void Hold()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            Apartment = Thread.CurrentThread.GetApartmentState();
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
        }
        public void Dispose() { Release.Set(); Started.Dispose(); }
    }

    private static void Paste(MainWindow w, MemoryFileClipboard clipboard, DataObject data)
    {
        clipboard.Data = data;
        Notify(w);
        Assert.True(w.HandleFileShortcut(Key.V, true, false));
    }

    [Fact]
    public void Copy_runs_in_the_background_while_Fyltrix_stays_usable_and_closing_waits()
    {
        using var t = new TempDir();
        var source = t.Dir("source"); var target = t.Dir("target"); var other = t.Dir("elsewhere");
        var a = t.File("source/a.txt", "alpha"); var b = t.File("source/b.txt", "beta");
        t.File("target/seed.txt", "seed"); t.File("elsewhere/x.txt", "x");
        Sta.Run(() => WithClipboard(clipboard =>
        {
            var w = MainWindowUiTests.OpenWindow(t, target);
            using var gate = new Gate();
            int blocked = 0;
            try
            {
                w.TransferStartedForTest = gate.Hold;
                w.TransferCloseBlockedForTest = n => blocked = n;
                Paste(w, clipboard, Files(false, a, b));

                // The UI thread keeps running (this wait pumps it) while the transfer sits on its own STA thread.
                Sta.WaitUntil(() => gate.Started.IsSet, "transfer started");
                Assert.NotEqual(Environment.CurrentManagedThreadId, gate.ThreadId);
                Assert.Equal(ApartmentState.STA, gate.Apartment);
                Assert.Equal(1, w.ActiveTransferCount);
                Assert.True(w.IsEnabled);
                Assert.False(w.PasteTask.IsCompleted);

                // Browse elsewhere and put something else on the clipboard: the running copy keeps its own targets.
                w.View!.Navigate(other);
                Sta.WaitUntil(() => w.View.CurrentPath == other && w.View.Items.Any(i => i.Name == "x.txt"), "navigated during copy");
                clipboard.Data = Files(false, Path.Combine(other, "x.txt")); Notify(w);
                Assert.Equal("Wait for file operations to finish.", w.UpdateCloseProblem());

                // Closing is refused (not cut off half-way) and says why.
                w.Close();
                Assert.Equal(1, blocked);
                Assert.True(w.IsLoaded);

                gate.Release.Set();
                Sta.WaitUntil(() => w.PasteTask.IsCompleted, "copy finished");
                w.PasteTask.GetAwaiter().GetResult();
                Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
                Assert.Equal("beta", File.ReadAllText(Path.Combine(target, "b.txt")));
                Assert.False(File.Exists(Path.Combine(other, "a.txt")));
                Assert.True(File.Exists(a) && File.Exists(b));
                Assert.Equal(0, w.ActiveTransferCount);
                Assert.Equal("Copied 2 items.", w.OperationStatusForTest);
                Assert.Null(w.UpdateCloseProblem());
            }
            finally { gate.Release.Set(); w.TransferCloseBlockedForTest = null; w.Close(); }
        }));
    }

    [Fact]
    public void Move_finishes_into_its_original_folder_and_keeps_a_newer_clipboard()
    {
        using var t = new TempDir();
        var source = t.Dir("source"); var target = t.Dir("target"); var other = t.Dir("elsewhere");
        var a = t.File("source/a.txt", "alpha"); var c = t.File("elsewhere/c.txt", "gamma");
        t.File("target/seed.txt", "seed");
        Sta.Run(() => WithClipboard(clipboard =>
        {
            var w = MainWindowUiTests.OpenWindow(t, target);
            using var gate = new Gate();
            try
            {
                w.TransferStartedForTest = gate.Hold;
                Paste(w, clipboard, Files(true, a));
                Sta.WaitUntil(() => gate.Started.IsSet, "move started");

                w.View!.Navigate(other);
                Sta.WaitUntil(() => w.View.CurrentPath == other && w.View.Items.Any(i => i.Name == "c.txt"), "navigated during move");
                var newer = Files(false, c);
                clipboard.Data = newer; Notify(w); // the user copies something else meanwhile

                gate.Release.Set();
                Sta.WaitUntil(() => w.PasteTask.IsCompleted, "move finished");
                w.PasteTask.GetAwaiter().GetResult();
                Assert.False(File.Exists(a));
                Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
                Assert.False(File.Exists(Path.Combine(other, "a.txt")));
                Assert.Same(newer, clipboard.Data); // the finished cut must not clear or replace the newer selection
                Assert.Equal("Moved 1 item.", w.OperationStatusForTest);
            }
            finally { gate.Release.Set(); w.Close(); }
        }));
    }

    [Fact]
    public void Name_conflicts_are_asked_on_the_UI_thread_without_disabling_Fyltrix()
    {
        using var t = new TempDir();
        var source = t.Dir("source"); var target = t.Dir("target");
        var a = t.File("source/a.txt", "new");
        t.File("target/a.txt", "old");
        Sta.Run(() => WithClipboard(clipboard =>
        {
            var w = MainWindowUiTests.OpenWindow(t, target);
            bool onUi = false, enabled = false;
            try
            {
                w.ConflictForTest = _ => { onUi = w.Dispatcher.CheckAccess(); enabled = w.IsEnabled; return (ConflictChoice.KeepBoth, false); };
                Paste(w, clipboard, Files(false, a));
                Sta.WaitUntil(() => w.PasteTask.IsCompleted, "paste with conflict");
                w.PasteTask.GetAwaiter().GetResult();
                Assert.True(onUi);
                Assert.True(enabled);
                Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.txt"))); // never overwritten without a choice
                Assert.Contains(Directory.GetFiles(target), f => Path.GetFileName(f) != "a.txt" && File.ReadAllText(f) == "new");
            }
            finally { w.ConflictForTest = null; w.Close(); }
        }));
    }

    [Fact]
    public void Drag_and_drop_transfers_also_run_in_the_background()
    {
        using var t = new TempDir();
        var source = t.Dir("source"); var target = t.Dir("target");
        var a = t.File("source/a.txt", "alpha");
        t.File("target/seed.txt", "seed");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, target);
            using var gate = new Gate();
            try
            {
                w.TransferStartedForTest = gate.Hold;
                w.DropFiles(w.View!, new[] { a }, target, DragDropEffects.Copy);
                Sta.WaitUntil(() => gate.Started.IsSet, "drop transfer started");
                Assert.Equal(1, w.ActiveTransferCount);
                Assert.True(w.IsEnabled);
                gate.Release.Set();
                Sta.WaitUntil(() => w.DropTask.IsCompleted && w.ActiveTransferCount == 0, "drop finished");
                w.DropTask.GetAwaiter().GetResult();
                Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "a.txt")));
            }
            finally { gate.Release.Set(); w.Close(); }
        });
    }

    [Fact]
    public void Progress_window_is_not_owned_and_does_not_take_focus()
    {
        Sta.Run(() =>
        {
            var owner = new Window();
            var ctor = typeof(JobWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(Window), typeof(string), typeof(CancellationTokenSource), typeof(bool) })!;
            using var cts = new CancellationTokenSource();
            var job = (JobWindow)ctor.Invoke(new object[] { owner, "Copying", cts, true });
            Assert.Null(job.Owner);             // an owned window can never go behind the main window
            Assert.False(job.ShowActivated);    // appearing doesn't take the keyboard
            Assert.True(job.ShowInTaskbar);     // and it can be brought back from the taskbar
        });
    }
}
