using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Fyltrix.Core;
using Fyltrix.Preview;
using Fyltrix.UI;
using Microsoft.Web.WebView2.Core;

namespace Fyltrix.Tests;

/// <summary>
/// The preview must survive WebView2 engine failures instead of staying blank/broken until restart
/// (docs/PREVIEW_RELIABILITY_INVESTIGATION.md). Failures are produced only in the test host's own isolated WebView2
/// profile: the processes stopped here are children of that profile's browser, never a Fyltrix the user has open.
/// </summary>
[Collection("UI")]
public class PreviewRecoveryTests
{
    private static (Window Window, PreviewPane Pane) Host(bool nativePdfInput = false)
    {
        PreviewPane.Settings ??= new AppSettings();
        var pane = new PreviewPane(showHeader: true, showPopOut: false, nativePdfInput: nativePdfInput);
        var window = new Window { Width = 900, Height = 700, Content = pane };
        Sta.ShowOffscreen(window);
        return (window, pane);
    }

    private static string BodyText(PreviewPane pane)
    {
        if (!pane.IsReady) return "";
        try
        {
            var task = pane.Web.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            var sw = Stopwatch.StartNew();
            while (!task.IsCompleted && sw.ElapsedMilliseconds < 2000) Sta.DoEvents(20);
            return task.IsCompletedSuccessfully ? JsonSerializer.Deserialize<string>(task.Result) ?? "" : "";
        }
        catch (InvalidOperationException) { return ""; } // engine being replaced
    }

    private static void WaitShows(PreviewPane pane, string token, string what, int timeoutMs = 30000) =>
        Sta.WaitUntil(() => BodyText(pane).Contains(token, StringComparison.Ordinal), what, timeoutMs);

    /// <summary>Stops the browser process of the test host's own isolated profile (and its child processes).</summary>
    private static void KillTestBrowser(PreviewPane pane)
    {
        Assert.StartsWith(Path.GetTempPath(), PreviewPane.WebDataFolder, StringComparison.OrdinalIgnoreCase);
        var browser = Process.GetProcessById((int)pane.Web.CoreWebView2.BrowserProcessId);
        Assert.Equal("msedgewebview2", browser.ProcessName, ignoreCase: true);
        browser.Kill(entireProcessTree: true);
    }

    private static int CountUnhandled(Action action)
    {
        int count = 0;
        void Handler(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) { count++; e.Handled = true; }
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += Handler;
        try { action(); }
        finally { dispatcher.UnhandledException -= Handler; }
        return count;
    }

    [Fact]
    public void Browser_process_crash_is_recovered_in_the_same_pane()
    {
        Sta.Run(() =>
        {
            using var t = new TempDir();
            var a = t.File("a.txt", "first-file-token");
            var b = t.File("b.json", "{\"second\":\"second-file-token\"}");
            var (window, pane) = Host();
            try
            {
                Sta.WaitUntil(() => pane.IsReady, "WebView2 ready", 30000);
                pane.Show(a, delayMs: 0);
                WaitShows(pane, "first-file-token", "first preview");
                int before = pane.RecoveryCount;

                int unhandled = CountUnhandled(() =>
                {
                    KillTestBrowser(pane);
                    Sta.WaitUntil(() => pane.RecoveryCount > before, "recovery after browser crash", 20000);
                    // the file that was on screen comes back by itself
                    WaitShows(pane, "first-file-token", "current file shown again after recovery");
                    // and the next selection works (it used to throw "The WebView control is no longer valid…")
                    pane.Show(b, delayMs: 0);
                    WaitShows(pane, "second-file-token", "next preview after recovery");
                });
                Assert.Equal(0, unhandled);
                Assert.False(pane.IsFailed);
            }
            finally { pane.DisposeWeb(); window.Close(); }
        });
    }

    [Fact]
    public void Renderer_crash_shows_the_current_file_again()
    {
        Sta.Run(() =>
        {
            using var t = new TempDir();
            var a = t.File("a.md", "# renderer-token");
            var (window, pane) = Host();
            try
            {
                Sta.WaitUntil(() => pane.IsReady, "WebView2 ready", 30000);
                pane.Show(a, delayMs: 0);
                WaitShows(pane, "renderer-token", "first preview");
                int before = pane.RecoveryCount;
                var renderers = pane.Web.CoreWebView2.Environment.GetProcessInfos().Where(p => p.Kind == CoreWebView2ProcessKind.Renderer).ToList();
                Assert.NotEmpty(renderers);
                Assert.StartsWith(Path.GetTempPath(), PreviewPane.WebDataFolder, StringComparison.OrdinalIgnoreCase);
                foreach (var r in renderers) { try { Process.GetProcessById(r.ProcessId).Kill(); } catch (ArgumentException) { } }
                Sta.WaitUntil(() => pane.RecoveryCount > before, "recovery after renderer crash", 20000);
                WaitShows(pane, "renderer-token", "current file shown again without reselecting it");
            }
            finally { pane.DisposeWeb(); window.Close(); }
        });
    }

