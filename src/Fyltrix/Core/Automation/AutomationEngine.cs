using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fyltrix.Core.Archives;
using Fyltrix.Core.Imaging;

namespace Fyltrix.Core.Automation;

public enum TriggerKind { NewFile, NewOrChangedFile, Manual, Interval }

public enum RuleAction { Move, Copy, Rename, ExtractArchive, CompressToZip, ConvertImage, OrganizeByDate, RunCommand }

/// <summary>"When a matching file appears in a folder, do something with it." Stored in %AppData%\FileLens\automation.json.</summary>
public sealed class AutomationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;

    public string WatchFolder { get; set; } = "";
    public bool IncludeSubfolders { get; set; }
    public TriggerKind Trigger { get; set; } = TriggerKind.NewFile;
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>"zip 7z" — empty = any extension.</summary>
    public string Extensions { get; set; } = "";
    /// <summary>Wildcard on the file name, e.g. "invoice*". Empty = any.</summary>
    public string NamePattern { get; set; } = "";
    public long? MinSizeKB { get; set; }
    public long? MaxSizeKB { get; set; }

    public RuleAction Action { get; set; } = RuleAction.Move;
    public string TargetFolder { get; set; } = "";
    /// <summary>Batch-rename template for Rename ({name}, {date:…}, {n}).</summary>
    public string RenameTemplate { get; set; } = "{date:yyyy-MM-dd} {name}";
    public ImageFormat ConvertTo { get; set; } = ImageFormat.Png;
    /// <summary>For ExtractArchive / ConvertImage: move the original here afterwards (empty = leave it).</summary>
    public string MoveOriginalTo { get; set; } = "";
    /// <summary>For RunCommand: a program chosen by the user and its arguments ({path}, {name}, {folder} are filled in and quoted).</summary>
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "\"{path}\"";

    public string Summary()
    {
        var what = Extensions.Trim().Length > 0 ? Extensions.Trim() + " files" : "files";
        if (NamePattern.Trim().Length > 0) what += $" named {NamePattern.Trim()}";
        var when = Trigger switch
        {
            TriggerKind.NewFile => "new", TriggerKind.NewOrChangedFile => "new or changed", TriggerKind.Interval => $"every {IntervalMinutes} min,", _ => "on demand,",
        };
        var act = Action switch
        {
            RuleAction.Move => $"move to {TargetFolder}", RuleAction.Copy => $"copy to {TargetFolder}", RuleAction.Rename => $"rename to \"{RenameTemplate}\"",
            RuleAction.ExtractArchive => $"extract into {TargetFolder}", RuleAction.CompressToZip => $"zip into {TargetFolder}",
            RuleAction.ConvertImage => $"convert to {ConvertTo} in {TargetFolder}", RuleAction.OrganizeByDate => $"file into {TargetFolder}\\yyyy\\MM",
            RuleAction.RunCommand => $"run {Path.GetFileName(Command)}", _ => Action.ToString(),
        };
        return $"{when} {what} in {WatchFolder}: {act}";
    }

    /// <summary>Checks the rule can run safely. Returns an error message or null.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Give the rule a name.";
        if (!Directory.Exists(WatchFolder)) return "The watched folder doesn't exist.";
        if (Trigger == TriggerKind.Interval && IntervalMinutes < 1) return "The interval must be at least one minute.";
        bool needsTarget = Action is RuleAction.Move or RuleAction.Copy or RuleAction.ExtractArchive or RuleAction.CompressToZip or RuleAction.ConvertImage or RuleAction.OrganizeByDate;
        if (needsTarget)
        {
            if (string.IsNullOrWhiteSpace(TargetFolder) || !Path.IsPathFullyQualified(TargetFolder)) return "Choose a full target folder path.";
            if (IsInside(TargetFolder, WatchFolder) && (IncludeSubfolders || SamePath(TargetFolder, WatchFolder)) && Action != RuleAction.ExtractArchive)
                return "The target folder is inside the watched folder; the rule would process its own results again.";
        }
        if (MoveOriginalTo.Length > 0 && !Path.IsPathFullyQualified(MoveOriginalTo)) return "The folder for originals must be a full path.";
        if (Action == RuleAction.Rename && FileOps.ValidateName(RenameTemplate.Replace("{", "").Replace("}", "").Replace(":", "")) != null && !RenameTemplate.Contains('{'))
            return "The rename template isn't a valid file name.";
        if (Action == RuleAction.RunCommand)
        {
            if (!File.Exists(Command)) return "Choose the program to run.";
            if (IsInside(Command, WatchFolder)) return "For safety, a rule can't run a program from the folder it watches.";
        }
        return null;
    }

    internal static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    internal static bool IsInside(string path, string folder) =>
        (Path.GetFullPath(path).TrimEnd('\\') + "\\").StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

