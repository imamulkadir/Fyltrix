using System.IO.Compression;
using System.Text;
using Fyltrix.Core;
using Fyltrix.Core.Templates;

namespace Fyltrix.Tests;

[Collection("UI")] // saved templates use AppSettings.Folder, shared with settings/UI tests
public class TemplateTests
{
    private static FileTemplate Get(string id) => FileTemplates.BuiltIns().Single(t => t.Id == id);

    [Fact]
    public void Builtins_cover_requested_types_with_unique_ids()
    {
        var all = FileTemplates.BuiltIns();
        var exts = all.Select(t => t.Extension).ToHashSet();
        foreach (var e in new[] { ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".ini", ".log", ".html", ".css", ".js", ".jsx", ".svg", ".py", ".cs", ".ps1", ".sh", ".sql", ".docx", ".xlsx", ".pptx" })
            Assert.Contains(e, exts);
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        Assert.All(all, t => Assert.Null(FileOps.ValidateName(t.DefaultFileName)));
    }

    [Fact]
    public void Text_templates_respect_newlines_and_bom()
    {
        var sh = FileTemplates.Render(Get("sh"), "x.sh");
        Assert.DoesNotContain((byte)'\r', sh);
        Assert.StartsWith("#!/usr/bin/env bash\n", Encoding.UTF8.GetString(sh));

        var ps1 = FileTemplates.Render(Get("ps1"), "x.ps1");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, ps1.Take(3));
        Assert.Contains("\r\n", Encoding.UTF8.GetString(ps1));

