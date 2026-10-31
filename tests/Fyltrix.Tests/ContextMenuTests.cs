using System.Runtime.InteropServices;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.Core.Templates;
using Fyltrix.UI;

namespace Fyltrix.Tests;

public class IdeIntegrationTests
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    /// <summary>How the started program will see its arguments (Windows' own parser).</summary>
    private static string[] Parse(string args)
    {
        var p = CommandLineToArgvW("prog.exe " + args, out int argc);
        try { return Enumerable.Range(1, argc - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!).ToArray(); }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Theory]
    [InlineData(@"C:\Projects\My App")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\dir\")]
    [InlineData(@"C:\a & b (1)\100% ^done! [x] {y} ;=,")]
    [InlineData(@"C:\Ünïcødé\日本語\emoji 😀")]
    [InlineData("C:\\odd\\quo\"te\\\\")]
    public void Folder_paths_reach_the_IDE_unchanged(string folder)
    {
        Assert.Equal(new[] { folder }, Parse(new IdeEntry { Arguments = "{path}" }.BuildArguments(folder)));
        Assert.Equal(new[] { folder }, Parse(new IdeEntry { Arguments = "\"{path}\"" }.BuildArguments(folder)));
        Assert.Equal(new[] { "--new-window", folder, "--name", Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder },
            Parse(new IdeEntry { Arguments = "--new-window {path} --name {name}" }.BuildArguments(folder)));
    }

    [Fact]
    public void Launches_the_program_directly_with_the_folder()
    {
        // robocopy stands in for an IDE: it copies the folder only if it received the exact path (spaces, &, %, !, ^, parentheses)
        using var t = new TempDir();
        var src = t.Dir("a & b (1) 100% !x ^y");
        File.WriteAllText(Path.Combine(src, "marker.txt"), "ok");
        var dest = t.Dir("dest");
        var ide = new IdeEntry { Name = "Copier", Program = "robocopy.exe", Arguments = "{path} " + IdeCatalog.Quote(dest) + " marker.txt" };
        Assert.Null(ide.Validate());
        ide.Open(src);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(Path.Combine(dest, "marker.txt")) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        Assert.Equal("ok", File.ReadAllText(Path.Combine(dest, "marker.txt")));
    }

    [Fact]
    public void Batch_file_launchers_refuse_folder_names_cmd_would_misread()
    {
        using var t = new TempDir();
        var cmd = t.File("ide.cmd", "@echo off\r\n");
        var ide = new IdeEntry { Name = "Shim", Program = cmd };
        var risky = t.Dir("R&D 100%");
        var plain = t.Dir("Plain folder");
        Assert.Contains(".exe", ide.Problem(risky));
        Assert.Throws<InvalidOperationException>(() => ide.Open(risky));
        Assert.Null(ide.Problem(plain));
        Assert.Contains("doesn't exist", ide.Problem(t["missing"]));
        Assert.Contains("wasn't found", new IdeEntry { Name = "Gone", Program = t["gone.exe"] }.Problem(plain));
    }

    [Fact]
    public void Validates_custom_entries()
    {
        Assert.Null(new IdeEntry { Name = "Notepad", Program = "notepad.exe" }.Validate());
        Assert.Contains("name", new IdeEntry { Program = "notepad.exe" }.Validate());
        Assert.Equal("The program wasn't found.", new IdeEntry { Name = "x", Program = @"C:\nope\ide.exe" }.Validate());
        Assert.Contains("{path}", new IdeEntry { Name = "x", Program = "notepad.exe", Arguments = "--new" }.Validate());
    }

    [Fact]
    public void Detects_installed_IDEs_from_their_install_folders()
    {
        using var t = new TempDir();
        var local = t.Dir("Local");
        var pf = t.Dir("PF");
        var pf86 = t.Dir("PF86");
        t.File(@"Local\Programs\Microsoft VS Code\Code.exe");
        t.File(@"Local\Programs\cursor\Cursor.exe");
        void JetBrains(string dir, string name, string code, string build, string exe)
        {
            t.File(dir + @"\bin\" + exe);
            t.File(dir + @"\product-info.json", $$"""
                { "name": "{{name}}", "version": "x", "buildNumber": "{{build}}", "productCode": "{{code}}",
                  "launch": [ { "os": "Linux", "launcherPath": "bin/idea.sh" }, { "os": "Windows", "arch": "aarch64", "launcherPath": "bin/arm.exe" },
                              { "os": "Windows", "arch": "amd64", "launcherPath": "bin/{{exe}}" } ] }
                """);
        }
        JetBrains(@"PF\JetBrains\IntelliJ IDEA 2025.2", "IntelliJ IDEA", "IU", "252.1.1", "idea64.exe");
        JetBrains(@"PF\JetBrains\IntelliJ IDEA 2026.1", "IntelliJ IDEA", "IU", "261.22158.277", "idea64.exe");
        JetBrains(@"Local\Programs\PyCharm", "PyCharm", "PY", "261.1", "pycharm64.exe");
        JetBrains(@"Local\JetBrains\Toolbox\apps\Rider\ch-0\261.5", "JetBrains Rider", "RD", "261.5", "rider64.exe");
        JetBrains(@"PF\Android\Android Studio", "Android Studio", "AI", "251.1", "studio64.exe");
        t.File(@"PF\JetBrains\Broken\product-info.json", "{ not json");
        t.File(@"PF\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe");
        t.File(@"PF86\Sublime Text\sublime_text.exe");

        var found = IdeCatalog.Detect(local, new[] { pf, pf86 }, "");
        string Exe(string id) => found.Single(f => f.Id == id).Program;
        Assert.Equal(t[@"Local\Programs\Microsoft VS Code\Code.exe"], Exe("vscode"));
        Assert.Equal(t[@"Local\Programs\cursor\Cursor.exe"], Exe("cursor"));
        Assert.Equal(t[@"PF\JetBrains\IntelliJ IDEA 2026.1\bin\idea64.exe"], Exe("jetbrains-iu")); // newest of two versions
        Assert.Equal("IntelliJ IDEA", found.Single(f => f.Id == "jetbrains-iu").Name);
        Assert.Equal(t[@"Local\Programs\PyCharm\bin\pycharm64.exe"], Exe("jetbrains-py"));
        Assert.Equal(t[@"Local\JetBrains\Toolbox\apps\Rider\ch-0\261.5\bin\rider64.exe"], Exe("jetbrains-rd"));
        Assert.Equal("Android Studio", found.Single(f => f.Id == "jetbrains-ai").Name);
        Assert.Equal("Visual Studio 2026 Community", found.Single(f => f.Program.EndsWith("devenv.exe")).Name);
        Assert.Equal(t[@"PF86\Sublime Text\sublime_text.exe"], Exe("sublime"));
        Assert.All(found, f => { Assert.False(f.IsCustom); Assert.True(f.Enabled); Assert.Equal("{path}", f.Arguments); });
        Assert.Equal(found.Count, found.Select(f => f.Program).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Finds_VS_Code_through_its_launcher_on_PATH()
    {
        using var t = new TempDir();
        t.File(@"Apps\VS Code\bin\code.cmd");
        t.File(@"Apps\VS Code\Code.exe");
        var found = IdeCatalog.Detect(t.Dir("Local"), new[] { t.Dir("PF") }, @"C:\nothing;" + t[@"Apps\VS Code\bin"]);
        Assert.Equal(t[@"Apps\VS Code\Code.exe"], found.Single(f => f.Id == "vscode").Program);
    }

    [Fact]
    public void Menu_shows_enabled_installed_IDEs_once()
    {
        using var t = new TempDir();
        var code = t.File(@"VS Code\Code.exe");
        var idea = t.File(@"IDEA\bin\idea64.exe");
        var detected = new[]
        {
            new IdeEntry { Id = "vscode", Name = "Visual Studio Code", Program = code },
            new IdeEntry { Id = "jetbrains-iu", Name = "IntelliJ IDEA", Program = idea },
        };

        // nothing saved yet: everything detected is shown
        Assert.Equal(new[] { "Visual Studio Code", "IntelliJ IDEA" }, IdeCatalog.MenuEntries(Array.Empty<IdeEntry>(), detected).Select(e => e.Name));

        var saved = new List<IdeEntry>
        {
            new() { Id = "vscode", Name = "Visual Studio Code", Program = code, Enabled = false },                     // switched off
            new() { Id = "custom-1", Name = "My IDEA", Program = idea, IsCustom = true },                             // same program as a detected one
            new() { Id = "custom-2", Name = "IDEA again", Program = idea, Arguments = "\"{path}\"", IsCustom = true }, // duplicate (same program + args)
            new() { Id = "custom-3", Name = "Uninstalled", Program = t["old\\ide.exe"], IsCustom = true },             // not installed
            new() { Id = "custom-4", Name = "Notepad", Program = "notepad.exe", Arguments = "{path}\\readme.txt", IsCustom = true },
        };
        var menu = IdeCatalog.MenuEntries(saved, detected);
        Assert.Equal(new[] { "My IDEA", "IDEA again", "Notepad" }, menu.Select(e => e.Name));

        // "{path}" and "\"{path}\"" mean the same command: the second is a duplicate after normalising
        saved[2].Arguments = "{path}";
        Assert.Equal(new[] { "My IDEA", "Notepad" }, IdeCatalog.MenuEntries(saved, detected).Select(e => e.Name));
    }

    [Fact]
    public void A_saved_detected_IDE_follows_an_upgrade_to_a_new_folder()
    {
        using var t = new TempDir();
        var newIdea = t.File(@"IntelliJ IDEA 2026.2\bin\idea64.exe");
        var saved = new[] { new IdeEntry { Id = "jetbrains-iu", Name = "IntelliJ IDEA", Program = t[@"IntelliJ IDEA 2026.1\bin\idea64.exe"], Enabled = false } };
        var merged = IdeCatalog.Merge(saved, new[] { new IdeEntry { Id = "jetbrains-iu", Name = "IntelliJ IDEA", Program = newIdea } });
        var e = Assert.Single(merged);
        Assert.Equal(newIdea, e.Program);
        Assert.False(e.Enabled); // the user's choice is kept
        Assert.NotSame(saved[0], e);
    }
}

[Collection("UI")]
public class ContextMenuUiTests
{
    private static bool IsIdeItem(MenuItem m) =>
        m.Header is string h && h.StartsWith("Open in ") && h is not ("Open in new tab" or "Open in File Explorer");

    private static IdeEntry Ide(string id, string name, string program) => new() { Id = id, Name = name, Program = program };

    [Fact]
    public void Sidebar_lists_Quick_access_then_Favourites_then_Recent()
    {
        using var t = new TempDir();
        var fav = t.Dir("Fav");
        var recent = t.Dir("Recent one");
        t.File("a.txt");
        Sta.Run(() =>
        {
            IdeCatalog.SetDetectedForTest(Array.Empty<IdeEntry>());
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => { s.Favourites.Add(fav); s.RecentFolders.Add(recent); });
            try
            {
                var sections = w.SidebarSections;
                Assert.Equal(new[] { "QUICK ACCESS", "FAVOURITES", "RECENT", "THIS PC" }, sections.Where(s => s != "TAGS"));
                Sta.Render(w, "sidebar-order");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Folder_context_menus_offer_the_configured_IDEs()
    {
        using var t = new TempDir();
        t.Dir("My Project");
        t.File("notes.txt");
        var exe = t.File(@"Tools\VS Code\Code.exe");
        var idea = t.File(@"Tools\IDEA\bin\idea64.exe");
        Sta.Run(() =>
        {
            IdeCatalog.SetDetectedForTest(new[] { Ide("vscode", "Visual Studio Code", exe) });
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                var folder = view.Items.Single(i => i.Name == "My Project");
                var file = view.Items.Single(i => i.Name == "notes.txt");

                // one IDE: a direct item, on a folder, on the empty area and on a sidebar place; never on a file
                MainWindowUiTests.Item(w.BuildContextMenu(view, new List<FileItem> { folder }), "Open in Visual Studio Code");
                MainWindowUiTests.Item(w.BuildContextMenu(view, new List<FileItem>()), "Open in Visual Studio Code");
                Assert.DoesNotContain(MainWindowUiTests.AllItems(w.BuildContextMenu(view, new List<FileItem> { file })), m => IsIdeItem(m));
                Assert.NotNull(w.IdeMenuItem(t.Path));

                // two IDEs: a submenu; a disabled one is hidden; the same program twice is listed once
                w.Settings.Ides = new List<IdeEntry>
                {
                    new() { Id = "custom-a", Name = "IntelliJ IDEA", Program = idea, IsCustom = true },
                    new() { Id = "custom-b", Name = "IDEA copy", Program = idea, IsCustom = true },
                };
                var menu = w.BuildContextMenu(view, new List<FileItem> { folder });
                var sub = MainWindowUiTests.Item(menu, "Open in IDE");
                Assert.Equal(new[] { "IntelliJ IDEA", "Visual Studio Code" }, MainWindowUiTests.AllItems(sub).Select(m => (string)m.Header));
                Assert.Single(MainWindowUiTests.AllItems(menu), m => (m.Header as string) == "Open in IDE");
                Sta.Render(w, "ide-menu");

                w.Settings.Ides.Add(new IdeEntry { Id = "vscode", Name = "Visual Studio Code", Program = exe, Enabled = false });
                Assert.Equal("Open in IntelliJ IDEA", (string)w.IdeMenuItem(t.Path)!.Header);

                // nothing set up: no item at all
                foreach (var e in w.Settings.Ides) e.Enabled = false;
                Assert.Null(w.IdeMenuItem(t.Path));
                Assert.DoesNotContain(MainWindowUiTests.AllItems(w.BuildContextMenu(view, new List<FileItem> { folder })), m => IsIdeItem(m));
            }
            finally { w.Close(); IdeCatalog.SetDetectedForTest(Array.Empty<IdeEntry>()); }
        });
    }

    [Fact]
    public void Settings_hide_New_menu_types_immediately_and_persist_them()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            IdeCatalog.SetDetectedForTest(Array.Empty<IdeEntry>());
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var view = w.View!;
                List<string> NewHeaders() => MainWindowUiTests.AllItems(MainWindowUiTests.Item(w.BuildContextMenu(view, new List<FileItem>()), "New"))
                    .Select(m => m.Header as string ?? "").ToList();
                var before = NewHeaders();
                Assert.Contains("Markdown", before);
                Assert.Contains("Word document", before);
                Assert.False(File.Exists(FileTemplates.FilePath)); // defaults untouched until the user changes something

                var windowsTypes = new List<ShellNewEntry> { new(".zzfl", "Test type from Windows", null, null, true) };
                var s = new SettingsWindow(w, w.Settings, FileTemplates.Load(), windowsTypes);
                Assert.True(s.NewTypeChecks.Values.All(c => c.IsChecked == true)); // default: everything visible
                Assert.True(s.TrySave());
                Assert.False(File.Exists(FileTemplates.FilePath));                  // saving without changes doesn't write templates.json

                s = new SettingsWindow(w, w.Settings, FileTemplates.Load(), windowsTypes);
                s.SelectTab("Context menu");
                Sta.ShowOffscreen(s);
                Sta.DoEvents(200);
                Sta.Render(s, "settings-context-menu");
                s.NewTypeChecks["md"].IsChecked = false;
                s.NewTypeChecks["docx"].IsChecked = false;
                s.NewTypeChecks["win:.zzfl"].IsChecked = false;
                Assert.True(s.TrySave());
                s.Close();
                w.ReloadTemplates(); // what OpenSettings does after Save

                var after = NewHeaders();
                Assert.DoesNotContain("Markdown", after);
                Assert.DoesNotContain("Word document", after);
                Assert.Contains("Text document", after);
                Assert.Contains("Excel workbook", after);
                Assert.Contains("Folder", after);

                // persisted for the next start
                var reloaded = FileTemplates.Load();
                Assert.False(reloaded.Single(x => x.Id == "md").Enabled);
                Assert.True(reloaded.Single(x => x.Id == "txt").Enabled);
                Assert.Contains(".zzfl", AppSettings.Load().HiddenNewTypes);

                // the template itself still works (only visibility changed)
                var md = FileTemplates.Create(t.Path, reloaded.Single(x => x.Id == "md"), "kept.md");
                Assert.True(File.Exists(md));

                // ticking again brings it back
                s = new SettingsWindow(w, w.Settings, FileTemplates.Load(), windowsTypes);
                Assert.False(s.NewTypeChecks["md"].IsChecked);
                Assert.False(s.NewTypeChecks["win:.zzfl"].IsChecked);
                s.NewTypeChecks["md"].IsChecked = true;
                s.NewTypeChecks["win:.zzfl"].IsChecked = true;
                Assert.True(s.TrySave());
                w.ReloadTemplates();
                Assert.Contains("Markdown", NewHeaders());
                Assert.DoesNotContain(".zzfl", AppSettings.Load().HiddenNewTypes);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Settings_list_detected_IDEs_and_save_choices_and_custom_ones()
    {
        using var t = new TempDir();
        t.File("a.txt");
        var code = t.File(@"VS Code\Code.exe");
        var idea = t.File(@"IDEA\bin\idea64.exe");
        var custom = t.File(@"My Editor\editor.exe");
        Sta.Run(() =>
        {
            IdeCatalog.SetDetectedForTest(new[] { Ide("vscode", "Visual Studio Code", code), Ide("jetbrains-iu", "IntelliJ IDEA", idea) });
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var s = new SettingsWindow(w, w.Settings, FileTemplates.Load(), Array.Empty<ShellNewEntry>());
                Assert.Equal(new[] { "Visual Studio Code", "IntelliJ IDEA" }, s.IdeItems.Select(i => i.Name));
                s.IdeItems[0].Enabled = false;

                Assert.Contains("already", s.AddOrReplaceIde(new IdeEntry { Id = "custom-x", Name = "Dup", Program = idea, IsCustom = true }, null));
                Assert.Contains("{path}", s.AddOrReplaceIde(new IdeEntry { Id = "custom-x", Name = "Bad", Program = custom, Arguments = "-n", IsCustom = true }, null));
                Assert.Null(s.AddOrReplaceIde(new IdeEntry { Id = "custom-x", Name = "My Editor", Program = custom, Arguments = "--folder {path}", IsCustom = true }, null));
                s.SelectTab("Context menu");
                Sta.ShowOffscreen(s);
                Sta.DoEvents(200);
                Sta.Render(s, "settings-ides");
                Assert.True(s.TrySave());
                s.Close();

                var names = MainWindowUiTests.AllItems(MainWindowUiTests.Item(w.BuildContextMenu(w.View!, new List<FileItem>()), "Open in IDE")).Select(m => (string)m.Header).ToList();
                Assert.Equal(new[] { "IntelliJ IDEA", "My Editor" }, names);

                var saved = AppSettings.Load().Ides;
                Assert.False(saved.Single(i => i.Id == "vscode").Enabled);
                Assert.Equal("--folder {path}", saved.Single(i => i.Name == "My Editor").Arguments);
                Assert.True(saved.Single(i => i.Name == "My Editor").IsCustom);
            }
            finally { w.Close(); IdeCatalog.SetDetectedForTest(Array.Empty<IdeEntry>()); }
        });
    }

    [Fact]
    public void Main_window_class_carries_the_app_icon_for_the_taskbar()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                // the taskbar falls back to these when the window is too busy to answer WM_GETICON
                Assert.NotEqual(IntPtr.Zero, AppIcon.ClassIcon(w, small: false));
                Assert.NotEqual(IntPtr.Zero, AppIcon.ClassIcon(w, small: true));
            }
            finally { w.Close(); }
        });
    }
}
