using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

public class FuzzyTests
{
    [Fact]
    public void Matches_subsequences_and_ranks_word_starts()
    {
        Assert.NotNull(Fuzzy.Score("nfol", "New folder"));
        Assert.Null(Fuzzy.Score("xyz", "New folder"));
        Assert.True(Fuzzy.Score("dup", "Find duplicate files…") > Fuzzy.Score("dup", "Undo: copy duplicated item"));
        Assert.True(Fuzzy.Score("new", "New folder") > Fuzzy.Score("new", "Show preview pane, renew"));
        var names = new[] { "Folder sizes…", "New folder", "Compare folders…" };
        Assert.Equal("New folder", names.OrderByDescending(n => Fuzzy.Score("nf", n) ?? int.MinValue).First());
        Assert.Equal(0, Fuzzy.Score("", "anything"));
    }
}

[Collection("UI")]
public class PaletteUiTests
{
    [Fact]
    public void Palette_finds_commands_templates_and_paths()
    {
        using var t = new TempDir();
        t.File("a/inner/x.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var p = new CommandPalette(w, w.PaletteEntries);
                Sta.ShowOffscreen(p);
                // Listing completion can precede the background destination-permission check.
                // The New command is intentionally absent until that existing check finishes.
                Sta.WaitUntil(() => w.Availability().New, "writable destination before palette New command");
                p.SetText("nfol");
                Assert.Equal("New folder", p.Visible[0].Title);
                p.SetText("markdown");
                Assert.Contains(p.Visible, e => e.Title == "New Markdown");
                p.SetText(t["a/inner"]);
                Assert.Equal("Go to", p.Visible[0].Category);
                Sta.Render(p, "palette");
                p.RunSelected();
                Sta.WaitUntil(() => w.View!.CurrentPath == t["a/inner"], "navigated via palette");
            }
            finally { w.Close(); }
        });
    }
}