        var csv = FileTemplates.Render(Get("csv"), "x.csv");
        Assert.Equal(0xEF, csv[0]);
        var txt = FileTemplates.Render(Get("txt"), "x.txt");
        Assert.Empty(txt);
    }

    [Theory]
    [InlineData("my-button", "MyButton")]
    [InlineData("user profile card", "UserProfileCard")]
    [InlineData("2fa", "_2fa")]
    [InlineData("---", "Component")]
    public void Identifier_from_file_name(string stem, string expected) => Assert.Equal(expected, FileTemplates.ToIdentifier(stem));

    [Fact]
    public void Placeholders_use_the_chosen_name()
    {
        var jsx = Get("jsx");
        Assert.True(jsx.NeedsNameFirst);
        var text = Encoding.UTF8.GetString(FileTemplates.Render(jsx, "nav-bar.jsx"));
        Assert.Contains("export default function NavBar()", text);
        Assert.False(Get("md").NeedsNameFirst);
    }

    [Fact]
    public void Create_never_overwrites()
    {
        using var t = new TempDir();
        var md = Get("md");
        var a = FileTemplates.Create(t.Path, md);
        var b = FileTemplates.Create(t.Path, md);
        Assert.Equal("New Markdown.md", Path.GetFileName(a));
        Assert.Equal("New Markdown (2).md", Path.GetFileName(b));
        File.WriteAllText(t["keep.md"], "precious");
        var c = FileTemplates.Create(t.Path, md, "keep.md");
        Assert.Equal("precious", File.ReadAllText(t["keep.md"]));
        Assert.Equal("keep (2).md", Path.GetFileName(c));
        Assert.Throws<IOException>(() => FileTemplates.Create(t.Path, md, "bad:name.md"));
    }

    [Theory]
    [InlineData("docx", "word/document.xml")]
    [InlineData("xlsx", "xl/workbook.xml")]
    [InlineData("pptx", "ppt/presentation.xml")]
    public void Office_templates_are_real_packages(string id, string mainPart)
    {
        using var t = new TempDir();
        var path = FileTemplates.Create(t.Path, Get(id));
        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        Assert.NotNull(zip.GetEntry(mainPart));
    }

    [Fact]
    public void Saved_state_merges_with_builtins()
    {
        using var t = new TempDir();
        var old = AppSettings.Folder;
        AppSettings.Folder = t.Path;
        try
        {
            var list = FileTemplates.Load();
            list.Single(x => x.Id == "log").Enabled = false;
            var md = list.Single(x => x.Id == "md");
            list.Remove(md);
            list.Insert(0, md);
            list.Add(new FileTemplate { Name = "Dockerfile stub", Extension = ".dockerfile", BaseName = "Dockerfile", Category = "Custom", Content = "FROM alpine\n" });
            list.Add(new FileTemplate { Name = "", Extension = "bad" }); // invalid: dropped on load
            FileTemplates.Save(list);

            var again = FileTemplates.Load();
            Assert.Equal("md", again[0].Id);
            Assert.False(again.Single(x => x.Id == "log").Enabled);
            var custom = again.Single(x => x.Name == "Dockerfile stub");
            Assert.False(custom.IsBuiltIn);
            Assert.Equal("FROM alpine\n", custom.Content);
            Assert.Equal(FileTemplates.BuiltIns().Count + 1, again.Count);
            // built-in content always comes from the app, never from the saved file
            Assert.Null(System.Text.Json.JsonDocument.Parse(File.ReadAllText(FileTemplates.FilePath)).RootElement[0].GetProperty("Content").GetString());

            FileTemplates.RestoreDefaults();
            Assert.Equal(FileTemplates.BuiltIns().Count, FileTemplates.Load().Count);
        }
        finally { AppSettings.Folder = old; }
    }

    [Theory]
    [InlineData("Ok", ".txt", null)]
    [InlineData("", ".txt", "Give the template a name.")]
    [InlineData("X", "txt", "The extension")]
    [InlineData("X", ".a b", "The extension")]
    public void Custom_template_validation(string name, string ext, string? expectedStart)
    {
        var err = FileTemplates.ValidateCustom(new FileTemplate { Name = name, Extension = ext });
        if (expectedStart == null) Assert.Null(err);
        else Assert.StartsWith(expectedStart, err);
    }

    [Fact]
    public void ShellNew_discovery_skips_known_types_and_never_throws()
    {
        var found = FileTemplates.DiscoverShellNew(FileTemplates.BuiltIns().Select(t => t.Extension));
        Assert.DoesNotContain(found, e => e.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, e => e.Extension.Equals(".docx", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(found.Count, found.Select(e => e.Extension.ToLowerInvariant()).Distinct().Count());
        Assert.All(found, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
    }
}

[Collection("UI")]
public class TemplateUiTests
{
    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public void Office_templates_open_in_the_installed_Office_preview_handler(string id)
    {
        using var t = new TempDir();
        var path = FileTemplates.Create(t.Path, FileTemplates.BuiltIns().Single(x => x.Id == id));
        if (Fyltrix.Preview.ShellPreviewHost.FindHandler(path) == null) return; // no Office previewer on this machine
        Sta.Run(() =>
        {
            using var host = new Fyltrix.Preview.ShellPreviewHost { Width = 800, Height = 600 };
            var form = new System.Windows.Forms.Form { Width = 820, Height = 640, ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Left = -32000, Top = -32000 };
            form.Controls.Add(host);
            form.Show();
            try
            {
                var err = host.Show(path);
                Assert.Null(err);
                Sta.DoEvents(500);
            }
            finally { host.Unload(); form.Close(); }
        });
    }

    [Fact]
    public void New_menu_creates_file_and_starts_rename()
    {
        using var t = new TempDir();
        t.File("existing.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                var menu = w.BuildContextMenu(view, new List<FileItem>());
                var newMenu = MainWindowUiTests.Item(menu, "New");
                Assert.True(newMenu.IsEnabled);
                var headers = MainWindowUiTests.AllItems(newMenu).Select(m => m.Header as string).ToList();
                Assert.Contains("Folder", headers);
                Assert.Contains("Word document", headers);
                Assert.Contains("Manage templates…", headers);

                MainWindowUiTests.Click(MainWindowUiTests.Item(menu, "Markdown"));
                Sta.WaitUntil(() => File.Exists(t["New Markdown.md"]), "markdown file");
                Sta.WaitUntil(() => view.Items.Any(i => i.Name == "New Markdown.md" && i.IsEditing), "inline rename");
                Assert.Equal("# Title\r\n\r\n", File.ReadAllText(t["New Markdown.md"]));
                Assert.StartsWith("Create 'New Markdown.md'", w.UndoStack.NextDescription);
                Sta.Render(w, "new-menu-rename");
            }
            finally { w.Close(); }
        });
    }
}
