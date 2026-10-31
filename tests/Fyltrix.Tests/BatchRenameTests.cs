using Fyltrix.Core.Tools;

namespace Fyltrix.Tests;

public class BatchRenameTests
{
    private static List<RenamePlanItem> Plan(TempDir t, RenameRules r, params string[] names) =>
        BatchRename.Plan(names.Select(n => t[n]).ToList(), r);

    [Fact]
    public void Numbering_prefix_suffix_and_case()
    {
        using var t = new TempDir();
        foreach (var n in new[] { "IMG 01.JPG", "IMG 02.JPG", "IMG 03.JPG" }) t.File(n);
        var plan = Plan(t, new RenameRules
        {
            Template = "Holiday {n:000}", CounterStart = 5, CounterStep = 5, Prefix = "2024 ", Suffix = "_x",
            Case = CaseChange.Lower, LowercaseExtension = true,
        }, "IMG 01.JPG", "IMG 02.JPG", "IMG 03.JPG");
        Assert.Equal(new[] { "2024 holiday 005_x.jpg", "2024 holiday 010_x.jpg", "2024 holiday 015_x.jpg" }, plan.Select(p => p.NewName));
        Assert.All(plan, p => Assert.Equal(RenameStatus.Ok, p.Status));
    }

    [Fact]
    public void Find_replace_plain_and_regex()
    {
        using var t = new TempDir();
        t.File("report_final_FINAL.docx");
        var plain = Plan(t, new RenameRules { Find = "final", Replace = "v2" }, "report_final_FINAL.docx");
        Assert.Equal("report_v2_v2.docx", plain[0].NewName);
        var cased = Plan(t, new RenameRules { Find = "final", Replace = "v2", MatchCase = true }, "report_final_FINAL.docx");
        Assert.Equal("report_v2_FINAL.docx", cased[0].NewName);
        var rx = Plan(t, new RenameRules { Find = @"_(\w+)_(\w+)$", Replace = "-$2-$1", UseRegex = true, MatchCase = true }, "report_final_FINAL.docx");
        Assert.Equal("report-FINAL-final.docx", rx[0].NewName);
        var bad = Plan(t, new RenameRules { Find = "(", UseRegex = true }, "report_final_FINAL.docx");
        Assert.Equal(RenameStatus.Invalid, bad[0].Status);
        Assert.StartsWith("Invalid regular expression", bad[0].Message);
    }

    [Fact]
    public void Remove_characters_spaces_and_extension()
    {
        using var t = new TempDir();
        t.File("My (old) file!.txt");
        var p = Plan(t, new RenameRules { RemoveCharacters = "()!", SpaceReplacement = "-", NewExtension = "md" }, "My (old) file!.txt");
        Assert.Equal("My-old-file.md", p[0].NewName);
        var noExt = Plan(t, new RenameRules { NewExtension = "" }, "My (old) file!.txt");
        Assert.Equal("My (old) file!", noExt[0].NewName);
    }

    [Fact]
    public void Date_tokens_use_modified_time()
    {
        using var t = new TempDir();
        var f = t.File("a.txt");
        File.SetLastWriteTime(f, new DateTime(2021, 3, 4, 5, 6, 7));
        var p = Plan(t, new RenameRules { Template = "{date:yyyyMMdd-HHmm} {name}" }, "a.txt");
        Assert.Equal("20210304-0506 a.txt", p[0].NewName);
    }

    [Fact]
    public void Conflicts_are_detected_before_anything_changes()
    {
        using var t = new TempDir();
        t.File("a.txt"); t.File("b.txt"); t.File("taken.txt", "keep");
        var dupes = Plan(t, new RenameRules { Template = "same" }, "a.txt", "b.txt");
        Assert.All(dupes, p => Assert.Equal(RenameStatus.Conflict, p.Status));
        var existing = Plan(t, new RenameRules { Template = "taken" }, "a.txt");
        Assert.Equal(RenameStatus.Conflict, existing[0].Status);
        var invalid = Plan(t, new RenameRules { Template = "bad:name" }, "a.txt");
        Assert.Equal(RenameStatus.Invalid, invalid[0].Status);
        var unchanged = Plan(t, new RenameRules(), "a.txt");
        Assert.Equal(RenameStatus.Unchanged, unchanged[0].Status);
        BatchRename.Execute(dupes.Concat(existing).Concat(invalid));
        Assert.True(File.Exists(t["a.txt"]) && File.Exists(t["b.txt"]));
        Assert.Equal("keep", File.ReadAllText(t["taken.txt"]));
    }

