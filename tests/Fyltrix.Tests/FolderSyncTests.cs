using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.Tests;

[Collection("UI")] // the baseline lives under AppSettings.Folder, which UI tests also change
public class FolderSyncTests
{
    private static readonly CompareOptions Opts = new();

    /// <summary>Stands in for the Recycle Bin: moves items into a folder so nothing is lost and nothing touches the real bin.</summary>
    private static Func<IList<string>, bool> FakeBin(TempDir t) => paths =>
    {
        foreach (var p in paths)
        {
            var dest = Path.Combine(t.Dir("_bin"), Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(p));
            if (Directory.Exists(p)) Directory.Move(p, dest); else File.Move(p, dest);
        }
        return true;
    };

    private static void Touch(string path, DateTime time) => File.SetLastWriteTimeUtc(path, time);

    [Fact]
    public void One_way_update_and_mirror()
    {
        using var t = new TempDir();
        var old = DateTime.UtcNow.AddDays(-2);
        t.File("L/same.txt", "s"); t.File("R/same.txt", "s");
        Touch(t["L/same.txt"], old); Touch(t["R/same.txt"], old);
        t.File("L/new.txt", "n");
        t.File("L/sub/deep.txt", "d");
        t.File("L/changed.txt", "v2"); t.File("R/changed.txt", "v1");
        Touch(t["R/changed.txt"], old);
        t.File("R/target-newer.txt", "R"); t.File("L/target-newer.txt", "L");
        Touch(t["L/target-newer.txt"], old);
        t.File("R/extra.txt", "x");

        var items = FolderSync.Compare(t["L"], t["R"], Opts);
        Kind(items, "same.txt", DiffKind.Same);
        Kind(items, "new.txt", DiffKind.OnlyLeft);
        Kind(items, "changed.txt", DiffKind.LeftNewer);
        Kind(items, "target-newer.txt", DiffKind.RightNewer);
        Kind(items, "extra.txt", DiffKind.OnlyRight);

        FolderSync.Plan(items, SyncMode.LeftToRight, new SyncOptions());
        Assert.Equal(SyncAction.Skip, items.Single(i => i.Path == "target-newer.txt").Action); // never overwrite a newer target by default
        Assert.Equal(SyncAction.None, items.Single(i => i.Path == "extra.txt").Action);      // never delete unless mirroring
        var r = FolderSync.Execute(t["L"], t["R"], items, FakeBin(t));
        Assert.Empty(r.Errors);
        Assert.Equal("n", File.ReadAllText(t["R/new.txt"]));
        Assert.Equal("d", File.ReadAllText(t["R/sub/deep.txt"]));
        Assert.Equal("v2", File.ReadAllText(t["R/changed.txt"]));
        Assert.Equal("R", File.ReadAllText(t["R/target-newer.txt"]));
        Assert.True(File.Exists(t["R/extra.txt"]));
        Assert.Single(Directory.GetFiles(t["_bin"])); // the replaced v1 went to the bin, not into thin air
        Assert.Empty(Directory.GetFiles(t["R"], "*.fl-partial", SearchOption.AllDirectories));

        var mirror = FolderSync.Compare(t["L"], t["R"], Opts);
        FolderSync.Plan(mirror, SyncMode.LeftToRight, new SyncOptions { PropagateDeletions = true, OverwriteNewer = true });
        FolderSync.Execute(t["L"], t["R"], mirror, FakeBin(t));
        Assert.False(File.Exists(t["R/extra.txt"]));
        Assert.Equal("L", File.ReadAllText(t["R/target-newer.txt"]));
        Assert.All(FolderSync.Compare(t["L"], t["R"], Opts), i => Assert.Equal(DiffKind.Same, i.Kind));
    }

