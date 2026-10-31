using System.Diagnostics;

namespace Fyltrix.Core;

/// <summary>
/// A user-defined context-menu command, e.g. "Open in VS Code": program + arguments with placeholders.
/// {path} runs once per selected item; {paths} runs once with every item. Paths are always quoted.
/// Programs start directly (no command shell), so file names can't inject commands.
/// </summary>
public sealed class CustomAction
{
    public string Name { get; set; } = "";
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "\"{path}\"";
    /// <summary>"py txt" — empty = any file.</summary>
    public string Extensions { get; set; } = "";
    public bool ForFiles { get; set; } = true;
    public bool ForFolders { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Give the action a name.";
        var ext = Path.GetExtension(Program.Trim().Trim('"')).ToLowerInvariant();
        if (ext is ".ps1" or ".py" or ".vbs" or ".js") return $"Run {ext} scripts through their interpreter, e.g. program powershell.exe with arguments -File \"script.ps1\" \"{{path}}\".";
        if (!File.Exists(Environment.ExpandEnvironmentVariables(Program)) && Resolve(Program) == null) return "The program wasn't found.";
        if (!ForFiles && !ForFolders) return "Choose files, folders or both.";
        return null;
    }

    /// <summary>Full path of a program given by name only (e.g. "code.cmd", "notepad.exe"), via PATH.</summary>
    public static string? Resolve(string program)
    {
        var p = Environment.ExpandEnvironmentVariables(program.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(p)) return File.Exists(p) ? p : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var c = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), p);
                if (File.Exists(c)) return c;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public bool AppliesTo(IReadOnlyCollection<(string path, bool isDir)> items)
    {
        if (items.Count == 0) return false;
        var exts = Extensions.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(e => "." + e.Trim().TrimStart('*', '.')).ToList();
        return items.All(i => i.isDir ? ForFolders : ForFiles && (exts.Count == 0 || exts.Any(e => i.path.EndsWith(e, StringComparison.OrdinalIgnoreCase))));
    }

    private static string Q(string s) => "\"" + s.TrimEnd('\\') + "\"";

    /// <summary>The argument strings to run (one per item, or one for all with {paths}).</summary>
    public List<string> BuildArguments(IReadOnlyList<string> paths)
    {
        // the template's own quotes around a placeholder are dropped; every path is quoted here instead
        var template = Arguments.Replace("\"{path}\"", "{path}").Replace("\"{paths}\"", "{paths}").Replace("\"{folder}\"", "{folder}").Replace("\"{name}\"", "{name}");
        string Fill(string t, string path) => t.Replace("{path}", Q(path)).Replace("{folder}", Q(Path.GetDirectoryName(path.TrimEnd('\\')) ?? path))
            .Replace("{name}", Q(Path.GetFileName(path.TrimEnd('\\'))));
        if (template.Contains("{paths}"))
            return new List<string> { Fill(template.Replace("{paths}", string.Join(" ", paths.Select(Q))), paths[0]) };
        return paths.Select(p => Fill(template, p)).ToList();
    }

    public void Run(IReadOnlyList<string> paths)
    {
        var program = Resolve(Program) ?? Environment.ExpandEnvironmentVariables(Program);
        foreach (var args in BuildArguments(paths))
        {
            var dir = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
            Process.Start(new ProcessStartInfo(program, args) { UseShellExecute = false, WorkingDirectory = Directory.Exists(dir) ? dir : "" })?.Dispose();
        }
    }
}