    [Fact]
    public void Swaps_and_case_only_renames_work_and_undo()
    {
        using var t = new TempDir();
        t.File("a.txt", "A"); t.File("b.txt", "B"); t.File("lower.txt", "L");
        var plan = new List<RenamePlanItem>();
        plan.AddRange(BatchRename.Plan(new[] { t["a.txt"] }, new RenameRules { Template = "b" }));
        plan.AddRange(BatchRename.Plan(new[] { t["b.txt"] }, new RenameRules { Template = "a" }));
        // planned separately, so check the combined batch is allowed: a→b and b→a are both sources
        var combined = BatchRename.Plan(new[] { t["a.txt"], t["b.txt"], t["lower.txt"] }, new RenameRules { Find = "a", Replace = "B", Case = CaseChange.Upper });
        Assert.Equal(new[] { "B.txt", "B.txt", "LOWER.txt" }, combined.Select(p => p.NewName)); // a→B collides with b→B
        Assert.Equal(RenameStatus.Conflict, combined[0].Status);

        var swap = new[]
        {
            new RenamePlanItem(t["a.txt"], "b.txt", RenameStatus.Ok, null),
            new RenamePlanItem(t["b.txt"], "a.txt", RenameStatus.Ok, null),
            new RenamePlanItem(t["lower.txt"], "LOWER.txt", RenameStatus.Ok, null),
        };
        var res = BatchRename.Execute(swap);
        Assert.Empty(res.Errors);
        Assert.Equal("B", File.ReadAllText(t["a.txt"]));
        Assert.Equal("A", File.ReadAllText(t["b.txt"]));
        Assert.Contains("LOWER.txt", Directory.GetFiles(t.Path).Select(Path.GetFileName));

        var undoErrors = BatchRename.Revert(res.Renamed);
        Assert.Empty(undoErrors);
        Assert.Equal("A", File.ReadAllText(t["a.txt"]));
        Assert.Contains("lower.txt", Directory.GetFiles(t.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Failure_midway_rolls_everything_back()
    {
        using var t = new TempDir();
        t.File("one.txt", "1"); t.File("two.txt", "2");
        using var locked = new FileStream(t["two.txt"], FileMode.Open, FileAccess.Read, FileShare.None);
        var res = BatchRename.Execute(new[]
        {
            new RenamePlanItem(t["one.txt"], "uno.txt", RenameStatus.Ok, null),
            new RenamePlanItem(t["two.txt"], "dos.txt", RenameStatus.Ok, null),
        });
        Assert.True(res.RolledBack);
        Assert.NotEmpty(res.Errors);
        Assert.True(File.Exists(t["one.txt"]));
        Assert.False(File.Exists(t["uno.txt"]));
        Assert.Empty(Directory.GetFiles(t.Path, ".fl-rename-*"));
    }

    [Fact]
    public void Name_held_by_a_selected_item_that_cannot_move_is_a_conflict()
    {
        using var t = new TempDir();
        t.File("a.txt"); t.File("b.txt");
        // a → b, but b → "b" again (unchanged): b stays where it is, so a can't take its name
        var p = BatchRename.Plan(new[] { t["a.txt"], t["b.txt"] }, new RenameRules { Find = "a", Replace = "b" });
        Assert.Equal(RenameStatus.Conflict, p[0].Status);
        Assert.Equal(RenameStatus.Unchanged, p[1].Status);
    }

    [Fact]
    public void Folders_can_be_excluded()
    {
        using var t = new TempDir();
        t.Dir("dir"); t.File("f.txt");
        var p = BatchRename.Plan(new[] { t["dir"], t["f.txt"] }, new RenameRules { Prefix = "x-", ApplyToFolders = false });
        Assert.Equal(RenameStatus.Unchanged, p[0].Status);
        Assert.Equal("x-f.txt", p[1].NewName);
    }
}
