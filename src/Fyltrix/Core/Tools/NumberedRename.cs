using System.Globalization;

namespace Fyltrix.Core.Tools;

/// <summary>Literal common-name numbering; execution and undo use the existing batch engine.</summary>
public static class NumberedRename
{
    public static string? ValidateBaseName(string name)
    {
        var error = FileOps.ValidateName(name);
        if (error != null) return error;
        var device = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" ||
            device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(device[3]))
            return "That base name is reserved by Windows.";
        return name.Length > 255 ? "The base name is too long." : null;
    }

    /// <summary>Snapshot sibling names once, preserving the caller's visible order. Never changes disk contents.</summary>
    public static List<RenamePlanItem> Plan(IReadOnlyList<string> sources, string baseName)
    {
        var error = ValidateBaseName(baseName);
        if (error != null) throw new ArgumentException(error, nameof(baseName));
        if (sources.Count == 0) throw new ArgumentException("Select items to rename.", nameof(sources));
        var paths = sources.Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        var directory = Path.GetDirectoryName(paths[0]);
        if (directory == null || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length ||
            paths.Any(p => !string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Select distinct items in the same folder.", nameof(sources));
        if (paths.Any(p => !FileOps.Exists(p))) throw new IOException("A selected item no longer exists.");

        // Reserve selected source names too. Targets never take another source's old
        // name, so rollback/undo do not depend on another item moving out of the way.
        var occupied = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = new List<RenamePlanItem>(paths.Length);
        long number = 1;
        foreach (var path in paths)
        {
            string oldName = Path.GetFileName(path);
            string extension = Directory.Exists(path) || Path.GetFileNameWithoutExtension(oldName).Length == 0
                ? "" : Path.GetExtension(oldName);
            string name;
            do
            {
                name = $"{baseName} ({(number++).ToString(CultureInfo.InvariantCulture)}){extension}";
                if (name.Length > 255 || Path.Combine(directory, name).Length >= 32000)
                    throw new ArgumentException("A numbered name or path would be too long. Use a shorter base name.", nameof(baseName));
            } while (occupied.Contains(name) && !string.Equals(name, oldName, StringComparison.Ordinal));
            occupied.Add(name);
            plan.Add(new RenamePlanItem(path, name, name == oldName ? RenameStatus.Unchanged : RenameStatus.Ok, null));
        }
        return plan;
    }
}
