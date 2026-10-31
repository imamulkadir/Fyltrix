using System.Diagnostics;
using System.IO.Compression;
using Fyltrix.Core;
using Fyltrix.Core.Archives;

namespace Fyltrix.Tests;

public class ArchiveTests
{
    private static void MakeSampleTree(TempDir t)
    {
        t.File("src/Proj/readme.md", "# hi");
        t.File("src/Proj/sub/deep/data.json", "{\"a\":1}");
        t.File("src/Proj/sub/ünïcødé name.txt", "unicode");
        t.Dir("src/Proj/empty");
        File.SetLastWriteTime(t["src/Proj/readme.md"], new DateTime(2020, 5, 6, 7, 8, 10));
    }

    [Theory]
    [InlineData(ArchiveCreateFormat.Zip)]
    [InlineData(ArchiveCreateFormat.Tar)]
    [InlineData(ArchiveCreateFormat.TarGz)]
    [InlineData(ArchiveCreateFormat.TarBz2)]
    public void Create_then_extract_round_trips_tree_and_timestamps(ArchiveCreateFormat format)
    {
        using var t = new TempDir();
        MakeSampleTree(t);
        var archive = t["out/proj" + ArchiveFormats.Extension(format)];
        ArchiveService.Create(new[] { t["src/Proj"] }, archive, new CreateOptions { Format = format });
        Assert.True(File.Exists(archive));
        Assert.Empty(Directory.GetFiles(t["out"], "*.fl-partial"));

        var entries = ArchiveService.List(archive);
        Assert.Contains(entries, e => e.Path == "Proj/sub/deep/data.json" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Path == "Proj/empty" && e.IsDirectory);

        var dest = t.Dir("dest");
        var r = ArchiveService.Extract(archive, dest, new ExtractOptions());
        Assert.True(r.Errors.Count == 0, Dump(r));
        Assert.Equal("{\"a\":1}", File.ReadAllText(t["dest/Proj/sub/deep/data.json"]));
        Assert.Equal("unicode", File.ReadAllText(t["dest/Proj/sub/ünïcødé name.txt"]));
        Assert.True(Directory.Exists(t["dest/Proj/empty"]));
        var ts = File.GetLastWriteTime(t["dest/Proj/readme.md"]);
        Assert.True(Math.Abs((ts - new DateTime(2020, 5, 6, 7, 8, 10)).TotalSeconds) <= 2, $"timestamp {ts}");
        Assert.True(r.CreatedRoots.Count == 1 && r.CreatedRoots[0] == t["dest/Proj"], Dump(r));
    }

    [Fact]
    public void GZip_single_file_round_trip()
    {
        using var t = new TempDir();
        var src = t.File("log.txt", new string('a', 50_000));
        var gz = t["log.txt.gz"];
        ArchiveService.Create(new[] { src }, gz, new CreateOptions { Format = ArchiveCreateFormat.GZip });
        var list = ArchiveService.List(gz);
        Assert.Single(list);
        Assert.Equal("log.txt", list[0].Path);
        Assert.Equal(50_000, list[0].Size);
        var r = ArchiveService.Extract(gz, t.Dir("x"), new ExtractOptions());
        Assert.Empty(r.Errors);
        Assert.Equal(50_000, new FileInfo(t["x/log.txt"]).Length);
    }

    [Fact]
    public void GZip_rejects_folders()
    {
        using var t = new TempDir();
        t.File("a/b.txt");
        Assert.Throws<NotSupportedException>(() =>
            ArchiveService.Create(new[] { t["a"] }, t["a.gz"], new CreateOptions { Format = ArchiveCreateFormat.GZip }));
    }

