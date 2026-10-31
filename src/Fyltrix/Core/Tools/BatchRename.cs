using System.Globalization;
using System.Text.RegularExpressions;

namespace Fyltrix.Core.Tools;

public enum CaseChange { None, Lower, Upper, Title, Sentence }

/// <summary>
/// Batch rename rules, applied in this order to the name without its extension:
/// template → find/replace → remove characters → spaces → case → prefix/suffix; then the extension rule.
/// Template, prefix and suffix accept tokens: {name}, {n}, {n:000}, {ext}, {folder}, {date:yyyy-MM-dd} (modified), {created:…}.
/// </summary>
public sealed class RenameRules
{
    public string Template { get; set; } = "{name}";
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool UseRegex { get; set; }
    public bool MatchCase { get; set; }
    public string RemoveCharacters { get; set; } = "";
    /// <summary>Null keeps spaces; otherwise spaces become this text (e.g. "_" or "-" or "").</summary>
    public string? SpaceReplacement { get; set; }
    public CaseChange Case { get; set; }
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    /// <summary>Null keeps the extension; "" removes it; ".txt" or "txt" sets it.</summary>
    public string? NewExtension { get; set; }
    public bool LowercaseExtension { get; set; }
    public int CounterStart { get; set; } = 1;
    public int CounterStep { get; set; } = 1;
    public bool ApplyToFolders { get; set; } = true;
}

public enum RenameStatus { Ok, Unchanged, Invalid, Conflict }

public sealed record RenamePlanItem(string Source, string NewName, RenameStatus Status, string? Message)
{
    public string Directory => Path.GetDirectoryName(Source)!;
    public string OldName => Path.GetFileName(Source);
    public string Target => Path.Combine(Directory, NewName);
}

public sealed class RenameResult
{
    public List<(string from, string to)> Renamed { get; } = new();
    public List<string> Errors { get; } = new();
    public bool RolledBack { get; set; }
}

