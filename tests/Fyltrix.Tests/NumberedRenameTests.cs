using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.Tests;

public class NumberedRenameTests
{
    [Fact]
    public void Numbering_preserves_given_order_extensions_folder_names_and_undo()
    {
        using var temp = new TempDir();
        var sources = new[] { temp.File("z.JPG", "photo"), temp.File("a.png", "picture"), temp.Dir("folder.with.dots"), temp.File(".gitignore", "ignore") };
        temp.File("folder.with.dots/child.txt", "child");
        var plan = NumberedRename.Plan(sources, "Holiday");
        Assert.Equal(new[] { "Holiday (1).JPG", "Holiday (2).png", "Holiday (3)", "Holiday (4)" }, plan.Select(p => p.NewName));
        Assert.All(sources, source => Assert.True(FileOps.Exists(source))); // planning is read-only
        var result = BatchRename.Execute(plan);
        Assert.Empty(result.Errors); Assert.Equal(4, result.Renamed.Count);
        Assert.Equal("photo", File.ReadAllText(temp["Holiday (1).JPG"]));
        Assert.Equal("picture", File.ReadAllText(temp["Holiday (2).png"]));
        Assert.Equal("child", File.ReadAllText(temp["Holiday (3)/child.txt"]));
        Assert.Equal("ignore", File.ReadAllText(temp["Holiday (4)"]));
        Assert.Null(new BatchRenameUndo(result.Renamed).Undo(IntPtr.Zero));
        Assert.All(sources, source => Assert.True(FileOps.Exists(source)));
        Assert.Equal("child", File.ReadAllText(temp["folder.with.dots/child.txt"]));
    }

    [Fact]
    public void Existing_and_selected_names_are_reserved_case_insensitively()
    {
        using var temp = new TempDir();
        temp.File("HOLIDAY (1).JPG", "keep");
        var sources = new[] { temp.File("a.JPG", "a"), temp.File("Holiday (2).JPG", "b") };
        var plan = NumberedRename.Plan(sources, "Holiday");
        Assert.Equal(new[] { "Holiday (3).JPG", "Holiday (4).JPG" }, plan.Select(p => p.NewName));
        var result = BatchRename.Execute(plan);
        Assert.Empty(result.Errors);
        Assert.Equal("keep", File.ReadAllText(temp["HOLIDAY (1).JPG"]));
        Assert.Equal("a", File.ReadAllText(temp["Holiday (3).JPG"]));
        Assert.Equal("b", File.ReadAllText(temp["Holiday (4).JPG"]));
        Assert.Null(new BatchRenameUndo(result.Renamed).Undo(IntPtr.Zero));
        Assert.Equal("b", File.ReadAllText(sources[1]));
    }

    [Fact]
    public void Folders_block_file_targets_and_existing_exact_names_can_stay_unchanged()
    {
        using var temp = new TempDir();
        temp.Dir("Holiday (1).txt");
        var plan = NumberedRename.Plan(new[] { temp.File("a.txt"), temp.File("Holiday (3).txt") }, "Holiday");
        Assert.Equal(new[] { "Holiday (2).txt", "Holiday (3).txt" }, plan.Select(p => p.NewName));
        Assert.Equal(RenameStatus.Unchanged, plan[1].Status);
        Assert.Single(BatchRename.Execute(plan).Renamed);
        Assert.True(Directory.Exists(temp["Holiday (1).txt"]));
    }

    [Fact]
    public void Common_names_are_literal_and_never_expand_batch_template_tokens()
    {
        using var temp = new TempDir();
        var plan = NumberedRename.Plan(new[] { temp.File("a.txt"), temp.File("b.txt") }, "{name} [x] $value");
        Assert.Equal("{name} [x] $value (1).txt", plan[0].NewName);
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData(".")] [InlineData("..")]
    [InlineData("bad/name")] [InlineData("bad\\name")] [InlineData("bad:name")]
    [InlineData("bad?name")] [InlineData("bad*name")] [InlineData("bad|name")]
    [InlineData("bad<name")] [InlineData("bad>name")] [InlineData("bad\"name")]
    [InlineData("trailing.")] [InlineData("trailing ")] [InlineData("CON")]
    [InlineData("nul.txt")] [InlineData("com1")] [InlineData("COM¹")]
    [InlineData("lpt9.data")] [InlineData("LPT²")]
    public void Invalid_or_reserved_base_names_are_rejected_before_any_change(string name)
    {
        using var temp = new TempDir();
        var sources = new[] { temp.File("a.txt", "a"), temp.File("b.txt", "b") };
        Assert.NotNull(NumberedRename.ValidateBaseName(name));
        Assert.Throws<ArgumentException>(() => NumberedRename.Plan(sources, name));
        Assert.Equal("a", File.ReadAllText(sources[0])); Assert.Equal("b", File.ReadAllText(sources[1]));
    }

    [Fact]
    public void Missing_duplicate_cross_folder_and_overlong_plans_are_rejected_as_a_whole()
    {
        using var temp = new TempDir();
        var a = temp.File("a.jpeg"); var b = temp.File("b.jpeg"); var other = temp.File("other/c.jpeg");
        Assert.Throws<IOException>(() => NumberedRename.Plan(new[] { a, temp["missing.jpeg"] }, "Holiday"));
        Assert.Throws<ArgumentException>(() => NumberedRename.Plan(new[] { a, a }, "Holiday"));
        Assert.Throws<ArgumentException>(() => NumberedRename.Plan(new[] { a, other }, "Holiday"));
        Assert.Throws<ArgumentException>(() => NumberedRename.Plan(new[] { a, b }, new string('x', 250)));
        Assert.True(File.Exists(a)); Assert.True(File.Exists(b));
    }

    [Fact]
    public void A_locked_source_rolls_back_every_completed_step()
    {
        using var temp = new TempDir();
        var sources = new[] { temp.File("a.txt", "a"), temp.File("b.txt", "b") };
        var plan = NumberedRename.Plan(sources, "Holiday");
        using var locked = new FileStream(sources[1], FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = BatchRename.Execute(plan);
        Assert.True(result.RolledBack); Assert.NotEmpty(result.Errors); Assert.Empty(result.Renamed);
        Assert.Equal("a", File.ReadAllText(sources[0])); Assert.Equal("b", File.ReadAllText(sources[1]));
        Assert.Empty(Directory.GetFiles(temp.Path, ".fl-rename-*"));
        Assert.False(File.Exists(temp["Holiday (1).txt"]));
    }

    [Fact]
    public void A_target_created_after_planning_is_preserved_and_the_batch_rolls_back()
    {
        using var temp = new TempDir();
        var sources = new[] { temp.File("a.txt", "a"), temp.File("b.txt", "b") };
        var plan = NumberedRename.Plan(sources, "Holiday");
        temp.File("Holiday (2).txt", "external");
        var result = BatchRename.Execute(plan);
        Assert.True(result.RolledBack); Assert.Empty(result.Renamed); Assert.NotEmpty(result.Errors);
        Assert.Equal("external", File.ReadAllText(temp["Holiday (2).txt"]));
        Assert.Equal("a", File.ReadAllText(sources[0])); Assert.Equal("b", File.ReadAllText(sources[1]));
        Assert.False(File.Exists(temp["Holiday (1).txt"]));
        Assert.Empty(Directory.GetFiles(temp.Path, ".fl-rename-*"));
    }
}
