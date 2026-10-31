using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fyltrix.Core;

/// <summary>
/// An editor/IDE that can open a folder ("Open in Visual Studio Code"). Detected ones are found by <see cref="IdeCatalog"/>;
/// custom ones are added by the user. {path} in the arguments is the folder (quoted here), {name} its name.
/// </summary>
public sealed class IdeEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "{path}";
    public bool Enabled { get; set; } = true;
    public bool IsCustom { get; set; }

    public IdeEntry Clone() => (IdeEntry)MemberwiseClone();

    /// <summary>Full path of the program, or null when it isn't installed (any more).</summary>
    [JsonIgnore] public string? ResolvedProgram => string.IsNullOrWhiteSpace(Program) ? null : CustomAction.Resolve(Program);

    [JsonIgnore] public bool IsConfigured => !string.IsNullOrWhiteSpace(Name) && ResolvedProgram != null;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Give the IDE a name.";
        if (string.IsNullOrWhiteSpace(Program)) return "Choose the IDE's program (.exe).";
        if (ResolvedProgram == null) return "The program wasn't found.";
        if (!Arguments.Contains("{path}")) return "The arguments need {path} (the folder to open).";
        return null;
    }

    /// <summary>The command line arguments for opening <paramref name="folder"/>.</summary>
    public string BuildArguments(string folder)
    {
        var template = Arguments.Replace("\"{path}\"", "{path}").Replace("\"{name}\"", "{name}");
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        return template.Replace("{path}", IdeCatalog.Quote(folder)).Replace("{name}", IdeCatalog.Quote(name.Length > 0 ? name : folder));
    }

    /// <summary>Why the folder can't be opened safely with this program, or null. Batch files (.cmd/.bat) run through cmd.exe,
    /// which treats &amp; | &lt; &gt; ^ % ! in an argument as commands or variables even inside quotes.</summary>
    public string? Problem(string folder)
    {
        var program = ResolvedProgram;
        if (program == null) return $"{Name} wasn't found at {Program}.";
        if (!Directory.Exists(folder)) return $"The folder {folder} doesn't exist.";
        var ext = Path.GetExtension(program).ToLowerInvariant();
        if (ext is ".cmd" or ".bat" && folder.IndexOfAny(BatchUnsafe) >= 0)
            return $"{Name} is set up with a batch file ({Path.GetFileName(program)}), which can't be given a folder name containing {string.Join(" ", folder.Where(c => BatchUnsafe.Contains(c)).Distinct())} safely. " +
                   "Point it at the IDE's .exe instead (Settings → Context menu).";
        return null;
    }

    private static readonly char[] BatchUnsafe = { '&', '|', '<', '>', '^', '%', '!', '"', '(', ')' };

    /// <summary>Starts the IDE directly (no command shell). Throws InvalidOperationException with a readable message when it can't.</summary>
    public void Open(string folder)
    {
        if (Problem(folder) is { } p) throw new InvalidOperationException(p);
        // CreateNoWindow only affects console programs (a .cmd launcher would otherwise flash a console window); GUI IDEs open normally
        var psi = new ProcessStartInfo(ResolvedProgram!, BuildArguments(folder)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        Process.Start(psi)?.Dispose();
    }
}

/// <summary>Finds installed IDEs and merges them with the user's list (<see cref="AppSettings.Ides"/>).</summary>
public static class IdeCatalog
{
    private static Task<List<IdeEntry>>? _detection;

    /// <summary>Starts looking for installed IDEs in the background (a few folder listings; no processes are started).</summary>
    public static void StartDetection(bool again = false)
    {
        if (_detection == null || again) _detection = Task.Run(Detect);
    }

    /// <summary>Tests: use this list instead of what is installed on the machine.</summary>
    internal static void SetDetectedForTest(IEnumerable<IdeEntry> detected) => _detection = Task.FromResult(detected.ToList());

