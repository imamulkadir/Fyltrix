using System.Diagnostics;
using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.Tests;

public class DuplicateFinderTests
{
    [Fact]
    public void Finds_true_duplicates_only()
    {
        using var t = new TempDir();
        var big = new byte[300_000];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(t.File("a/one.bin"), big);
        File.WriteAllBytes(t.File("b/two.bin"), big);
        File.WriteAllBytes(t.File("b/deep/three.bin"), big);
        // same size, same first and last 64 KB, different middle: the fingerprint can't tell, SHA-256 must
        var trap = (byte[])big.Clone();
        trap[150_000] ^= 0xFF;
        File.WriteAllBytes(t.File("c/trap.bin"), trap);
        t.File("c/small1.txt", "hello");
        t.File("c/small2.txt", "hello");
        t.File("c/different.txt", "hellO");
        // a hard link is the same file, not a duplicate
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{t["c/link.bin"]}\" \"{t["a/one.bin"]}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();

        var r = DuplicateFinder.Find(new[] { t.Path }, new DuplicateOptions());
        Assert.Equal(2, r.Groups.Count);
        var bigGroup = r.Groups.Single(g => g.Size == 300_000);
        Assert.Equal(3, bigGroup.Files.Count);
        Assert.DoesNotContain(bigGroup.Files, f => f.Path.EndsWith("trap.bin"));
        Assert.Equal(1, r.HardLinksIgnored);
        Assert.Equal(600_000, bigGroup.Wasted);
        Assert.Equal(FileHasher.Sha256(t["a/one.bin"]), bigGroup.Sha256);
        var small = r.Groups.Single(g => g.Size == 5);
        Assert.Equal(2, small.Files.Count);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Options_filter_and_select_helpers()
    {
        using var t = new TempDir();
        t.File("x/a.jpg", "same"); t.File("x/b.jpg", "same"); t.File("x/c.txt", "same"); t.File("x/sub/d.jpg", "same");
        File.SetLastWriteTime(t["x/b.jpg"], DateTime.Now.AddDays(-3));
        var flat = DuplicateFinder.Find(new[] { t["x"] }, new DuplicateOptions { Recursive = false, Extensions = new[] { "jpg" } });
        Assert.Equal(2, flat.Groups.Single().Files.Count);
        var bigOnly = DuplicateFinder.Find(new[] { t["x"] }, new DuplicateOptions { MinSize = 100 });
        Assert.Empty(bigOnly.Groups);

        var all = DuplicateFinder.Find(new[] { t["x"] }, new DuplicateOptions());
        var remove = DuplicateFinder.SelectAllBut(all.Groups, files => files.OrderByDescending(f => f.Modified).First());
        Assert.Equal(3, remove.Count);
        Assert.Contains(t["x/b.jpg"], remove);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var t = new TempDir();
        for (int i = 0; i < 50; i++) t.File($"f{i}.txt", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DuplicateFinder.Find(new[] { t.Path }, new DuplicateOptions(), null, cts.Token));
    }
}

public class FileHasherTests
{
    [Fact]
    public void Known_vectors()
    {
        using var t = new TempDir();
        var f = t.File("abc.txt", "abc");
        var h = FileHasher.Compute(f, new[] { HashAlgorithmKind.Sha256, HashAlgorithmKind.Sha512, HashAlgorithmKind.Sha1, HashAlgorithmKind.Md5 });
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h[HashAlgorithmKind.Sha256]);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", h[HashAlgorithmKind.Sha1]);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", h[HashAlgorithmKind.Md5]);
        Assert.StartsWith("ddaf35a193617aba", h[HashAlgorithmKind.Sha512]);
    }

    [Theory]
    [InlineData("SHA256: BA7816BF 8F01CFEA 414140DE 5DAE2223 B00361A3 96177A9C B410FF61 F20015AD", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("90:01:50:98:3c:d2:4f:b0:d6:96:3f:7d:28:e1:7f:72", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("not a hash", null)]
    public void Normalizes_pasted_checksums(string input, string? expected) => Assert.Equal(expected, FileHasher.NormalizeExpected(input));

    [Fact]
    public void Parses_checksum_files_and_compares()
    {
        var lines = FileHasher.ParseChecksumFile("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad *abc.txt\nSHA256 (x y.bin) = BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\n# comment\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal("x y.bin", lines[1].file);
        Assert.True(FileHasher.Matches(lines[0].hash, lines[1].hash));
        Assert.Equal(HashAlgorithmKind.Sha256, FileHasher.KindForLength(64));
    }
}

public class FolderAnalyzerTests
{
    [Fact]
    public void Sizes_tree_skips_junction_loops_and_lists_largest()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t.File("root/a/big.bin"), new byte[50_000]);
        File.WriteAllBytes(t.File("root/a/b/mid.bin"), new byte[20_000]);
        File.WriteAllBytes(t.File("root/small.bin"), new byte[1_000]);
        // a junction pointing back up would loop forever if followed
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{t["root/a/b/loop"]}\" \"{t["root"]}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();

        var r = FolderAnalyzer.Analyze(t["root"], largestFiles: 2);
        Assert.Equal(71_000, r.Root.Size);
        Assert.Equal(3, r.Root.Files);
        Assert.Equal(1, r.LinksSkipped);
        var a = r.Root.Children.First();
        Assert.Equal("a", a.Name);
        Assert.Equal(70_000, a.Size);
        Assert.Equal(70_000.0 / 71_000, a.ShareOfParent, 3);
        Assert.Equal(new[] { t["root/a/big.bin"], t["root/a/b/mid.bin"] }, r.LargestFiles.Select(f => f.path));
    }

    [Fact]
    public void Treemap_fills_the_rectangle_without_overlap()
    {
        var sizes = new long[] { 500, 250, 120, 80, 40, 10, 0 };
        var rects = FolderAnalyzer.Treemap(sizes, 400, 300);
        Assert.Equal(sizes.Length, rects.Count);
        Assert.Equal((0, 0, 0, 0), rects[^1]);
        double area = rects.Sum(r => r.w * r.h);
        Assert.Equal(400 * 300, area, 1);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(sizes[i] * 120000.0 / 1000, rects[i].w * rects[i].h, 1);
            Assert.True(rects[i].x >= -0.01 && rects[i].y >= -0.01 && rects[i].x + rects[i].w <= 400.01 && rects[i].y + rects[i].h <= 300.01);
            for (int j = i + 1; j < 6; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                double ox = Math.Min(a.x + a.w, b.x + b.w) - Math.Max(a.x, b.x), oy = Math.Min(a.y + a.h, b.y + b.h) - Math.Max(a.y, b.y);
                Assert.False(ox > 0.01 && oy > 0.01, $"rects {i} and {j} overlap");
            }
        }
    }
}

