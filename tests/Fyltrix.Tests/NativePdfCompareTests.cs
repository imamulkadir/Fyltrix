using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Fyltrix.Core;
using Fyltrix.Preview;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class NativePdfCompareTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public NativePdfCompareTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    internal static void Pdf(string path, string text, int pages = 3)
    {
        var objects = new List<string> { "", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<string>();
        for (int i = 0; i < pages; i++)
        {
            string stream = $"BT /F1 18 Tf 60 700 Td ({text} page {i+1}) Tj ET";
            int content = objects.Count + 1;
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");
            kids.Add($"{objects.Count+1} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {content} 0 R >>");
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R >>";
        objects[1] = $"<< /Type /Pages /Count {pages} /Kids [{string.Join(" ",kids)}] >>";
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i+1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }

    private static string Eval(PreviewPane pane, string script)
    {
        var task = pane.Web.CoreWebView2.ExecuteScriptAsync(script);
        Sta.WaitUntil(() => task.IsCompleted, "native PDF host script");
        return JsonSerializer.Deserialize<JsonElement>(task.GetAwaiter().GetResult()).ToString();
    }

    private static void Ready(PreviewPane pane, string path)
    {
        Sta.WaitUntil(() => pane.IsReady && pane.Web.CoreWebView2.Source == PreviewUrls.FileUrl(path), "native PDF URL", 30000);
        Sta.WaitUntil(() => Eval(pane, "document.contentType") == "application/pdf", "native PDF MIME document", 30000);
    }

    private static string Protocol(PreviewPane pane, string method, string parameters = "{}")
    {
        var task = pane.Web.CoreWebView2.CallDevToolsProtocolMethodAsync(method,parameters);
        Sta.WaitUntil(() => task.IsCompleted,"PDF browser protocol");
        return task.GetAwaiter().GetResult();
    }

    private static string NativeSession(PreviewPane pane, string title)
    {
        string? targetId = null;
        Sta.WaitUntil(() =>
        {
            using var targets = JsonDocument.Parse(Protocol(pane,"Target.getTargets"));
            targetId = targets.RootElement.GetProperty("targetInfos").EnumerateArray()
                .Where(t => t.GetProperty("type").GetString() == "webview" && t.GetProperty("title").GetString() == title)
                .Select(t => t.GetProperty("targetId").GetString()).FirstOrDefault();
            return targetId != null;
        },"native PDF plugin target",30000);
        using var attached = JsonDocument.Parse(Protocol(pane,"Target.attachToTarget",JsonSerializer.Serialize(new { targetId, flatten = true })));
        return attached.RootElement.GetProperty("sessionId").GetString()!;
    }

    private static string NativeEval(PreviewPane pane, string session, string expression)
    {
        var task = pane.Web.CoreWebView2.CallDevToolsProtocolMethodForSessionAsync(session,"Runtime.evaluate",JsonSerializer.Serialize(new { expression, returnByValue = true }));
        Sta.WaitUntil(() => task.IsCompleted,"native PDF control script");
        using var result = JsonDocument.Parse(task.GetAwaiter().GetResult());
        Assert.False(result.RootElement.TryGetProperty("exceptionDetails",out _),result.RootElement.ToString());
        return result.RootElement.GetProperty("result").TryGetProperty("value",out var value) ? value.ToString() : "";
    }

    [Fact]
    public void Both_panes_use_original_native_pdf_documents_with_independent_controllers()
    {
        using var temp = new TempDir();
        Pdf(temp["left.pdf"], "Selectable left text");
        Pdf(temp["right.pdf"], "Selectable right text", 20);
        var leftHash = SHA256.HashData(File.ReadAllBytes(temp["left.pdf"]));
        var rightHash = SHA256.HashData(File.ReadAllBytes(temp["right.pdf"]));
        Sta.Run(() =>
        {
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var window = new CompareWindow(owner,temp["left.pdf"],temp["right.pdf"]) { Width = 1400, Height = 900 };
            try
            {
                Sta.ShowOffscreen(window);
                Ready(window.LeftPane,temp["left.pdf"]);
                Ready(window.RightPane,temp["right.pdf"]);
                Sta.WaitUntil(() => window.SyncScrollCheck.IsEnabled,"native PDF accessibility synchronization",30000);
                Assert.True(window.SyncScrolling);
                Assert.Contains("relative", window.SyncScrollCheck.ToolTip.ToString());
                Assert.False(window.LeftPane.ReportsScroll);
                Assert.False(window.RightPane.ReportsScroll);
                Assert.NotSame(window.LeftPane.Web.CoreWebView2,window.RightPane.Web.CoreWebView2);
                Assert.True(window.LeftPane.Web.CoreWebView2.Settings.IsZoomControlEnabled);
                Assert.True(window.RightPane.Web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled);
                Assert.Equal(Microsoft.Web.WebView2.Core.CoreWebView2PdfToolbarItems.None, window.LeftPane.Web.CoreWebView2.Settings.HiddenPdfToolbarItems);
                Assert.Equal("false",Eval(window.LeftPane,"String(!!document.querySelector('.pdf-pages'))"));
                _output.WriteLine("Left PDF DOM: " + Eval(window.LeftPane,"document.documentElement.outerHTML"));
                string leftSession = NativeSession(window.LeftPane,"left.pdf"), rightSession = NativeSession(window.RightPane,"right.pdf");
                Sta.WaitUntil(() => NativeEval(window.LeftPane,leftSession,"String(!!document.getElementById('pageselector') && !!document.getElementById('zoom-in'))") == "true", "native PDF controls",30000);
                Sta.WaitUntil(() => NativeEval(window.RightPane,rightSession,"String(!!document.getElementById('pageselector'))") == "true", "right PDF controls",30000);
                Assert.Equal("1",NativeEval(window.LeftPane,leftSession,"document.getElementById('pageselector').value"));
                Assert.Equal("1",NativeEval(window.RightPane,rightSession,"document.getElementById('pageselector').value"));
                var zoom = window.RightPane.Web.ZoomFactor;
                window.LeftPane.Web.ZoomFactor = 1.25;
                Assert.Equal(zoom,window.RightPane.Web.ZoomFactor);
                Assert.Equal(PreviewUrls.FileUrl(temp["right.pdf"]),window.RightPane.Web.CoreWebView2.Source);
                using var image = File.Create(Path.Combine(Sta.ShotsDir,"compare-native-pdf-left.png"));
                var capture = window.LeftPane.Web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,image);
                Sta.WaitUntil(() => capture.IsCompleted,"native PDF capture");
                capture.GetAwaiter().GetResult();
                var swap = VisualTree.FindDescendants<Button>(window).Single(button => button.ToolTip?.ToString() == "Swap left and right");
                swap.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Ready(window.LeftPane,temp["right.pdf"]);
                Ready(window.RightPane,temp["left.pdf"]);
                Sta.WaitUntil(() => window.SyncScrollCheck.IsEnabled,"native PDF access after swapping",30000);
            }
            finally { window.Close(); owner.Close(); }
        });
        Assert.Equal(leftHash,SHA256.HashData(File.ReadAllBytes(temp["left.pdf"])));
        Assert.Equal(rightHash,SHA256.HashData(File.ReadAllBytes(temp["right.pdf"])));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Compare_has_no_native_owner_or_topmost_style_and_parent_close_cleans_it_up(bool maximized)
    {
        using var temp = new TempDir();
        temp.File("a.txt","left"); temp.File("b.txt","right");
        Sta.Run(() =>
        {
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var window = new CompareWindow(owner,temp["a.txt"],temp["b.txt"]);
            try
            {
                Assert.True(window.ShowInTaskbar);
                // Create the independent HWND without showing it. ShowOffscreen deliberately sets
                // ShowInTaskbar=false, which makes WPF assign a hidden framework owner instead.
                new WindowInteropHelper(window).EnsureHandle();
                Assert.Null(window.Owner);
                Assert.False(window.Topmost);
                var hwnd = new WindowInteropHelper(window).Handle;
                Assert.Equal(IntPtr.Zero,GetWindow(hwnd,4)); // GW_OWNER
                Assert.Equal(0,GetWindowLongPtr(hwnd,-20).ToInt64() & 8); // WS_EX_TOPMOST
                // Changing native WS_MAXIMIZE on this offscreen window never activates it.
                var style = GetWindowLongPtr(hwnd,-16).ToInt64();
                if (maximized) SetWindowLongPtr(hwnd,-16,new IntPtr(style | 0x01000000));
                Assert.Equal(IntPtr.Zero,GetWindow(hwnd,4));
                bool closed = false;
                window.Closed += (_,_) => closed = true;
                owner.Close();
                Assert.True(closed);
                Assert.Throws<ObjectDisposedException>(() => window.LeftPane.Web.CoreWebView2);
            }
            finally { if (window.IsVisible) window.Close(); owner.Close(); }
        });
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);

    [Theory]
    [InlineData("encrypted")] [InlineData("corrupt")] [InlineData("many-pages")]
    public void Difficult_pdf_inputs_keep_native_handling_without_modifying_sources(string kind)
    {
        using var temp = new TempDir();
        Pdf(temp["left.pdf"],"Known good left");
        Pdf(temp["right.pdf"],"Right document",kind == "many-pages" ? 500 : 3);
        var right = temp["right.pdf"];
        if (kind == "encrypted")
        {
            using var document = PdfSharp.Pdf.IO.PdfReader.Open(right,PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
            document.SecuritySettings.UserPassword = "fixture-only";
            right = temp["locked.pdf"];
            document.Save(right);
        }
        else if (kind == "corrupt") File.WriteAllText(right,"%PDF-1.7\nBroken test document\n");
        var before = SHA256.HashData(File.ReadAllBytes(right));
        Sta.Run(() =>
        {
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var compare = new CompareWindow(owner,temp["left.pdf"],right) { Width = 1400, Height = 900 };
            try
            {
                Sta.ShowOffscreen(compare);
                Ready(compare.LeftPane,temp["left.pdf"]);
                Ready(compare.RightPane,right);
                compare.SyncScrolling = false;
                Assert.False(compare.SyncScrolling);
                Assert.Equal("false",Eval(compare.RightPane,"String(!!document.querySelector('.pdf-pages'))"));
                _output.WriteLine($"{kind}: handled by native PDF host, {new FileInfo(right).Length} bytes. Password/error visual acceptance is separate from navigation/MIME verification.");
            }
            finally {compare.Close();owner.Close();}
        });
        Assert.Equal(before,SHA256.HashData(File.ReadAllBytes(right)));
    }
}
