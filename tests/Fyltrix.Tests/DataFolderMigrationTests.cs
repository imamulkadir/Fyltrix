using System.Diagnostics;
using System.Security.Cryptography;
using Fyltrix.Launcher;

namespace Fyltrix.Tests;

/// <summary>The one-time copy of settings from %AppData%\FileLens to %AppData%\Fyltrix, run against temporary folders only.</summary>
public class DataFolderMigrationTests
{
    private static Dictionary<string, string> Hashes(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(folder, f),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);

    private static string Legacy(TempDir t)
    {
        t.File("AppData/FileLens/settings.json", "{\"Theme\":1,\"ShowHidden\":true}");
        t.File("AppData/FileLens/tags.json", "{\"C:\\\\a.txt\":[\"red\"]}");
        t.File("AppData/FileLens/error.log", "old log");
        t.File("AppData/FileLens/sync/pair.json", "{}");
        var ro = t.File("AppData/FileLens/templates.json", "[]");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        return t["AppData/FileLens"];
    }

    [Fact]
    public void Legacy_profile_is_copied_and_left_unchanged()
    {
        using var t = new TempDir();
        string legacy = Legacy(t), target = t["AppData/Fyltrix"];
        var before = Hashes(legacy);
        Assert.Equal(legacy, DataFolders.CurrentRoaming(target, legacy)); // the updater snapshots the live (legacy) profile until the copy

        var notes = new List<string>();
        Assert.Equal(target, DataFolders.MigrateRoaming(target, legacy, notes.Add));

        Assert.Equal(before, Hashes(legacy)); // the old folder is only read: an older version rolled back to still has it
        var after = Hashes(target);
        foreach (var (file, hash) in before) Assert.Equal(hash, after[file]);
        Assert.True(DataFolders.IsLiveProfile(target));
        Assert.Equal(target, DataFolders.CurrentRoaming(target, legacy));
        Assert.False((File.GetAttributes(Path.Combine(target, "templates.json")) & FileAttributes.ReadOnly) != 0); // settings stay writable
        Assert.DoesNotContain(Directory.EnumerateDirectories(t["AppData"]), d => Path.GetFileName(d).Contains(".migrating-"));
        Assert.Contains(notes, n => n.Contains("copied"));
        File.SetAttributes(Path.Combine(legacy, "templates.json"), FileAttributes.Normal);
    }

    [Fact]
    public void New_install_gets_a_Fyltrix_profile_and_no_FileLens_folder()
    {
        using var t = new TempDir();
        string legacy = t["AppData/FileLens"], target = t["AppData/Fyltrix"];
        Assert.Equal(target, DataFolders.MigrateRoaming(target, legacy));
        Assert.True(DataFolders.IsLiveProfile(target));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void Copy_happens_once()
    {
        using var t = new TempDir();
        string legacy = Legacy(t), target = t["AppData/Fyltrix"];
        DataFolders.MigrateRoaming(target, legacy);
        File.WriteAllText(Path.Combine(target, "settings.json"), "{\"Theme\":2}");      // used and changed by this version
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"Theme\":0}");      // an older version changed its own copy
        Assert.Equal(target, DataFolders.MigrateRoaming(target, legacy));
        Assert.Equal("{\"Theme\":2}", File.ReadAllText(Path.Combine(target, "settings.json")));
        File.SetAttributes(Path.Combine(legacy, "templates.json"), FileAttributes.Normal);
    }

    [Fact]
    public void Unmarked_target_from_an_interrupted_attempt_is_completed_from_legacy()
    {
        using var t = new TempDir();
        string legacy = Legacy(t), target = t["AppData/Fyltrix"];
        t.File("AppData/Fyltrix/settings.json", "stale partial copy"); // never used live: no marker
        Assert.Equal(target, DataFolders.MigrateRoaming(target, legacy));
        Assert.Equal(File.ReadAllText(Path.Combine(legacy, "settings.json")), File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "sync", "pair.json")));
        File.SetAttributes(Path.Combine(legacy, "templates.json"), FileAttributes.Normal);
    }

    [Fact]
    public void A_failed_copy_keeps_using_the_legacy_profile_and_retries_next_start()
    {
        using var t = new TempDir();
        string legacy = Legacy(t), target = t["AppData/Fyltrix"];
        var before = Hashes(legacy);
        string result;
        var notes = new List<string>();
        using (new FileStream(Path.Combine(legacy, "tags.json"), FileMode.Open, FileAccess.Read, FileShare.None)) // e.g. locked by another program
            result = DataFolders.MigrateRoaming(target, legacy, notes.Add);
        Assert.Equal(legacy, result);
        Assert.False(Directory.Exists(target));
        Assert.DoesNotContain(Directory.EnumerateDirectories(t["AppData"]), d => Path.GetFileName(d).Contains(".migrating-"));
        Assert.Contains(notes, n => n.Contains("failed"));
        Assert.Equal(before, Hashes(legacy));

        Assert.Equal(target, DataFolders.MigrateRoaming(target, legacy)); // next start
        Assert.True(File.Exists(Path.Combine(target, "tags.json")));
        File.SetAttributes(Path.Combine(legacy, "templates.json"), FileAttributes.Normal);
    }

    [Fact]
    public void Junctions_inside_the_legacy_profile_are_not_followed()
    {
        using var t = new TempDir();
        string legacy = Legacy(t), target = t["AppData/Fyltrix"];
        t.File("Elsewhere/secret.txt", "outside the profile");
        var link = Path.Combine(legacy, "linked");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{t["Elsewhere"]}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(link));
        DataFolders.MigrateRoaming(target, legacy);
        Assert.False(Directory.Exists(Path.Combine(target, "linked")));
        Assert.True(File.Exists(t["Elsewhere/secret.txt"]));
        Directory.Delete(link); // removes the junction only
        File.SetAttributes(Path.Combine(legacy, "templates.json"), FileAttributes.Normal);
    }

    [Fact]
    public void Update_staging_stays_allowed_beside_the_Fyltrix_caches_but_not_inside_them()
    {
        Assert.Contains(DataFolders.WebView2, DataFolders.ProtectedFromStaging);
        Assert.Contains(DataFolders.LegacyRoaming, DataFolders.ProtectedFromStaging);
        Assert.DoesNotContain(DataFolders.Local, DataFolders.ProtectedFromStaging); // %LocalAppData%\Fyltrix\Updates is the default staging root
        Assert.Contains(DataFolders.Local, DataFolders.Protected);
        Assert.Contains(DataFolders.LegacyLocal, DataFolders.Protected);
    }
}