public sealed record AutomationLogEntry(DateTime Time, string Rule, string File, string Outcome, bool Success, bool DryRun);

public static class AutomationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string FilePath => Path.Combine(AppSettings.Folder, "automation.json");
    public static string LogPath => Path.Combine(AppSettings.Folder, "automation.log");

    public static List<AutomationRule> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<AutomationRule>>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }

    public static void Save(IEnumerable<AutomationRule> rules)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rules.ToList(), Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>
/// Runs automation rules while Fyltrix is open. File-system events are debounced, files are only touched once
/// they have stopped changing, results of Fyltrix's own actions are ignored (no loops), each file version is
/// handled once, and a periodic rescan catches events the watcher missed. Nothing is overwritten or deleted.
/// </summary>
public sealed class AutomationEngine : IDisposable
{
    private readonly List<(AutomationRule rule, FileSystemWatcher? watcher, Timer? timer)> _active = new();
    private readonly ConcurrentDictionary<string, DateTime> _ownOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _processed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<AutomationLogEntry> _log = new();
    private readonly ArchiveLimits _limits;

    /// <summary>How long a file must stay unchanged before it's processed.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(30);

    public event EventHandler<AutomationLogEntry>? Logged;
    public IReadOnlyList<AutomationLogEntry> Log { get { lock (_log) return _log.ToList(); } }

    public AutomationEngine(ArchiveLimits? limits = null) => _limits = limits ?? new ArchiveLimits();

    private static readonly string[] PartialSuffixes = { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".fl-partial", ".!ut" };

