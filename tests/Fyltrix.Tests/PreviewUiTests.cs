using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using Fyltrix.Core;
using Fyltrix.Preview;

namespace Fyltrix.Tests;

/// <summary>Loads files into a real PreviewPane (WebView2) off-screen and inspects what the viewer rendered.</summary>
[Collection("UI")]
public class PreviewUiTests
{
    internal sealed class Host : IDisposable
    {
        public Window Window { get; }
        public PreviewPane Pane { get; } = new();

        public Host()
        {
            PreviewPane.Settings ??= new AppSettings();
            Window = new Window { Width = 1000, Height = 760, Content = Pane };
            Sta.ShowOffscreen(Window);
            Sta.WaitUntil(() => Pane.IsReady, "WebView2 ready", 30000);
        }

        /// <summary>Shows a file and waits until the viewer replaced its "Loading…" placeholder.</summary>
        public string ShowAndWaitForText(string path, Func<string, bool>? until = null, int timeoutMs = 20000)
        {
            Pane.Show(path, delayMs: 0);
            string text = "";
            Sta.WaitUntil(() =>
            {
                text = Eval("(document.getElementById('toolbar')?.innerText||'') + '\\n' + (document.getElementById('root')?.innerText||document.body?.innerText||'')");
                return text.Length > 0 && !text.Contains("Loading…") && (until == null || until(text));
            }, "viewer content for " + Path.GetFileName(path), timeoutMs);
            return text;
        }

        public string Eval(string script)
        {
            var task = Pane.Web.CoreWebView2.ExecuteScriptAsync(script);
            Sta.WaitUntil(() => task.IsCompleted, "script");
            var json = task.Result;
            return json == "null" ? "" : JsonSerializer.Deserialize<string>(json) ?? "";
        }

        public string Capture(string name)
        {
            Directory.CreateDirectory(Sta.ShotsDir);
            var file = Path.Combine(Sta.ShotsDir, name + ".png");
            using var fs = File.Create(file);
            var task = Pane.Web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, fs);
            Sta.WaitUntil(() => task.IsCompleted, "capture");
            return file;
        }

        public void Dispose()
        {
            Pane.DisposeWeb();
            Window.Close();
        }
    }

    [Fact]
    public void Archive_preview_lists_contents()
    {
        using var t = new TempDir();
        t.File("p/Project/README.md", "# hi");
        t.File("p/Project/src/main.cs", "class X {}");
        ZipFile.CreateFromDirectory(t["p"], t["a.zip"]);
        Sta.Run(() =>
        {
            using var h = new Host();
            var text = h.ShowAndWaitForText(t["a.zip"], s => s.Contains("main.cs"));
            Assert.Contains("README.md", text);
            Assert.Contains("2 files", text);
            h.Capture("preview-archive");
        });
    }
}
