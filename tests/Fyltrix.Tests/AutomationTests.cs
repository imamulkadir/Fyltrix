using System.IO.Compression;
using Fyltrix.Core;
using Fyltrix.Core.Automation;

namespace Fyltrix.Tests;

[Collection("UI")] // the engine logs under AppSettings.Folder
public class AutomationTests
{
    private static AutomationRule Rule(TempDir t, RuleAction action, string ext = "") => new()
    {
        Name = "test", WatchFolder = t.Dir("watch"), TargetFolder = t["target"], Action = action, Extensions = ext, Trigger = TriggerKind.Manual,
    };

    private static T WithSettings<T>(TempDir t, Func<T> f)
    {
        var old = AppSettings.Folder;
        AppSettings.Folder = t.Dir("settings");
        try { return f(); } finally { AppSettings.Folder = old; }
    }

    [Fact]
    public void Conditions_and_temporary_names()
    {
        using var t = new TempDir();
        var r = Rule(t, RuleAction.Move, "zip, 7z");
        r.NamePattern = "invoice*";
        Assert.True(AutomationEngine.Matches(r, t.File("watch/invoice-42.zip")));
        Assert.False(AutomationEngine.Matches(r, t.File("watch/invoice-42.pdf")));
        Assert.False(AutomationEngine.Matches(r, t.File("watch/receipt.zip")));
        r.MinSizeKB = 1;
        Assert.False(AutomationEngine.Matches(r, t["watch/invoice-42.zip"]));
        Assert.True(AutomationEngine.IsTemporaryName("x.zip.crdownload"));
        Assert.True(AutomationEngine.IsTemporaryName("~$report.docx"));
        Assert.False(AutomationEngine.IsTemporaryName("report.docx"));
    }

    [Fact]
    public void Validation_blocks_loops_and_scripts_inside_the_watched_folder()
    {
        using var t = new TempDir();
        var r = Rule(t, RuleAction.Copy);
        Assert.Null(r.Validate());
        r.TargetFolder = t["watch"];
        Assert.Contains("inside the watched folder", r.Validate());
        r.TargetFolder = t["watch/sub"];
        r.IncludeSubfolders = true;
        Assert.Contains("inside the watched folder", r.Validate());
        var cmd = Rule(t, RuleAction.RunCommand);
        cmd.Command = t.File("watch/evil.cmd", "echo hi");
        Assert.Contains("can't run a program", cmd.Validate());
    }

    [Fact]
    public void Dry_run_changes_nothing_and_real_run_happens_once()
    {
        using var t = new TempDir();
        WithSettings(t, () =>
        {
            t.File("watch/a.txt", "a"); t.File("watch/b.log", "b");
            using var engine = new AutomationEngine();
            var rule = Rule(t, RuleAction.Move, "txt");
            var dry = engine.RunNowAsync(rule, dryRun: true).GetAwaiter().GetResult();
            Assert.Single(dry);
            Assert.StartsWith("Would move", dry[0].Outcome);
            Assert.True(File.Exists(t["watch/a.txt"]));
            Assert.False(Directory.Exists(t["target"]));

            var real = engine.RunNowAsync(rule, dryRun: false).GetAwaiter().GetResult();
            Assert.True(real[0].Success, real[0].Outcome);
            Assert.True(File.Exists(t["target/a.txt"]));
            Assert.True(File.Exists(t["watch/b.log"]));

            t.File("watch/a.txt", "again"); // same name again: never overwrites the earlier result
            engine.RunNowAsync(rule, dryRun: false).GetAwaiter().GetResult();
            Assert.Equal("a", File.ReadAllText(t["target/a.txt"]));
            Assert.Equal("again", File.ReadAllText(t["target/a (2).txt"]));
            Assert.Contains("Moved", File.ReadAllText(AutomationStore.LogPath));
            return 0;
        });
    }

    [Fact]
    public void Organize_by_date_and_run_command()
    {
        using var t = new TempDir();
        WithSettings(t, () =>
        {
            var f = t.File("watch/photo.jpg", "p");
            File.SetLastWriteTime(f, new DateTime(2023, 7, 9));
            using var engine = new AutomationEngine();
            var org = Rule(t, RuleAction.OrganizeByDate);
            Assert.True(engine.RunNowAsync(org, false).GetAwaiter().GetResult()[0].Success);
            Assert.True(File.Exists(t["target/2023/07/photo.jpg"]));

            t.File("watch/note.txt", "hello");
            var run = Rule(t, RuleAction.RunCommand);
            run.Command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            run.Arguments = $"/c copy \"{{path}}\" \"{t.Dir("cmdout")}\"";
            var log = engine.RunNowAsync(run, false).GetAwaiter().GetResult();
            Assert.Contains("exit code 0", log[0].Outcome);
            Assert.Equal("hello", File.ReadAllText(t["cmdout/note.txt"]));
            return 0;
        });
    }

    [Fact]
    public async Task Waits_for_files_that_are_still_being_written()
    {
        using var t = new TempDir();
        var engine = new AutomationEngine { Settle = TimeSpan.FromMilliseconds(200) };
        var path = t["growing.bin"];
        var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(new byte[10]);
        writer.Flush();
        var wait = engine.WaitUntilStable(path, CancellationToken.None);
        await Task.Delay(700);
        Assert.False(wait.IsCompleted); // the writer still holds the file
        writer.Dispose();
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Watcher_extracts_a_finished_download_once()
    {
        using var t = new TempDir();
        var old = AppSettings.Folder;
        AppSettings.Folder = t.Dir("settings");
        try
        {
            t.File("src/readme.txt", "inside");
            ZipFile.CreateFromDirectory(t["src"], t["pack.zip"]);
            var rule = Rule(t, RuleAction.ExtractArchive, "zip");
            rule.Trigger = TriggerKind.NewFile;
            rule.MoveOriginalTo = t["done"];
            using var engine = new AutomationEngine { Settle = TimeSpan.FromMilliseconds(300) };
            var logs = new List<AutomationLogEntry>();
            engine.Logged += (_, e) => { lock (logs) logs.Add(e); };
            engine.Start(new[] { rule });

            // a browser writes "pack.zip.crdownload" and renames it when finished
            File.Copy(t["pack.zip"], t["watch/pack.zip.crdownload"]);
            await Task.Delay(500);
            Assert.Empty(logs);
            File.Move(t["watch/pack.zip.crdownload"], t["watch/pack.zip"]);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            // Extraction can finish before the same job moves its original archive; wait for the complete result.
            while ((!File.Exists(t["target/pack/readme.txt"]) || !File.Exists(t["done/pack.zip"])) && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.Equal("inside", File.ReadAllText(t["target/pack/readme.txt"]));
            Assert.True(File.Exists(t["done/pack.zip"]));
            await Task.Delay(1000);
            lock (logs) Assert.Single(logs, l => !l.DryRun);
        }
        finally { AppSettings.Folder = old; }
    }
}