    public static bool IsTemporaryName(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$") || name.StartsWith(".~") || name.StartsWith(".fl-") || PartialSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private readonly object _lifecycle = new();

    public void Start(IEnumerable<AutomationRule> rules)
    {
        lock (_lifecycle) StartCore(rules);
    }

    private void StartCore(IEnumerable<AutomationRule> rules)
    {
        StopCore();
        if (_stop.IsCancellationRequested) return; // disposed while starting in the background
        foreach (var rule in rules.Where(r => r.Enabled && r.Validate() == null))
        {
            FileSystemWatcher? w = null;
            Timer? t = null;
            if (rule.Trigger is TriggerKind.NewFile or TriggerKind.NewOrChangedFile)
            {
                w = new FileSystemWatcher(rule.WatchFolder)
                {
                    IncludeSubdirectories = rule.IncludeSubfolders,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                var r = rule;
                w.Created += (_, e) => Schedule(r, e.FullPath);
                w.Renamed += (_, e) => Schedule(r, e.FullPath); // downloads finish by renaming .crdownload to the real name
                if (rule.Trigger == TriggerKind.NewOrChangedFile) w.Changed += (_, e) => Schedule(r, e.FullPath);
                w.Error += (_, _) => _ = Task.Run(() => Reconcile(r)); // buffer overflow: rescan instead of losing files
                w.EnableRaisingEvents = true;
                // also pick up files that changed while Fyltrix was closed... only for interval rules; new-file rules start fresh
            }
            var period = rule.Trigger == TriggerKind.Interval ? TimeSpan.FromMinutes(rule.IntervalMinutes) : TimeSpan.FromMinutes(10);
            if (rule.Trigger != TriggerKind.Manual)
            {
                var r2 = rule;
                t = new Timer(_ => _ = Task.Run(() => Reconcile(r2)), null, rule.Trigger == TriggerKind.Interval ? TimeSpan.Zero : period, period);
            }
            _active.Add((rule, w, t));
            if (rule.Trigger is TriggerKind.NewFile or TriggerKind.NewOrChangedFile) SeedExisting(rule);
        }
    }

    /// <summary>Files already present when a watch starts count as handled, so turning a rule on doesn't process the whole folder.</summary>
    private void SeedExisting(AutomationRule rule)
    {
        foreach (var f in Candidates(rule)) _processed.TryAdd(Key(rule, f), 0);
    }

    public void Stop()
    {
        lock (_lifecycle) StopCore();
    }

    private void StopCore()
    {
        foreach (var (_, w, t) in _active) { w?.Dispose(); t?.Dispose(); }
        _active.Clear();
        foreach (var p in _pending.Values) p.Cancel();
        _pending.Clear();
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _stop.Cancel();
            StopCore();
        }
    }

    private void Schedule(AutomationRule rule, string path)
    {
        if (IsTemporaryName(path) || IsOwnOutput(path) || !Matches(rule, path)) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var old = _pending.AddOrUpdate(path, cts, (_, prev) => { prev.Cancel(); return cts; });
        _ = Task.Run(async () =>
        {
            try
            {
                if (await WaitUntilStable(path, cts.Token)) await ProcessAsync(rule, path, dryRun: false, cts.Token);
            }
            catch (OperationCanceledException) { }
            finally { _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, cts)); }
        });
    }

    /// <summary>True once the file has kept its size and time for <see cref="Settle"/> and can be opened exclusively.</summary>
    public async Task<bool> WaitUntilStable(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + GiveUpAfter;
        (long, DateTime)? last = null;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            var now = (fi.Length, fi.LastWriteTimeUtc);
            if (last != now) { last = now; stableSince = DateTime.UtcNow; }
            else if (DateTime.UtcNow - stableSince >= Settle)
            {
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    return true;
                }
                catch (IOException) { stableSince = DateTime.UtcNow; } // still being written by someone
                catch (UnauthorizedAccessException) { return false; }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, Settle.TotalMilliseconds / 2 + 50)), ct);
        }
        return false;
    }

    public static bool Matches(AutomationRule rule, string path)
    {
        var name = Path.GetFileName(path);
        if (rule.Extensions.Trim().Length > 0)
        {
            var exts = rule.Extensions.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(e => "." + e.Trim().TrimStart('*', '.'));
            if (!exts.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return false;
        }
        if (rule.NamePattern.Trim().Length > 0)
        {
            var rx = "^" + Regex.Escape(rule.NamePattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (!Regex.IsMatch(name, rx, RegexOptions.IgnoreCase) && !Regex.IsMatch(Path.GetFileNameWithoutExtension(name), rx, RegexOptions.IgnoreCase)) return false;
        }
        if (rule.MinSizeKB != null || rule.MaxSizeKB != null)
        {
            long kb;
            try { kb = new FileInfo(path).Length / 1024; } catch (IOException) { return false; }
            if (kb < (rule.MinSizeKB ?? 0) || kb > (rule.MaxSizeKB ?? long.MaxValue)) return false;
        }
        return true;
    }

    private static IEnumerable<string> Candidates(AutomationRule rule)
    {
        try
        {
            return Directory.EnumerateFiles(rule.WatchFolder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = rule.IncludeSubfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            }).Where(f => !IsTemporaryName(f) && Matches(rule, f)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string Key(AutomationRule rule, string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return $"{rule.Id}|{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException) { return $"{rule.Id}|{path}"; }
    }

    /// <summary>Rescans the folder for matching files not handled yet (missed events, interval rules).</summary>
    public async Task Reconcile(AutomationRule rule)
    {
        foreach (var f in Candidates(rule))
        {
            if (_stop.IsCancellationRequested) return;
            if (_processed.ContainsKey(Key(rule, f)) || _pending.ContainsKey(f)) continue;
            if (IsOwnOutput(f)) continue;
            Schedule(rule, f);
        }
        await Task.CompletedTask;
    }

    /// <summary>Runs a rule on every matching file now (the "Run now" / "Dry run" buttons).</summary>
    public async Task<List<AutomationLogEntry>> RunNowAsync(AutomationRule rule, bool dryRun, CancellationToken ct = default)
    {
        var problem = rule.Validate();
        if (problem != null) return new List<AutomationLogEntry> { Write(rule, rule.WatchFolder, problem, false, dryRun) };
        var results = new List<AutomationLogEntry>();
        foreach (var f in Candidates(rule))
        {
            ct.ThrowIfCancellationRequested();
            if (!dryRun && _processed.ContainsKey(Key(rule, f))) continue;
            results.Add(await ProcessAsync(rule, f, dryRun, ct));
        }
        if (results.Count == 0) results.Add(Write(rule, rule.WatchFolder, "No matching files.", true, dryRun));
        return results;
    }

    public async Task<AutomationLogEntry> ProcessAsync(AutomationRule rule, string path, bool dryRun, CancellationToken ct)
    {
        using var updateWork = UpdateWork.Track();
        await _gate.WaitAsync(ct);
        try
        {
            var key = Key(rule, path);
            if (!dryRun && !_processed.TryAdd(key, 0)) return Write(rule, path, "Already handled.", true, dryRun);
            try
            {
                var outcome = await Task.Run(() => Apply(rule, path, dryRun, ct), ct);
                return Write(rule, path, outcome, true, dryRun);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
                                           or ArchivePasswordException or OperationLimitException or FileFormatException or ArgumentException
                                           or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {
                if (!dryRun) _processed.TryRemove(key, out _);
                return Write(rule, path, "Failed: " + ex.Message, false, dryRun);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Results of this engine (and anything inside a folder it just created) are never fed back into a rule.</summary>
    private bool IsOwnOutput(string path)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        foreach (var (p, when) in _ownOutputs)
        {
            if (when < cutoff) { _ownOutputs.TryRemove(p, out _); continue; }
            if (path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private string Own(string output)
    {
        _ownOutputs[output] = DateTime.UtcNow;
        return output;
    }

    private string Apply(AutomationRule r, string path, bool dry, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        string Target(string folder, string fileName) => FileOps.UniqueName(EnsureDir(folder, dry), fileName);
        switch (r.Action)
        {
            case RuleAction.Move:
            {
                var to = Target(r.TargetFolder, name);
                if (!dry) File.Move(path, Own(to));
                return $"{(dry ? "Would move" : "Moved")} to {to}";
            }
            case RuleAction.Copy:
            {
                var to = Target(r.TargetFolder, name);
                if (!dry) File.Copy(path, Own(to));
                return $"{(dry ? "Would copy" : "Copied")} to {to}";
            }
            case RuleAction.Rename:
            {
                var newName = Tools.BatchRename.NewName(path, false, new Tools.RenameRules { Template = r.RenameTemplate }, null, 1);
                var err = FileOps.ValidateName(newName);
                if (err != null) throw new IOException(err);
                if (newName == name) return "Name already matches.";
                var to = FileOps.UniqueName(Path.GetDirectoryName(path)!, newName);
                if (!dry) File.Move(path, Own(to));
                return $"{(dry ? "Would rename" : "Renamed")} to {Path.GetFileName(to)}";
            }
            case RuleAction.OrganizeByDate:
            {
                var t = File.GetLastWriteTime(path);
                var to = Target(Path.Combine(r.TargetFolder, t.ToString("yyyy"), t.ToString("MM")), name);
                if (!dry) File.Move(path, Own(to));
                return $"{(dry ? "Would file" : "Filed")} under {Path.GetDirectoryName(to)}";
            }
            case RuleAction.ExtractArchive:
            {
                if (!ArchiveFormats.IsArchive(path)) return "Skipped: not a supported archive.";
                ArchiveService.List(path); // validates the archive (and its password needs) before anything is written
                var dest = FileOps.UniqueName(EnsureDir(r.TargetFolder, dry), ArchiveFormats.StripArchiveExtension(name));
                if (dry) return $"Would extract into {dest}" + MoveOriginal(r, path, dry);
                var res = ArchiveService.Extract(path, Own(dest), new ExtractOptions { AskConflict = null, Limits = _limits, ZoneIdentifier = ZoneInfo.Read(path) }, null, ct);
                foreach (var c in res.CreatedRoots) Own(c);
                if (res.Errors.Count > 0) throw new IOException(string.Join("; ", res.Errors.Take(3)));
                return $"Extracted {res.Files} file(s) into {dest}" + MoveOriginal(r, path, dry);
            }
            case RuleAction.CompressToZip:
            {
                var to = Target(r.TargetFolder, Path.GetFileNameWithoutExtension(name) + ".zip");
                if (!dry) ArchiveService.Create(new[] { path }, Own(to), new CreateOptions(), null, ct);
                return $"{(dry ? "Would compress" : "Compressed")} to {to}";
            }
            case RuleAction.ConvertImage:
            {
                if (!ImageService.CanDecode(path)) return "Skipped: not an image Windows can read.";
                var to = Target(r.TargetFolder, Path.GetFileNameWithoutExtension(name) + ImageService.Extension(r.ConvertTo));
                if (!dry) ImageService.Save(ImageService.Load(path), Own(to), r.ConvertTo, 92, metadataFrom: path);
                return $"{(dry ? "Would convert" : "Converted")} to {to}" + MoveOriginal(r, path, dry);
            }
            case RuleAction.RunCommand:
            {
                var args = r.Arguments.Replace("{path}", path).Replace("{name}", name).Replace("{folder}", Path.GetDirectoryName(path));
                if (dry) return $"Would run: {r.Command} {args}";
                using var p = Process.Start(new ProcessStartInfo(r.Command, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)! })
                              ?? throw new IOException("The program couldn't be started.");
                if (!p.WaitForExit(TimeSpan.FromMinutes(10))) { p.Kill(true); throw new IOException("The program ran for more than 10 minutes and was stopped."); }
                return $"Ran {Path.GetFileName(r.Command)} (exit code {p.ExitCode})";
            }
        }
        return "Nothing to do.";
    }

    private string MoveOriginal(AutomationRule r, string path, bool dry)
    {
        if (string.IsNullOrWhiteSpace(r.MoveOriginalTo)) return "";
        var to = FileOps.UniqueName(EnsureDir(r.MoveOriginalTo, dry), Path.GetFileName(path));
        if (!dry) File.Move(path, Own(to));
        return $"; original {(dry ? "would move" : "moved")} to {r.MoveOriginalTo}";
    }

    private static string EnsureDir(string dir, bool dry)
    {
        if (!dry) Directory.CreateDirectory(dir);
        return dir;
    }

    private AutomationLogEntry Write(AutomationRule rule, string file, string outcome, bool ok, bool dry)
    {
        var e = new AutomationLogEntry(DateTime.Now, rule.Name, file, outcome, ok, dry);
        lock (_log)
        {
            _log.Add(e);
            if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        }
        if (!dry)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var logPath = AutomationStore.LogPath;
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 2_000_000) File.Move(logPath, logPath + ".old", overwrite: true);
                File.AppendAllText(logPath, $"{e.Time:u}\t{(ok ? "OK" : "FAIL")}\t{rule.Name}\t{file}\t{outcome}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Logged?.Invoke(this, e);
        return e;
    }
}