    [Fact]
    public void Hung_preview_page_is_recovered_by_the_watchdog()
    {
        Sta.Run(() =>
        {
            var saved = PreviewPane.NavigationTimeout;
            PreviewPane.NavigationTimeout = TimeSpan.FromSeconds(4);
            using var t = new TempDir();
            var a = t.File("a.txt", "before-hang-token");
            var b = t.File("b.txt", "after-hang-token");
            var (window, pane) = Host();
            try
            {
                Sta.WaitUntil(() => pane.IsReady, "WebView2 ready", 30000);
                pane.Show(a, delayMs: 0);
                WaitShows(pane, "before-hang-token", "first preview");
                int before = pane.RecoveryCount;
                _ = pane.Web.CoreWebView2.ExecuteScriptAsync("setTimeout(() => { for (;;) {} }, 0); 1"); // page stops responding
                Sta.DoEvents(300);
                pane.Show(b, delayMs: 0);
                Sta.WaitUntil(() => pane.RecoveryCount > before, "watchdog recovery of the hung page", 20000);
                WaitShows(pane, "after-hang-token", "the selected file after recovery");
            }
            finally { PreviewPane.NavigationTimeout = saved; pane.DisposeWeb(); window.Close(); }
        });
    }

    [Fact]
    public void Failed_engine_start_is_logged_shown_and_retried()
    {
        Sta.Run(() =>
        {
            using var t = new TempDir();
            var a = t.File("a.txt", "after-retry-token");
            var envField = typeof(PreviewPane).GetField("_envTask", BindingFlags.NonPublic | BindingFlags.Static)!;
            var create = PreviewPane.CreateEnvironment;
            // one transient environment failure (used to disable every preview in the process until restart)
            int attempts = 0;
            PreviewPane.CreateEnvironment = folder => ++attempts == 1
                ? Task.FromException<CoreWebView2Environment>(new System.Runtime.InteropServices.COMException("simulated transient start failure", unchecked((int)0x8007139F)))
                : create(folder);
            envField.SetValue(null, null);
            var (window, pane) = Host();
            var (window2, pane2) = ((Window?)null, (PreviewPane?)null);
            try
            {
                Sta.WaitUntil(() => pane.IsFailed, "start failure shown", 15000);
                Assert.Contains("simulated transient start failure", File.ReadAllText(Diagnostics.LogPath));

                // a new pane (Compare / Quick View) is no longer poisoned by the failed attempt
                (window2, pane2) = Host();
                Sta.WaitUntil(() => pane2.IsReady, "new pane starts", 30000);

                // selecting a file in the failed pane retries it
                pane.Show(a, delayMs: 0);
                WaitShows(pane, "after-retry-token", "preview after retry");
                Assert.False(pane.IsFailed);
            }
            finally
            {
                PreviewPane.CreateEnvironment = create;
                if (envField.GetValue(null) is Task { IsFaulted: true }) envField.SetValue(null, null);
                pane.DisposeWeb(); window.Close();
                pane2?.DisposeWeb(); window2?.Close();
            }
        });
    }

    [Fact]
    public void Repeated_crashes_stop_automatic_recovery_and_offer_retry()
    {
        Sta.Run(() =>
        {
            using var t = new TempDir();
            var a = t.File("a.txt", "bounded-token");
            var (window, pane) = Host();
            try
            {
                Sta.WaitUntil(() => pane.IsReady, "WebView2 ready", 30000);
                pane.Show(a, delayMs: 0);
                WaitShows(pane, "bounded-token", "first preview");
                for (int i = 0; i < PreviewPane.MaxRecoveries; i++)
                {
                    int before = pane.RecoveryCount;
                    KillTestBrowser(pane);
                    Sta.WaitUntil(() => pane.RecoveryCount > before, "recovery " + (i + 1), 20000);
                    WaitShows(pane, "bounded-token", "preview after recovery " + (i + 1));
                }
                KillTestBrowser(pane);
                Sta.WaitUntil(() => pane.IsFailed, "automatic recovery stops", 20000);
                Assert.Contains("automatic recovery stopped", File.ReadAllText(Diagnostics.LogPath));

                // Retry (the button on the failure message) starts again
                typeof(PreviewPane).GetMethod("RetryPreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pane, null);
                WaitShows(pane, "bounded-token", "preview after Retry");
                Assert.False(pane.IsFailed);
            }
            finally { pane.DisposeWeb(); window.Close(); }
        });
    }

    [Fact]
    public void Compare_pdf_panes_recover_after_browser_crash()
    {
        Sta.Run(() =>
        {
            using var temp = new TempDir();
            NativePdfCompareTests.Pdf(temp["left.pdf"], "Left recovery");
            NativePdfCompareTests.Pdf(temp["right.pdf"], "Right recovery");
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var window = new CompareWindow(owner, temp["left.pdf"], temp["right.pdf"]) { Width = 1400, Height = 900 };
            try
            {
                Sta.ShowOffscreen(window);
                bool Loaded(PreviewPane p, string path)
                {
                    try { return p.IsReady && p.Web.CoreWebView2.Source == PreviewUrls.FileUrl(path); }
                    catch (InvalidOperationException) { return false; }
                }
                Sta.WaitUntil(() => Loaded(window.LeftPane, temp["left.pdf"]) && Loaded(window.RightPane, temp["right.pdf"]), "both PDFs", 30000);
                int left = window.LeftPane.RecoveryCount, right = window.RightPane.RecoveryCount;
                KillTestBrowser(window.LeftPane); // one browser process serves both sides
                Sta.WaitUntil(() => window.LeftPane.RecoveryCount > left && window.RightPane.RecoveryCount > right, "both sides recover", 20000);
                Sta.WaitUntil(() => Loaded(window.LeftPane, temp["left.pdf"]) && Loaded(window.RightPane, temp["right.pdf"]), "both PDFs shown again", 30000);
                // native PDF scrolling/navigation reconnects to the new controls
                Sta.WaitUntil(() => window.PdfAccess?.MatchesHandles(window.LeftPane.Web.Handle, window.RightPane.Web.Handle) == true,
                    "PDF access attached to the recovered panes", 30000);
            }
            finally { window.Close(); owner.Close(); }
        });
    }
}
