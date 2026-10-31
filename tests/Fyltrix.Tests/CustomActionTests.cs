using Fyltrix.Core;

namespace Fyltrix.Tests;

public class CustomActionTests
{
    [Fact]
    public void Builds_quoted_arguments_per_item_or_for_all()
    {
        var per = new CustomAction { Arguments = "--open \"{path}\" --in {folder} --title {name}" };
        var a = per.BuildArguments(new[] { @"C:\x y\a.txt", @"C:\z\b.txt" });
        Assert.Equal(new[]
        {
            "--open \"C:\\x y\\a.txt\" --in \"C:\\x y\" --title \"a.txt\"",
            "--open \"C:\\z\\b.txt\" --in \"C:\\z\" --title \"b.txt\"",
        }, a);
        var all = new CustomAction { Arguments = "-diff {paths}" }.BuildArguments(new[] { @"C:\a.txt", @"C:\b c.txt" });
        Assert.Equal(new[] { "-diff \"C:\\a.txt\" \"C:\\b c.txt\"" }, all);
        // a folder path with a trailing backslash would escape the closing quote; it is trimmed
        Assert.Equal(new[] { "\"C:\\dir\"" }, new CustomAction().BuildArguments(new[] { @"C:\dir\" }));
    }

    [Fact]
    public void Applies_by_type_and_validates()
    {
        var py = new CustomAction { Name = "Run", Program = "notepad.exe", Extensions = "py, txt" };
        Assert.True(py.AppliesTo(new[] { (@"C:\a.py", false), (@"C:\b.TXT", false) }));
        Assert.False(py.AppliesTo(new[] { (@"C:\a.py", false), (@"C:\c.md", false) }));
        Assert.False(py.AppliesTo(new[] { (@"C:\dir", true) }));
        Assert.Null(py.Validate()); // found on PATH
        Assert.Contains("interpreter", new CustomAction { Name = "x", Program = @"C:\scripts\tool.ps1" }.Validate());
        Assert.Equal("The program wasn't found.", new CustomAction { Name = "x", Program = @"C:\nope\nothing.exe" }.Validate());
    }

    [Fact]
    public void Runs_without_a_shell_so_file_names_cannot_inject_commands()
    {
        using var t = new TempDir();
        var tricky = t.File("a&b.txt", "payload");
        var dest = t.Dir("out");
        var action = new CustomAction
        {
            Name = "Copy", Program = "cmd.exe",
            Arguments = $"/c copy {{path}} \"{dest}\"",
        };
        action.Run(new[] { tricky });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(Path.Combine(dest, "a&b.txt")) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(dest, "a&b.txt")));
        Assert.False(File.Exists(Path.Combine(t.Path, "b.txt"))); // "&b.txt" was not run as a second command
    }
}

[Collection("UI")]
public class CustomActionUiTests
{
    [Fact]
    public void Actions_submenu_lists_matching_actions()
    {
        using var t = new TempDir();
        t.File("script.py", "print(1)");
        t.File("notes.md", "x");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path, s => s.CustomActions.Add(new CustomAction { Name = "Edit in Notepad", Program = "notepad.exe", Extensions = "py" }));
            try
            {
                var view = w.View!;
                var py = view.Items.Single(i => i.Name == "script.py");
                MainWindowUiTests.Item(w.BuildContextMenu(view, new List<FileItem> { py }), "Edit in Notepad");
                var md = view.Items.Single(i => i.Name == "notes.md");
                Assert.DoesNotContain(MainWindowUiTests.AllItems(w.BuildContextMenu(view, new List<FileItem> { md })), m => (m.Header as string) == "Actions");
            }
            finally { w.Close(); }
        });
    }
}
