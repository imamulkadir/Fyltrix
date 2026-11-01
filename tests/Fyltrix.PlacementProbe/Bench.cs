using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Preview;
using Fyltrix.UI;

namespace Fyltrix.PlacementProbe;

/// <summary>
/// Resource benchmark: the real Fyltrix window (per-monitor DPI aware, DWM-cloaked so nothing shows or takes focus), with
/// its own settings and WebView2 profile, on a fixed data set. Measures startup, idle CPU / RAM / disk, many tabs, folder
/// navigation, a 20 000-file folder, a recursive search, previews and clean-up, and writes the numbers as JSON.
///   Fyltrix.PlacementProbe bench &lt;data folder&gt; &lt;result.json&gt;
/// </summary>
internal static class Bench
{
    private static readonly Dictionary<string, object> R = new();
    /// <summary>Start with the preview pane on (the default) or off (BENCH_PREVIEW=0).</summary>
    private static readonly bool PreviewOn = Environment.GetEnvironmentVariable("BENCH_PREVIEW") != "0";

    public static int Run(string dataRoot, string outFile, DateTime processStart)
    {
        var total = Stopwatch.StartNew();
        try
        {
            dataRoot = LongPath(dataRoot); // %TEMP% is often given in 8.3 form (IMAMUL~1); Fyltrix shows long paths
            var data = MakeData(dataRoot);
            MainWindow.PreviewReleaseAfter = TimeSpan.FromSeconds(2); // the real 30 s would only make the run longer
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); // as App.Main does
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Apply(app);
            AppSettings.Folder = Path.Combine(dataRoot, "settings-" + Environment.ProcessId);
            Fyltrix.Core.Media.ThumbnailService.DiskCacheFolder = Path.Combine(dataRoot, "thumbs");
            PreviewPane.WebDataFolder = Path.Combine(dataRoot, "webview2");

            // ---- startup: process start → window shown → first folder listed (preview pane on, as by default)
            var me0 = Process.GetCurrentProcess();
            GetProcessIoCounters(me0.Handle, out var ioStart);
            R["startupToBenchMainMs"] = (DateTime.Now - processStart).TotalMilliseconds;
            var settings = new AppSettings { PreviewVisible = PreviewOn, WindowWidth = 1400, WindowHeight = 850 };
            var phase = Stopwatch.StartNew();
            long before = Stopwatch.GetTimestamp();
            var w = new MainWindow(settings, data.Small);
            R["ctorFieldsMs"] = Math.Round((MainWindow.CtorBodyStarted - before) * 1000.0 / Stopwatch.Frequency, 1);
            R["mainWindowCtorMs"] = phase.Elapsed.TotalMilliseconds;
            R["ctorSteps"] = w.StartupMarks.ToDictionary(m => m.Step, m => m.Ms);
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.SourceInitialized += (_, _) => Cloak(new WindowInteropHelper(w).Handle);
            phase.Restart();
            w.Show();
            R["showMs"] = phase.Elapsed.TotalMilliseconds;
            WaitUntil(() => w.View is { Items.Count: > 0 } v && AppSettings.SameFolder(v.CurrentPath, data.Small), 30000);
            R["startupToListedMs"] = (DateTime.Now - processStart).TotalMilliseconds;
            if (PreviewOn)
            {
                WaitUntil(() => w.PreviewForTest.IsReady, 30000);
                R["startupToPreviewReadyMs"] = (DateTime.Now - processStart).TotalMilliseconds;
            }

            // ---- idle: settle, then 10 s doing nothing
            Pump(4000);
            R["idle"] = Idle(10_000);

            // ---- navigation between ordinary folders (time until the new folder's items are listed)
            var navTimes = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                var target = data.Navigate[i % data.Navigate.Length];
                navTimes.Add(NavigateMs(w, target));
            }
            R["navigateMs"] = Stats(navTimes);

