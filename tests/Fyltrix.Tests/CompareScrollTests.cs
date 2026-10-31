using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fyltrix.Core;
using Fyltrix.Preview;
using Fyltrix.UI;
using PdfSharp.Pdf;

namespace Fyltrix.Tests;

/// <summary>Compare → Side by side: the "Sync scrolling" checkbox scrolls both files together (off-screen, real WebView2).</summary>
[Collection("UI")]
public class CompareScrollTests
{
    private static string Eval(PreviewPane pane, string script)
    {
        var task = pane.Web.CoreWebView2.ExecuteScriptAsync(script);
        Sta.WaitUntil(() => task.IsCompleted, "script");
        var json = task.Result;
        return json == "null" ? "" : JsonSerializer.Deserialize<JsonElement>(json).ToString();
    }
    private static void Wheel(PreviewPane pane, int dy)
    {
        var task = pane.Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mouseWheel\",\"x\":300,\"y\":300,\"deltaX\":0,\"deltaY\":{dy}}}");
        Sta.WaitUntil(() => task.IsCompleted, "wheel");
    }
    private static void Docx(string path, string tag)
    {
        var body = new StringBuilder();
        for (int i = 1; i <= 300; i++) body.Append($"<w:p><w:r><w:t>{tag} paragraph {i}</w:t></w:r></w:p>");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string n, string s) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(s); }
        Put("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Put("_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Put("word/document.xml", "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>");
    }
    private static void Xlsx(string path, string tag)
    {
        var rows = new StringBuilder();
        for (int i = 1; i <= 300; i++) rows.Append($"<row r=\"{i}\"><c r=\"A{i}\" t=\"inlineStr\"><is><t>{tag} row {i}</t></is></c><c r=\"B{i}\"><v>{i}</v></c></row>");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string n, string s) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(s); }
        Put("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Put("_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Put("xl/workbook.xml", "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Put("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        Put("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + rows + "</sheetData></worksheet>");
    }
    private static void WaitForText(PreviewPane pane, string text) =>
        Sta.WaitUntil(() => pane.IsReady && Eval(pane, "String(document.body?.innerText || '')").Contains(text), "content " + text, 20000);

    private static int Top(PreviewPane pane, string scroller) => (int)double.Parse(Eval(pane, $"String({scroller}.scrollTop)"), System.Globalization.CultureInfo.InvariantCulture);

    private static void Check(CompareWindow w, string scroller)
    {
        // left drives right
        Eval(w.LeftPane, $"{scroller}.scrollTop = 900; ''");
        Sta.WaitUntil(() => Top(w.RightPane, scroller) == 900, "right follows left");
        // right drives left
        Eval(w.RightPane, $"{scroller}.scrollTop = 400; ''");
        Sta.WaitUntil(() => Top(w.LeftPane, scroller) == 400, "left follows right");
        Assert.Equal(400, Top(w.RightPane, scroller)); // no bounce back

        // off: the sides scroll on their own
        w.SyncScrollCheck.IsChecked = false;
        Eval(w.LeftPane, $"{scroller}.scrollTop = 1500; ''");
        Sta.DoEvents(700);
        Assert.Equal(1500, Top(w.LeftPane, scroller));
        Assert.Equal(400, Top(w.RightPane, scroller));

        // on again: the right side lines up with the left straight away
        w.SyncScrollCheck.IsChecked = true;
        Sta.WaitUntil(() => Top(w.RightPane, scroller) == 1500, "right lines up when switched back on");
    }

    [Fact]
    public void Side_by_side_scrolls_together_and_can_be_switched_off()
    {
        using var t = new TempDir();
        string Lines(string tag) => string.Join("\n\n", Enumerable.Range(1, 300).Select(i => $"{tag} paragraph {i}"));
        t.File("a.md", Lines("Left"));
        t.File("b.md", Lines("Right"));
        string Page(string tag) => "<html><body>" + string.Concat(Enumerable.Range(1, 300).Select(i => $"<p>{tag} {i}</p>")) + "</body></html>";
        t.File("a.html", Page("Left"));
        t.File("b.html", Page("Right"));
        Sta.Run(() =>
        {
            PreviewPane.Settings ??= new AppSettings();
            PreviewPane.Settings.HtmlScripts = false; // the sync must work without the page's own scripts
            var owner = new System.Windows.Window();
            Sta.ShowOffscreen(owner);
            var w = new CompareWindow(owner, t["a.md"], t["b.md"]);
            Sta.ShowOffscreen(w);
            try
            {
                Assert.True(w.SyncScrolling); // on by default
                w.SetMode(false); // two text files open as a text diff; switch to side by side
                Assert.True(w.SyncScrollCheck.IsEnabled);
                WaitForText(w.LeftPane, "Left paragraph 300");
                WaitForText(w.RightPane, "Right paragraph 300");
                Check(w, "document.getElementById('root')"); // the viewer scrolls inside #root
                Sta.Render(w, "compare-sync-scroll");

                w.SetMode(true);
                Assert.False(w.SyncScrollCheck.IsEnabled); // the diff is one table; it always scrolls together
                Assert.True(w.SyncScrolling);
                w.Close();

                w = new CompareWindow(owner, t["a.html"], t["b.html"]);
                Sta.ShowOffscreen(w);
                w.SetMode(false);
                WaitForText(w.LeftPane, "Left 300");
                WaitForText(w.RightPane, "Right 300");
                Check(w, "document.scrollingElement"); // a web page (scripts off) scrolls the document
            }
            finally { w.Close(); owner.Close(); }
        });
    }

    /// <summary>Real mouse-wheel input (sent inside WebView2, not on the desktop) on every kind of file Compare draws in a page.</summary>
    [Theory]
    [InlineData("txt")]
    [InlineData("md")]
    [InlineData("cs")]
    [InlineData("json")]
    [InlineData("csv")]
    [InlineData("html")]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    public void Wheel_on_either_side_scrolls_both(string ext)
    {
        using var t = new TempDir();
        foreach (var (s, tag) in new[] { ("a", "Left"), ("b", "Right") })
        {
            var f = t[s + "." + ext];
            switch (ext)
            {
                case "md": t.File(s + ".md", string.Join("\n\n", Enumerable.Range(1, 300).Select(i => $"{tag} line {i}"))); break;
                case "json": t.File(s + ".json", "[" + string.Join(",", Enumerable.Range(1, 300).Select(i => $"\"{tag} {i}\"")) + "]"); break;
                case "csv": t.File(s + ".csv", "h1,h2\n" + string.Join("\n", Enumerable.Range(1, 300).Select(i => $"{tag},{i}"))); break;
                case "html": t.File(s + ".html", "<html><body>" + string.Concat(Enumerable.Range(1, 300).Select(i => $"<p>{tag} {i}</p>")) + "</body></html>"); break;
                case "docx": Docx(f, tag); break;
                case "xlsx": Xlsx(f, tag); break;
                case "pdf": using (var doc = new PdfDocument()) { for (int i = 0; i < 20; i++) doc.AddPage(); doc.Save(f); } break;
                default: t.File(s + "." + ext, string.Join("\n", Enumerable.Range(1, 300).Select(i => $"// {tag} line {i}"))); break;
            }
        }
        Sta.Run(() =>
        {
            PreviewPane.Settings ??= new AppSettings();
            PreviewPane.Settings.HtmlScripts = false;
            var owner = new System.Windows.Window();
            Sta.ShowOffscreen(owner);
            var w = new CompareWindow(owner, t["a." + ext], t["b." + ext]);
            Sta.ShowOffscreen(w);
            try
            {
                w.SetMode(false);
                Sta.WaitUntil(() => w.LeftPane.IsReady && w.RightPane.IsReady, "WebView2 ready", 30000);
                string Pos(PreviewPane p) => Eval(p, "window.__flPos ? __flPos() : ''");
                // wait until both pages are drawn and scrollable
                Sta.WaitUntil(() => Eval(w.LeftPane, "String(!!window.__flPos && (() => { const d = document.scrollingElement, r = document.getElementById('root'); return d.scrollHeight > d.clientHeight + 100 || (r && r.scrollHeight > r.clientHeight + 100); })())") == "true"
                                 && Eval(w.RightPane, "String(!!window.__flPos && (() => { const d = document.scrollingElement, r = document.getElementById('root'); return d.scrollHeight > d.clientHeight + 100 || (r && r.scrollHeight > r.clientHeight + 100); })())") == "true", "both sides scrollable", 30000);

                for (int i = 0; i < 3; i++) { Wheel(w.LeftPane, 200); Sta.DoEvents(100); }
                Sta.WaitUntil(() => Pos(w.LeftPane) == "0,600", "left scrolled by the wheel");
                Sta.WaitUntil(() => Pos(w.RightPane) == "0,600", "right follows the left wheel");

                Wheel(w.RightPane, -200);
                Sta.WaitUntil(() => Pos(w.RightPane) == "0,400", "right scrolled by the wheel");
                Sta.WaitUntil(() => Pos(w.LeftPane) == "0,400", "left follows the right wheel");

                w.SyncScrollCheck.IsChecked = false;
                Wheel(w.LeftPane, 200);
                Sta.WaitUntil(() => Pos(w.LeftPane) == "0,600", "left scrolled alone");
                Sta.DoEvents(600);
                Assert.Equal("0,400", Pos(w.RightPane));
            }
            finally { w.Close(); owner.Close(); }
        });
    }
}