public class SearchCriteriaTests
{
    private static List<string> Run(TempDir t, SearchCriteria c)
    {
        Assert.Null(c.Compile(out var m));
        return new DirectoryInfo(t.Path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Where(i => m!.MatchesMetadata(i) && (!m.NeedsContent || m.MatchesContent(i.FullName, default)))
            .Select(i => Path.GetRelativePath(t.Path, i.FullName)).OrderBy(x => x).ToList();
    }

    [Fact]
    public void Name_extension_size_date_and_content()
    {
        using var t = new TempDir();
        t.File("docs/Report 2023.txt", "quarterly revenue up");
        t.File("docs/report-2024.md", "nothing here");
        File.WriteAllBytes(t.File("img/photo.jpg"), new byte[5000]);
        File.WriteAllBytes(t.File("img/bin.dat"), new byte[] { 0, 1, 2, (byte)'r', (byte)'e', (byte)'v' });
        File.SetLastWriteTime(t["docs/report-2024.md"], new DateTime(2024, 6, 1));

        Assert.Equal(new[] { @"docs\Report 2023.txt", @"docs\report-2024.md" }, Run(t, new SearchCriteria { Name = "report", NameMode = NameMatch.Contains }));
        Assert.Equal(new[] { @"docs\report-2024.md" }, Run(t, new SearchCriteria { Name = "report", MatchCase = true, NameMode = NameMatch.Contains }));
        Assert.Equal(new[] { @"docs\report-2024.md" }, Run(t, new SearchCriteria { Name = @"^report-\d{4}", NameMode = NameMatch.Regex }));
        Assert.Equal(new[] { @"docs\Report 2023.txt" }, Run(t, new SearchCriteria { Name = "*2023.*" }));
        Assert.Equal(new[] { @"img\photo.jpg" }, Run(t, new SearchCriteria { Extensions = "jpg, *.png", MinSize = 1000 }));
        Assert.Equal(new[] { @"docs\report-2024.md" }, Run(t, new SearchCriteria { ModifiedBefore = new DateTime(2025, 1, 1), Type = ItemTypeFilter.FilesOnly }));
        Assert.Equal(new[] { @"docs\Report 2023.txt" }, Run(t, new SearchCriteria { Content = "REVENUE" }));
        Assert.Equal(new[] { "docs", "img" }, Run(t, new SearchCriteria { Type = ItemTypeFilter.FoldersOnly }));
    }

    [Fact]
    public void Bad_input_is_reported()
    {
        Assert.StartsWith("Invalid regular expression", new SearchCriteria { Name = "(", NameMode = NameMatch.Regex }.Compile(out _));
        Assert.NotNull(new SearchCriteria { MinSize = 10, MaxSize = 5 }.Compile(out _));
    }
}

[Collection("UI")] // TagStore.Shared follows AppSettings.Folder, which UI tests also set
public class TagStoreTests
{
    [Fact]
    public void Tags_labels_moves_and_cleanup()
    {
        using var t = new TempDir();
        var old = AppSettings.Folder;
        AppSettings.Folder = t.Dir("settings");
        try
        {
            var store = TagStore.Shared;
            var a = t.File("d/a.txt");
            var b = t.File("d/sub/b.txt");
            store.SetTags(new[] { a, b }, new[] { "Work", " work ", "urgent" }, Array.Empty<string>());
            store.SetLabel(new[] { a }, ColorLabel.Red);
            Assert.Equal(new[] { "urgent", "Work" }, store.Get(a)!.Tags);
            Assert.Equal(ColorLabel.Red, store.Get(a)!.Label);
            Assert.Equal(2, store.PathsWithTag("WORK").Count);

            Directory.Move(t["d"], t["e"]);
            store.Move(t["d"], t["e"]);
            Assert.Null(store.Get(a));
            Assert.Equal(ColorLabel.Red, store.Get(t["e/a.txt"])!.Label);
            Assert.NotNull(store.Get(t["e/sub/b.txt"]));

            store.SetTags(new[] { t["e/a.txt"] }, Array.Empty<string>(), new[] { "urgent", "work" });
            store.SetLabel(new[] { t["e/a.txt"] }, ColorLabel.None);
            Assert.Null(store.Get(t["e/a.txt"])); // empty entries are dropped

            File.Delete(t["e/sub/b.txt"]);
            Assert.Equal(1, store.RemoveMissing());
            Assert.Empty(store.AllTags());
            Assert.True(File.Exists(TagStore.FilePath));
        }
        finally { AppSettings.Folder = old; }
    }
}