            // ---- a folder of 20 000 files
            var bigTimes = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                NavigateMs(w, data.Small);
                bigTimes.Add(NavigateMs(w, data.Big));
            }
            R["bigFolderMs"] = Stats(bigTimes);
            R["bigFolderItems"] = w.View!.Items.Count;
            Pump(1500);
            R["afterBigFolder"] = Mem();

            // ---- recursive search
            NavigateMs(w, data.Tree);
            var searchTimes = new List<double>();
            int results = 0;
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                w.View!.StartSearch("file_1");
                WaitUntil(() => !w.View.IsSearching && w.View.IsSearchResults, 60000);
                searchTimes.Add(sw.Elapsed.TotalMilliseconds);
                results = w.View.Items.Count;
                w.View.EndSearch();
                Pump(300);
            }
            R["searchMs"] = Stats(searchTimes);
            R["searchResults"] = results;
            R["searchExpected"] = data.TreeMatches;

            // ---- preview: text, code, markdown, image (time until the page has loaded)
            NavigateMs(w, data.Previews);
            if (!PreviewOn) w.SetPreviewVisibleForTest(true);
            var previewTimes = new Dictionary<string, double>();
            var t0 = Stopwatch.StartNew();
            WaitUntil(() => w.PreviewForTest.IsReady, 30000);
            R["previewEngineWaitMs"] = t0.Elapsed.TotalMilliseconds;
            for (int round = 1; round <= 2; round++)
                foreach (var f in Directory.GetFiles(data.Previews).OrderBy(x => x))
                    previewTimes[$"{Path.GetFileName(f)} #{round}"] = PreviewMs(w, f);
            R["previewMs"] = previewTimes;
            R["withPreviews"] = Mem();

            // ---- many tabs: open 12, close 11, three times; memory must not keep growing
            var cycles = new List<object>();
            int cyclesN = int.TryParse(Environment.GetEnvironmentVariable("BENCH_CYCLES"), out var cn) ? cn : 3;
            for (int c = 0; c < cyclesN; c++)
            {
                for (int i = 0; i < 12; i++)
                {
                    w.AddTabForTest(data.Navigate[i % data.Navigate.Length]);
                    WaitUntil(() => w.View!.Items.Count > 0, 20000);
                }
                Pump(800);
                var open = Mem();
                while (w.TabCountForTest > 1) w.CloseTabForTest(w.TabCountForTest - 1);
                Pump(800);
                var closed = Mem(collect: true);
                cycles.Add(new { open, closed });
            }
            R["tabCycles"] = cycles;

            // ---- preview pane closed: what is released
            w.SetPreviewVisibleForTest(false);
            Pump((int)MainWindow.PreviewReleaseAfter.TotalMilliseconds + 1500);
            R["previewHidden"] = Mem(collect: true);
            R["idleAfterWork"] = Idle(10_000);

            GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var ioEnd);
            R["ioWholeRun"] = new
            {
                readMB = Math.Round((ioEnd.ReadTransferCount - ioStart.ReadTransferCount) / 1048576.0, 1),
                writeMB = Math.Round((ioEnd.WriteTransferCount - ioStart.WriteTransferCount) / 1048576.0, 1),
            };
            w.Close();
        }
        catch (Exception ex) { R["error"] = ex.ToString(); }
        R["benchTotalMs"] = total.Elapsed.TotalMilliseconds;
        File.WriteAllText(outFile, JsonSerializer.Serialize(R, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // ------------------------------------------------------------------ measurements

    private static double NavigateMs(MainWindow w, string path)
    {
        var view = w.View!;
        bool loaded = false;
        EventHandler h = (_, _) => loaded = true;
        view.ItemsLoaded += h;
        var sw = Stopwatch.StartNew();
        view.Navigate(path);
        WaitUntil(() => loaded && view.CurrentPath == path, 60000);
        var ms = sw.Elapsed.TotalMilliseconds;
        view.ItemsLoaded -= h;
        return ms;
    }

    private static double PreviewMs(MainWindow w, string file)
    {
        var pane = w.PreviewForTest;
        var web = pane.Web.CoreWebView2;
        bool done = false;
        EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs> h = (_, _) => done = true;
        web.NavigationCompleted += h;
        var sw = Stopwatch.StartNew();
        w.View!.SelectPaths(new[] { file });
        WaitUntil(() => done && pane.CurrentPath == file, 30000);
        // and until the viewer replaced its "Loading…" placeholder
        WaitUntil(() =>
        {
            var t = web.ExecuteScriptAsync("String(!!document.getElementById('root') && !(document.body.innerText||'').includes('Loading…'))");
            while (!t.IsCompleted) Pump(5);
            return t.Result == "\"true\"";
        }, 30000);
        var ms = sw.Elapsed.TotalMilliseconds;
        web.NavigationCompleted -= h;
        return Math.Round(ms, 1);
    }

    private static object Idle(int ms)
    {
        var me = Process.GetCurrentProcess();
        var tree0 = TreeSnapshot();
        var cpu0 = me.TotalProcessorTime;
        GetProcessIoCounters(me.Handle, out var io0);
        var sw = Stopwatch.StartNew();
        Pump(ms);
        me.Refresh();
        var cpu1 = me.TotalProcessorTime;
        GetProcessIoCounters(me.Handle, out var io1);
        var tree1 = TreeSnapshot();
        double secs = sw.Elapsed.TotalSeconds;
        return new
        {
            seconds = Math.Round(secs, 1),
            cpuMsPerSec = Math.Round((cpu1 - cpu0).TotalMilliseconds / secs, 2),
            childCpuMsPerSec = Math.Round((tree1.childCpu - tree0.childCpu) / secs, 2),
            ioReadBytes = (long)(io1.ReadTransferCount - io0.ReadTransferCount),
            ioWriteBytes = (long)(io1.WriteTransferCount - io0.WriteTransferCount),
            ioOtherOps = (long)(io1.OtherOperationCount - io0.OtherOperationCount),
            ioReadOps = (long)(io1.ReadOperationCount - io0.ReadOperationCount),
            ioWriteOps = (long)(io1.WriteOperationCount - io0.WriteOperationCount),
            mem = Mem(),
        };
    }

    private static object Mem(bool collect = false)
    {
        if (collect) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        var me = Process.GetCurrentProcess();
        me.Refresh();
        var tree = TreeSnapshot();
        return new
        {
            workingSetMB = Math.Round(me.WorkingSet64 / 1048576.0, 1),
            privateMB = Math.Round(me.PrivateMemorySize64 / 1048576.0, 1),
            gcHeapMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            threads = me.Threads.Count,
            handles = me.HandleCount,
            children = tree.children,
            childrenWorkingSetMB = Math.Round(tree.childWs / 1048576.0, 1),
            childrenPrivateMB = Math.Round(tree.childPrivate / 1048576.0, 1),
        };
    }

    private static object Stats(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return new { median = Math.Round(s[s.Count / 2], 1), p90 = Math.Round(s[(int)(s.Count * 0.9)], 1), max = Math.Round(s[^1], 1), n = s.Count };
    }

    /// <summary>All processes started by this one, directly or not (WebView2's browser, renderers, GPU…).</summary>
    private static (int children, long childWs, long childPrivate, double childCpu) TreeSnapshot()
    {
        var parents = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(2, 0);
        try
        {
            var e = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref e))
                do parents[e.th32ProcessID] = e.th32ParentProcessID; while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        var mine = new HashSet<int> { Environment.ProcessId };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pid, parent) in parents)
                if (!mine.Contains(pid) && mine.Contains(parent) && pid != parent) { mine.Add(pid); grew = true; }
        }
        mine.Remove(Environment.ProcessId);
        long ws = 0, priv = 0;
        double cpu = 0;
        foreach (var pid in mine)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                ws += p.WorkingSet64;
                priv += p.PrivateMemorySize64;
                cpu += p.TotalProcessorTime.TotalMilliseconds;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return (mine.Count, ws, priv, cpu);
    }

    // ------------------------------------------------------------------ data set (made once, reused)

    private sealed record Data(string Small, string Big, string Tree, int TreeMatches, string Previews, string[] Navigate);

    private static Data MakeData(string root)
    {
        var d = Path.Combine(root, "data");
        var small = Path.Combine(d, "small");
        var big = Path.Combine(d, "big");
        var tree = Path.Combine(d, "tree");
        var previews = Path.Combine(d, "previews");
        var nav = Enumerable.Range(0, 6).Select(i => Path.Combine(d, "nav", "folder" + i)).ToArray();
        var marker = Path.Combine(d, "ready-v1");
        if (!File.Exists(marker))
        {
            if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(small);
            for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(small, $"doc {i:000}.txt"), "small " + i);
            foreach (var n in nav)
            {
                Directory.CreateDirectory(n);
                for (int i = 0; i < 300; i++) File.WriteAllText(Path.Combine(n, $"{(i % 3 == 0 ? "report" : i % 3 == 1 ? "photo" : "notes")} {i:000}.{(i % 3 == 0 ? "pdf" : i % 3 == 1 ? "jpg" : "md")}"), "x");
                for (int i = 0; i < 20; i++) Directory.CreateDirectory(Path.Combine(n, "sub " + i));
            }
            Directory.CreateDirectory(big);
            for (int i = 0; i < 20_000; i++) File.WriteAllText(Path.Combine(big, $"item_{i:00000}.{(i % 4) switch { 0 => "txt", 1 => "log", 2 => "csv", _ => "json" }}"), "");
            // tree: 4 levels × 6 folders, 15 files each (9 330 files)
            void Fill(string dir, int depth)
            {
                Directory.CreateDirectory(dir);
                for (int i = 0; i < 15; i++) File.WriteAllText(Path.Combine(dir, $"file_{i}.txt"), "t");
                if (depth < 4) for (int i = 0; i < 6; i++) Fill(Path.Combine(dir, $"dir_{depth}_{i}"), depth + 1);
            }
            Fill(tree, 0);
            Directory.CreateDirectory(previews);
            File.WriteAllText(Path.Combine(previews, "a.txt"), string.Join("\n", Enumerable.Range(1, 2000).Select(i => "line " + i)));
            File.WriteAllText(Path.Combine(previews, "b.cs"), string.Join("\n", Enumerable.Range(1, 800).Select(i => $"class C{i} {{ int X = {i}; }}")));
            File.WriteAllText(Path.Combine(previews, "c.md"), string.Join("\n\n", Enumerable.Range(1, 300).Select(i => $"## Heading {i}\nSome *text* {i}")));
            File.WriteAllText(Path.Combine(previews, "d.json"), "[" + string.Join(",", Enumerable.Range(1, 3000).Select(i => $"{{\"id\":{i},\"name\":\"n{i}\"}}")) + "]");
            File.WriteAllText(marker, "ok");
        }
        int files = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories).Count(f => Path.GetFileName(f).Contains("file_1", StringComparison.OrdinalIgnoreCase));
        int dirs = 0;
        return new Data(small, big, tree, files + dirs, previews, nav);
    }

    // ------------------------------------------------------------------ plumbing

    private static string LongPath(string path)
    {
        var sb = new System.Text.StringBuilder(1024);
        return GetLongPathName(path, sb, sb.Capacity) > 0 ? sb.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetLongPathName(string shortPath, System.Text.StringBuilder longPath, int size);

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("bench step timed out");
            Pump(5);
        }
    }

    private static void Cloak(IntPtr h)
    {
        int on = 1;
        DwmSetWindowAttribute(h, 13, ref on, 4);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public int dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