    [Fact]
    public void Two_way_uses_the_baseline_for_conflicts_and_deletions()
    {
        using var t = new TempDir();
        var old = AppSettings.Folder;
        AppSettings.Folder = t.Dir("settings");
        try
        {
            foreach (var n in new[] { "a.txt", "b.txt", "c.txt", "gone.txt" }) { t.File("L/" + n, n); t.File("R/" + n, n); }
            var start = DateTime.UtcNow.AddHours(-5);
            foreach (var f in Directory.GetFiles(t.Path, "*.txt", SearchOption.AllDirectories)) Touch(f, start);
            FolderSync.Baseline.Save(t["L"], t["R"], Opts);

            // a: changed left only. b: changed right only (but with an OLDER time than left, so timestamps alone would get it wrong).
            // c: changed on both sides. gone: deleted on the right.
            File.WriteAllText(t["L/a.txt"], "a2"); Touch(t["L/a.txt"], start.AddHours(1));
            File.WriteAllText(t["R/b.txt"], "b2"); Touch(t["R/b.txt"], start.AddMinutes(-30));
            File.WriteAllText(t["L/c.txt"], "cL"); File.WriteAllText(t["R/c.txt"], "cR!");
            File.Delete(t["R/gone.txt"]);

            var baseline = FolderSync.Baseline.Load(t["L"], t["R"]);
            Assert.NotNull(baseline);
            var items = FolderSync.Compare(t["L"], t["R"], Opts, baseline);
            Kind(items, "a.txt", DiffKind.LeftNewer);
            Kind(items, "b.txt", DiffKind.RightNewer);
            Kind(items, "c.txt", DiffKind.Conflict);
            Kind(items, "gone.txt", DiffKind.DeletedRight);

            FolderSync.Plan(items, SyncMode.TwoWay, new SyncOptions());
            Assert.Equal(SyncAction.Skip, items.Single(i => i.Path == "c.txt").Action);
            Assert.Equal(SyncAction.CopyToRight, items.Single(i => i.Path == "gone.txt").Action); // restored unless deletions are applied
            FolderSync.Plan(items, SyncMode.TwoWay, new SyncOptions { PropagateDeletions = true });
            Assert.Equal(SyncAction.DeleteLeft, items.Single(i => i.Path == "gone.txt").Action);
            items.Single(i => i.Path == "c.txt").Action = SyncAction.CopyToLeft; // the user resolves the conflict

            var r = FolderSync.Execute(t["L"], t["R"], items, FakeBin(t));
            Assert.Empty(r.Errors);
            Assert.Equal("a2", File.ReadAllText(t["R/a.txt"]));
            Assert.Equal("b2", File.ReadAllText(t["L/b.txt"]));
            Assert.Equal("cR!", File.ReadAllText(t["L/c.txt"]));
            Assert.False(File.Exists(t["L/gone.txt"]));
            Assert.Equal(1, r.Recycled);                               // gone.txt
            Assert.Equal(4, Directory.GetFiles(t["_bin"]).Length);     // + the replaced old copies of a (right), b and c (left)
        }
        finally { AppSettings.Folder = old; }
    }

    [Fact]
    public void Verify_content_catches_changes_with_identical_size_and_time()
    {
        using var t = new TempDir();
        t.File("L/x.bin", "AAAA"); t.File("R/x.bin", "AAAB");
        var time = DateTime.UtcNow.AddDays(-1);
        Touch(t["L/x.bin"], time); Touch(t["R/x.bin"], time);
        Kind(FolderSync.Compare(t["L"], t["R"], Opts), "x.bin", DiffKind.Same);
        Kind(FolderSync.Compare(t["L"], t["R"], new CompareOptions { VerifyContent = true }), "x.bin", DiffKind.Different);
    }

    [Fact]
    public void Compare_window_compares_and_synchronises()
    {
        using var t = new TempDir();
        t.File("L/a.txt", "a"); t.File("L/sub/b.txt", "b"); t.File("R/only-right.txt", "r");
        Sta.Run(() =>
        {
            var owner = new System.Windows.Window();
            Sta.ShowOffscreen(owner);
            var w = new Fyltrix.UI.FolderCompareWindow(owner, t["L"], t["R"]);
            Sta.ShowOffscreen(w);
            try
            {
                Sta.WaitUntil(() => w.Items.Count > 0, "comparison");
                Assert.Equal(SyncAction.CopyToRight, w.Items.Single(i => i.Path == "a.txt").Action);
                Assert.Equal(SyncAction.None, w.Items.Single(i => i.Path == "only-right.txt").Action);
                w.SetMode(SyncMode.TwoWay);
                Assert.Equal(SyncAction.CopyToLeft, w.Items.Single(i => i.Path == "only-right.txt").Action);
                Sta.Render(w, "folder-compare");
                var sync = w.SyncAsync(confirm: false, recycle: FakeBin(t));
                Sta.WaitUntil(() => sync.IsCompleted, "sync");
                Assert.Empty(sync.Result!.Errors);
                Assert.Equal("b", File.ReadAllText(t["R/sub/b.txt"]));
                Assert.Equal("r", File.ReadAllText(t["L/only-right.txt"]));
                Sta.WaitUntil(() => w.Items.Count > 0 && w.Items.All(i => i.Kind == DiffKind.Same), "re-compare after sync");
            }
            finally { w.Close(); owner.Close(); }
        });
    }

    private static void Kind(List<DiffItem> items, string path, DiffKind kind) =>
        Assert.True(items.Single(i => i.Path == path).Kind == kind, $"{path}: expected {kind}, got {items.Single(i => i.Path == path).Kind}");
}