    [Fact]
    public void Zip_slip_entries_are_skipped_and_nothing_escapes()
    {
        using var t = new TempDir();
        var zip = t["evil.zip"];
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "../escaped.txt", "ok/../../escaped2.txt", "/abs.txt", "C:/drive.txt", "good/file.txt", "ads.txt:stream" })
                using (var w = new StreamWriter(z.CreateEntry(name).Open())) w.Write("payload");
        }
        var dest = t.Dir("dest/inner");
        var r = ArchiveService.Extract(zip, dest, new ExtractOptions());
        Assert.True(File.Exists(t["dest/inner/good/file.txt"]));
        Assert.False(File.Exists(t["dest/escaped.txt"]));
        Assert.False(File.Exists(t["escaped2.txt"]));
        Assert.True(r.Skipped.Count == 5, Dump(r) + "\nLIST:" + string.Join(",", ArchiveService.List(zip).Select(e => e.Path)));
        Assert.Equal(1, r.Files);
        Assert.Single(Directory.GetFiles(t.Path, "*", SearchOption.AllDirectories), f => !f.EndsWith(".zip"));
    }

    [Theory]
    [InlineData("a/b.txt", "a\\b.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("dir/nul", "dir\\_nul")]
    [InlineData("we?ird*na<me>.txt", "we_ird_na_me_.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("./x/./y", "x\\y")]
    public void ResolveSafePath_sanitizes(string entry, string expectedRelative)
    {
        var root = @"C:\dest";
        Assert.Equal(Path.Combine(root, expectedRelative), ArchiveService.ResolveSafePath(root, entry, out _));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("/x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("D:x")]
    [InlineData("a/b:c")]
    public void ResolveSafePath_rejects(string entry)
    {
        Assert.Null(ArchiveService.ResolveSafePath(@"C:\dest", entry, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Total_size_limit_stops_bomb_and_leaves_no_partial_files()
    {
        using var t = new TempDir();
        var zip = t["bomb.zip"];
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var s = z.CreateEntry("zeros.bin", CompressionLevel.SmallestSize).Open())
            s.Write(new byte[2_000_000]);
        var dest = t.Dir("d");
        var r = ArchiveService.Extract(zip, dest, new ExtractOptions { Limits = new ArchiveLimits { MaxTotalBytes = 100_000 } });
        Assert.Contains(r.Errors, e => e.Contains("safety limit"));
        Assert.Empty(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Ratio_limit_detects_bomb()
    {
        using var t = new TempDir();
        var zip = t["bomb.zip"];
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var s = z.CreateEntry("zeros.bin", CompressionLevel.SmallestSize).Open())
            s.Write(new byte[5_000_000]);
        var r = ArchiveService.Extract(zip, t.Dir("d"), new ExtractOptions { Limits = new ArchiveLimits { MaxRatio = 50, RatioCheckAfterBytes = 0 } });
        Assert.Contains(r.Errors, e => e.Contains("decompression bomb"));
    }

    [Fact]
    public void Corrupted_archive_reports_error_instead_of_crashing()
    {
        using var t = new TempDir();
        MakeSampleTree(t);
        var zip = t["c.zip"];
        ArchiveService.Create(new[] { t["src/Proj"] }, zip, new CreateOptions { Format = ArchiveCreateFormat.TarGz });
        var bytes = File.ReadAllBytes(zip);
        var broken = t["broken.tar.gz"];
        File.WriteAllBytes(broken, bytes.Take(bytes.Length / 2).ToArray());
        var r = ArchiveService.Extract(broken, t.Dir("d"), new ExtractOptions());
        Assert.NotEmpty(r.Errors);

        var garbage = t.File("garbage.zip", "this is not a zip file at all");
        Assert.ThrowsAny<Exception>(() => ArchiveService.List(garbage));
    }

    [Fact]
    public void Selected_entries_with_prefix_extract_flat()
    {
        using var t = new TempDir();
        MakeSampleTree(t);
        var zip = t["p.zip"];
        ArchiveService.Create(new[] { t["src/Proj"] }, zip, new CreateOptions());
        var dest = t.Dir("d");
        var r = ArchiveService.Extract(zip, dest, new ExtractOptions { Entries = new[] { "Proj/sub/deep" }, StripPrefix = "Proj/sub" });
        Assert.Empty(r.Errors);
        Assert.True(File.Exists(t["d/deep/data.json"]));
        Assert.False(File.Exists(t["d/readme.md"]));
        Assert.Equal(1, r.Files);
    }

    [Fact]
    public void Conflicts_skip_or_keep_both()
    {
        using var t = new TempDir();
        t.File("src/a.txt", "new");
        var zip = t["a.zip"];
        ArchiveService.Create(new[] { t["src/a.txt"] }, zip, new CreateOptions());
        var dest = t.Dir("d");
        File.WriteAllText(t["d/a.txt"], "old");

        var skip = ArchiveService.Extract(zip, dest, new ExtractOptions { AskConflict = _ => (ConflictChoice.Skip, false) });
        Assert.Equal("old", File.ReadAllText(t["d/a.txt"]));
        Assert.Single(skip.Skipped);

        var both = ArchiveService.Extract(zip, dest, new ExtractOptions { AskConflict = _ => (ConflictChoice.KeepBoth, false) });
        Assert.Equal("old", File.ReadAllText(t["d/a.txt"]));
        Assert.Equal("new", File.ReadAllText(t["d/a (2).txt"]));
        Assert.Equal(new[] { t["d/a (2).txt"] }, both.CreatedRoots);
    }

    [Fact]
    public void Existing_junction_inside_destination_is_not_followed()
    {
        using var t = new TempDir();
        var outside = t.Dir("outside");
        var dest = t.Dir("dest");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{t["dest/j"]}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(t["dest/j"]));

        var zip = t["j.zip"];
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("j/planted.txt").Open())) w.Write("x");
        var r = ArchiveService.Extract(zip, dest, new ExtractOptions());
        Assert.Single(r.Errors);
        Assert.False(File.Exists(Path.Combine(outside, "planted.txt")));
    }

    [Fact]
    public void Tar_symlink_entries_are_skipped()
    {
        using var t = new TempDir();
        var tarPath = t["l.tar"];
        using (var fs = File.Create(tarPath))
        using (var w = new System.Formats.Tar.TarWriter(fs))
        {
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.SymbolicLink, "link") { LinkName = "C:\\Windows" });
            w.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "real.txt") { DataStream = new MemoryStream(new byte[] { 65 }) });
        }
        var r = ArchiveService.Extract(tarPath, t.Dir("d"), new ExtractOptions());
        Assert.Single(r.Skipped);
        Assert.True(File.Exists(t["d/real.txt"]));
        Assert.False(File.Exists(t["d/link"]) || Directory.Exists(t["d/link"]));
    }

    [Fact]
    public void Cancellation_stops_and_cleans_partial()
    {
        using var t = new TempDir();
        for (int i = 0; i < 20; i++) t.File($"src/f{i}.txt", new string('x', 1000));
        var zip = t["c.zip"];
        ArchiveService.Create(new[] { t["src"] }, zip, new CreateOptions());
        using var cts = new CancellationTokenSource();
        int seen = 0;
        var progress = new SyncProgress(_ => { if (++seen == 5) cts.Cancel(); });
        var dest = t.Dir("d");
        var r = ArchiveService.Extract(zip, dest, new ExtractOptions(), progress, cts.Token);
        Assert.True(r.Cancelled);
        Assert.True(r.Files < 20);
        Assert.Empty(Directory.GetFiles(dest, "*.fl-partial", SearchOption.AllDirectories));

        using var cts2 = new CancellationTokenSource();
        cts2.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveService.Create(new[] { t["src"] }, t["c2.zip"], new CreateOptions(), null, cts2.Token));
        Assert.False(File.Exists(t["c2.zip"]));
        Assert.Empty(Directory.GetFiles(t.Path, "*.fl-partial"));
    }

    [Fact]
    public void ExtractEntryToFile_extracts_single_entry()
    {
        using var t = new TempDir();
        MakeSampleTree(t);
        var archive = t["p.tar.gz"];
        ArchiveService.Create(new[] { t["src/Proj"] }, archive, new CreateOptions { Format = ArchiveCreateFormat.TarGz });
        var target = t["cache/data.json"];
        ArchiveService.ExtractEntryToFile(archive, "Proj/sub/deep/data.json", target, null, new ArchiveLimits());
        Assert.Equal("{\"a\":1}", File.ReadAllText(target));
        Assert.Throws<FileNotFoundException>(() => ArchiveService.ExtractEntryToFile(archive, "nope", t["cache/x"], null, new ArchiveLimits()));
    }

    [Theory]
    [InlineData("a.tar.gz", ArchiveKind.CompressedTar, "a")]
    [InlineData("A.TGZ", ArchiveKind.CompressedTar, "A")]
    [InlineData("x.zip", ArchiveKind.Zip, "x")]
    [InlineData("x.7z", ArchiveKind.SevenZip, "x")]
    [InlineData("log.txt.gz", ArchiveKind.GZip, "log.txt")]
    public void Format_detection(string name, ArchiveKind kind, string stripped)
    {
        Assert.Equal(kind, ArchiveFormats.Detect(name));
        Assert.Equal(stripped, ArchiveFormats.StripArchiveExtension(name));
    }

    [Fact]
    public void Non_archives_are_not_detected()
    {
        Assert.Null(ArchiveFormats.Detect("doc.docx"));
        Assert.Null(ArchiveFormats.Detect(".gz"));
    }

    private static string Dump(ExtractResult r) => $"files={r.Files} cancelled={r.Cancelled}\nERR:{string.Join("\n", r.Errors)}\nSKIP:{string.Join("\n", r.Skipped)}\nROOTS:{string.Join("\n", r.CreatedRoots)}";

    private sealed class SyncProgress : IProgress<OperationProgress>
    {
        private readonly Action<OperationProgress> _a;
        public SyncProgress(Action<OperationProgress> a) => _a = a;
        public void Report(OperationProgress value) => _a(value);
    }
}