public static class BatchRename
{
    private static readonly Regex Token = new(@"\{(?<k>name|n|ext|folder|date|created)(:(?<f>[^}]*))?\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Computes every new name and flags problems. Nothing on disk changes.</summary>
    public static List<RenamePlanItem> Plan(IReadOnlyList<string> sources, RenameRules rules)
    {
        Regex? regex = null;
        string? regexError = null;
        if (rules.UseRegex && rules.Find.Length > 0)
        {
            try { regex = new Regex(rules.Find, (rules.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { regexError = "Invalid regular expression: " + ex.Message; }
        }

        var names = new List<(string source, string? name, string? error)>();
        int counter = rules.CounterStart;
        foreach (var src in sources)
        {
            bool isDir = System.IO.Directory.Exists(src);
            if (isDir && !rules.ApplyToFolders) { names.Add((src, Path.GetFileName(src.TrimEnd('\\')), null)); continue; }
            if (regexError != null) { names.Add((src, null, regexError)); continue; }
            try
            {
                names.Add((src, NewName(src, isDir, rules, regex, counter), null));
            }
            catch (RegexMatchTimeoutException) { names.Add((src, null, "The regular expression took too long.")); }
            catch (FormatException ex) { names.Add((src, null, "Bad format in a token: " + ex.Message)); }
            counter += rules.CounterStep;
        }

        // conflicts: two items ending up with the same name, or a name taken by a file that isn't being renamed
        var sourceSet = new HashSet<string>(sources.Select(Norm), StringComparer.OrdinalIgnoreCase);
        var targetCount = names.Where(n => n.name != null)
            .GroupBy(n => Norm(Path.Combine(Path.GetDirectoryName(n.source)!, n.name!)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // A name may be taken by another selected item only if that item really moves away; items that can't
        // move make others fail, so repeat until nothing changes.
        var moving = new HashSet<string>(sourceSet, StringComparer.OrdinalIgnoreCase);
        List<RenamePlanItem> plan;
        while (true)
        {
            plan = new List<RenamePlanItem>();
            foreach (var (source, name, error) in names)
            {
                if (error != null || name == null) { plan.Add(new RenamePlanItem(source, Path.GetFileName(source), RenameStatus.Invalid, error)); continue; }
                var invalid = FileOps.ValidateName(name);
                if (invalid != null) { plan.Add(new RenamePlanItem(source, name, RenameStatus.Invalid, invalid)); continue; }
                var target = Norm(Path.Combine(Path.GetDirectoryName(source)!, name));
                if (string.Equals(Path.GetFileName(source.TrimEnd('\\')), name, StringComparison.Ordinal))
                {
                    plan.Add(new RenamePlanItem(source, name, RenameStatus.Unchanged, null));
                    continue;
                }
                if (targetCount[target] > 1) { plan.Add(new RenamePlanItem(source, name, RenameStatus.Conflict, "Another item would get the same name.")); continue; }
                if (FileOps.Exists(target) && !moving.Contains(target) && !string.Equals(target, Norm(source), StringComparison.OrdinalIgnoreCase))
                {
                    plan.Add(new RenamePlanItem(source, name, RenameStatus.Conflict, "An item with this name already exists."));
                    continue;
                }
                if (target.Length >= 32000) { plan.Add(new RenamePlanItem(source, name, RenameStatus.Invalid, "The path would be too long.")); continue; }
                plan.Add(new RenamePlanItem(source, name, RenameStatus.Ok, null));
            }
            var nowMoving = new HashSet<string>(plan.Where(p => p.Status == RenameStatus.Ok).Select(p => Norm(p.Source)), StringComparer.OrdinalIgnoreCase);
            if (nowMoving.SetEquals(moving)) return plan;
            moving = nowMoving;
        }
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\');

    public static string NewName(string source, bool isDir, RenameRules r, Regex? regex, int counter)
    {
        var fileName = Path.GetFileName(source.TrimEnd('\\'));
        string stem = isDir ? fileName : Path.GetFileNameWithoutExtension(fileName);
        string ext = isDir ? "" : Path.GetExtension(fileName);
        if (!isDir && stem.Length == 0) { stem = fileName; ext = ""; } // ".gitignore"

        DateTime modified = DateTime.MinValue, created = DateTime.MinValue;
        if (r.Template.Contains("{date", StringComparison.OrdinalIgnoreCase) || r.Template.Contains("{created", StringComparison.OrdinalIgnoreCase)
            || r.Prefix.Contains('{') || r.Suffix.Contains('{'))
        {
            try
            {
                modified = File.GetLastWriteTime(source);
                created = File.GetCreationTime(source);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var folder = Path.GetFileName(Path.GetDirectoryName(source.TrimEnd('\\'))?.TrimEnd('\\') ?? "") ?? "";
        string Expand(string template) => Token.Replace(template, m =>
        {
            var f = m.Groups["f"].Success ? m.Groups["f"].Value : null;
            return m.Groups["k"].Value.ToLowerInvariant() switch
            {
                "name" => stem,
                "n" => counter.ToString(string.IsNullOrEmpty(f) ? "0" : f, CultureInfo.InvariantCulture),
                "ext" => ext.TrimStart('.'),
                "folder" => folder,
                "date" => modified.ToString(string.IsNullOrEmpty(f) ? "yyyy-MM-dd" : f, CultureInfo.InvariantCulture),
                "created" => created.ToString(string.IsNullOrEmpty(f) ? "yyyy-MM-dd" : f, CultureInfo.InvariantCulture),
                _ => m.Value,
            };
        });

        var name = Expand(string.IsNullOrEmpty(r.Template) ? "{name}" : r.Template);
        if (r.Find.Length > 0)
        {
            if (regex != null) name = regex.Replace(name, r.Replace);
            else name = name.Replace(r.Find, r.Replace, r.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        if (r.RemoveCharacters.Length > 0) name = new string(name.Where(c => !r.RemoveCharacters.Contains(c)).ToArray());
        if (r.SpaceReplacement != null) name = name.Replace(" ", r.SpaceReplacement);
        name = r.Case switch
        {
            CaseChange.Lower => name.ToLower(CultureInfo.CurrentCulture),
            CaseChange.Upper => name.ToUpper(CultureInfo.CurrentCulture),
            CaseChange.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower(CultureInfo.CurrentCulture)),
            CaseChange.Sentence => name.Length == 0 ? name : char.ToUpper(name[0], CultureInfo.CurrentCulture) + name.Substring(1).ToLower(CultureInfo.CurrentCulture),
            _ => name,
        };
        name = Expand(r.Prefix) + name + Expand(r.Suffix);

        if (!isDir)
        {
            if (r.NewExtension != null)
            {
                var e = r.NewExtension.Trim();
                ext = e.Length == 0 ? "" : (e.StartsWith('.') ? e : "." + e);
            }
            if (r.LowercaseExtension) ext = ext.ToLowerInvariant();
        }
        return name.Trim() + ext;
    }

    /// <summary>
    /// Renames every Ok item in two passes (to a temporary name, then to the final one) so swaps and case-only
    /// changes work. If a step fails, everything renamed so far is put back.
    /// </summary>
    public static RenameResult Execute(IEnumerable<RenamePlanItem> plan)
    {
        var result = new RenameResult();
        var items = plan.Where(p => p.Status == RenameStatus.Ok).ToList();
        var temps = new List<(RenamePlanItem item, string temp)>();
        try
        {
            foreach (var it in items)
            {
                var temp = Path.Combine(it.Directory, ".fl-rename-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                MoveItem(it.Source, temp);
                temps.Add((it, temp));
            }
            foreach (var (it, temp) in temps)
            {
                if (FileOps.Exists(it.Target)) throw new IOException($"'{it.NewName}' appeared while renaming.");
                MoveItem(temp, it.Target);
                result.Renamed.Add((it.Source, it.Target));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Errors.Add(ex.Message);
            Rollback(result, temps);
        }
        return result;
    }

    private static void Rollback(RenameResult result, List<(RenamePlanItem item, string temp)> temps)
    {
        result.RolledBack = true;
        var done = result.Renamed.ToDictionary(r => r.from, r => r.to, StringComparer.OrdinalIgnoreCase);
        foreach (var (it, temp) in temps)
        {
            try
            {
                var current = done.TryGetValue(it.Source, out var final) ? final : temp;
                if (FileOps.Exists(current) && !FileOps.Exists(it.Source)) MoveItem(current, it.Source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"Couldn't restore '{it.OldName}': {ex.Message}");
            }
        }
        result.Renamed.Clear();
    }

    private static void MoveItem(string from, string to)
    {
        if (System.IO.Directory.Exists(from)) System.IO.Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Puts every item back (used by Undo). Returns error messages.</summary>
    public static List<string> Revert(IReadOnlyList<(string from, string to)> renamed)
    {
        var back = renamed.Where(r => FileOps.Exists(r.to)).Select(r => new RenamePlanItem(r.to, Path.GetFileName(r.from), RenameStatus.Ok, null)).ToList();
        var res = Execute(back);
        return res.Errors;
    }
}