    /// <summary>What detection found so far (empty until it has finished; menus are built often and never wait).</summary>
    public static IReadOnlyList<IdeEntry> Detected => _detection is { IsCompletedSuccessfully: true } t ? t.Result : Array.Empty<IdeEntry>();

    /// <summary>Waits for detection (for the settings window).</summary>
    public static IReadOnlyList<IdeEntry> DetectedWait()
    {
        StartDetection();
        try { return _detection!.GetAwaiter().GetResult(); }
        catch (Exception) { return Array.Empty<IdeEntry>(); }
    }

    /// <summary>
    /// The user's list with newly detected IDEs appended (enabled). A detected IDE whose saved program has gone (e.g. IntelliJ was
    /// upgraded into a new versioned folder) takes the newly found path. An IDE that is already listed under any id is not added twice.
    /// </summary>
    public static List<IdeEntry> Merge(IEnumerable<IdeEntry> saved, IEnumerable<IdeEntry> detected)
    {
        var result = saved.Select(s => s.Clone()).ToList();
        foreach (var d in detected)
        {
            var same = result.FirstOrDefault(r => !r.IsCustom && r.Id == d.Id);
            if (same != null)
            {
                if (same.ResolvedProgram == null) same.Program = d.Program;
                continue;
            }
            var dProgram = d.ResolvedProgram;
            if (dProgram != null && result.Any(r => string.Equals(r.ResolvedProgram, dProgram, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(d.Clone());
        }
        return result;
    }

    /// <summary>What the context menu shows: enabled, installed, and each program + arguments only once.</summary>
    public static List<IdeEntry> MenuEntries(IEnumerable<IdeEntry> saved) => MenuEntries(saved, Detected);

    public static List<IdeEntry> MenuEntries(IEnumerable<IdeEntry> saved, IEnumerable<IdeEntry> detected)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<IdeEntry>();
        foreach (var e in Merge(saved, detected))
        {
            if (!e.Enabled || !e.IsConfigured) continue;
            if (!seen.Add(e.ResolvedProgram + "\n" + e.Arguments.Trim())) continue;
            list.Add(e);
        }
        return list;
    }

    /// <summary>One argument quoted for the Windows command line (CommandLineToArgvW rules: backslashes before a quote are doubled,
    /// so "C:\" stays C:\ and isn't read as an escaped quote).</summary>
    public static string Quote(string arg)
    {
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ detection

    /// <summary>Installed IDEs on this PC. Exposed for tests with other roots.</summary>
    public static List<IdeEntry> Detect() => Detect(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        Environment.GetEnvironmentVariable("PATH") ?? "");

    internal static List<IdeEntry> Detect(string localAppData, string[] programFiles, string pathVariable)
    {
        var found = new List<IdeEntry>();
        void Add(string id, string name, string? exe)
        {
            if (exe == null || found.Any(f => f.Id == id || string.Equals(f.Program, exe, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(new IdeEntry { Id = id, Name = name, Program = exe });
        }
        string? First(params string[] candidates) => candidates.FirstOrDefault(SafeFileExists);
        IEnumerable<string> Under(string relative) =>
            new[] { Path.Combine(localAppData, "Programs", relative) }.Concat(programFiles.Select(p => Path.Combine(p, relative)));

        // VS Code and its relatives (Electron; the .exe takes a folder and is what Explorer's "Open with Code" uses)
        Add("vscode", "Visual Studio Code", First(Under(@"Microsoft VS Code\Code.exe").ToArray()) ?? FromPathShim(pathVariable, "code.cmd", @"..\Code.exe"));
        Add("vscode-insiders", "Visual Studio Code - Insiders", First(Under(@"Microsoft VS Code Insiders\Code - Insiders.exe").ToArray()));
        Add("vscodium", "VSCodium", First(Under(@"VSCodium\VSCodium.exe").ToArray()));
        Add("cursor", "Cursor", First(Under(@"cursor\Cursor.exe").Concat(Under(@"Cursor\Cursor.exe")).ToArray()));
        Add("windsurf", "Windsurf", First(Under(@"Windsurf\Windsurf.exe").ToArray()));
        Add("zed", "Zed", First(Under(@"Zed\Zed.exe").ToArray()));

        // JetBrains IDEs and Android Studio describe themselves in product-info.json (name, version, launcher)
        var jetbrains = new List<(string code, string name, Version version, string exe)>();
        var roots = programFiles.SelectMany(p => new[] { Path.Combine(p, "JetBrains"), Path.Combine(p, "Android") })
            .Append(Path.Combine(localAppData, "Programs"));
        foreach (var root in roots)
            foreach (var dir in SafeDirs(root))
                if (ReadProductInfo(dir) is { } p) jetbrains.Add(p);
        foreach (var dir in SafeDirs(Path.Combine(localAppData, "JetBrains", "Toolbox", "apps")))   // older Toolbox: apps\<product>\ch-0\<build>
            foreach (var channel in SafeDirs(dir))
                foreach (var build in SafeDirs(channel))
                    if (ReadProductInfo(build) is { } p) jetbrains.Add(p);
        foreach (var g in jetbrains.GroupBy(j => j.code, StringComparer.OrdinalIgnoreCase))
        {
            var newest = g.OrderByDescending(j => j.version).First();
            Add("jetbrains-" + newest.code.ToLowerInvariant(), newest.name, newest.exe);
        }

        // Visual Studio opens a folder with devenv.exe <folder>
        foreach (var pf in programFiles)
            foreach (var ver in SafeDirs(Path.Combine(pf, "Microsoft Visual Studio")).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                foreach (var edition in SafeDirs(ver))
                {
                    var devenv = Path.Combine(edition, "Common7", "IDE", "devenv.exe");
                    if (!SafeFileExists(devenv)) continue;
                    var year = Path.GetFileName(ver) switch { "18" => "2026", var y => y };
                    Add($"vs-{Path.GetFileName(ver)}-{Path.GetFileName(edition)}".ToLowerInvariant(), $"Visual Studio {year} {Path.GetFileName(edition)}", devenv);
                }

        Add("sublime", "Sublime Text", First(programFiles.SelectMany(p => new[] { Path.Combine(p, "Sublime Text", "sublime_text.exe"), Path.Combine(p, "Sublime Text 3", "sublime_text.exe") }).ToArray()));
        return found;
    }

    private static (string code, string name, Version version, string exe)? ReadProductInfo(string dir)
    {
        var file = Path.Combine(dir, "product-info.json");
        if (!SafeFileExists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var name = Str(root, "name");
            var code = Str(root, "productCode") ?? name;
            if (name == null || code == null || !root.TryGetProperty("launch", out var launch) || launch.ValueKind != JsonValueKind.Array) return null;
            foreach (var l in launch.EnumerateArray())
            {
                if (!string.Equals(Str(l, "os"), "Windows", StringComparison.OrdinalIgnoreCase)) continue;
                if (Str(l, "arch") is { } arch && !arch.Equals("amd64", StringComparison.OrdinalIgnoreCase)) continue; // Fyltrix is x64-only
                var exe = Str(l, "launcherPath") is { } rel ? Path.GetFullPath(Path.Combine(dir, rel)) : null;
                if (exe == null || !SafeFileExists(exe)) continue;
                var version = Version.TryParse(Str(root, "buildNumber") ?? Str(root, "version"), out var v) ? v : new Version(0, 0);
                return (code, name, version, exe);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    /// <summary>A program found through a launcher script on PATH (e.g. code.cmd in "Microsoft VS Code\bin"), resolved to the .exe next to it.</summary>
    private static string? FromPathShim(string pathVariable, string shim, string relativeExe)
    {
        foreach (var dir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var d = Environment.ExpandEnvironmentVariables(dir.Trim());
                if (!SafeFileExists(Path.Combine(d, shim))) continue;
                var exe = Path.GetFullPath(Path.Combine(d, relativeExe));
                if (SafeFileExists(exe)) return exe;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Array.Empty<string>(); }
    }
}
