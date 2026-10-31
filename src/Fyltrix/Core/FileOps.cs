namespace Fyltrix.Core;

public enum ConflictChoice { Replace, KeepBoth, Skip, Cancel }

public sealed class TransferResult
{
    public List<string> Created { get; } = new();                 // copy targets that now exist
    public List<(string from, string to)> Moved { get; } = new(); // completed moves
    public List<(string original, DateTime when)> Replaced { get; } = new(); // overwritten items (now in Recycle Bin)
    public List<string> Errors { get; } = new();
    public bool Cancelled { get; set; }
}

/// <summary>
/// File operations built on the Windows shell (SHFileOperation): you get Explorer's own
/// progress dialog, the Recycle Bin, and long-path handling, without admin rights.
/// </summary>
public static class FileOps
{
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static bool RunMulti(uint func, IList<string> from, IList<string> to, IntPtr owner)
    {
        if (from.Count == 0) return true;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = func,
            pFrom = string.Join("\0", from) + "\0",
            pTo = string.Join("\0", to) + "\0",
            fFlags = (ushort)(Native.FOF_MULTIDESTFILES | Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR | Native.FOF_NOCONFIRMATION),
        };
        int rc = Native.SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Deletes to the Recycle Bin (or permanently). Returns false if cancelled/failed.</summary>
    public static bool Delete(IList<string> paths, bool permanent, IntPtr owner, bool silent = false)
    {
        if (paths.Count == 0) return true;
        ushort flags = Native.FOF_NOCONFIRMATION;
        if (!permanent) flags |= Native.FOF_ALLOWUNDO | Native.FOF_WANTNUKEWARNING;
        if (silent) flags |= Native.FOF_SILENT;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = Native.FO_DELETE,
            pFrom = string.Join("\0", paths) + "\0",
            pTo = null,
            fFlags = flags,
        };
        int rc = Native.SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>"name (2).ext" style – used for new items and "keep both".</summary>
    public static string UniqueName(string dir, string name)
    {
        var candidate = Path.Combine(dir, name);
        if (!Exists(candidate)) return candidate;
        var (stem, ext) = SplitName(candidate, name);
        for (int i = 2; ; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Exists(candidate)) return candidate;
        }
    }

    /// <summary>"name - Copy.ext", "name - Copy (2).ext" – used when pasting into the same folder.</summary>
    public static string UniqueCopyName(string dir, string name)
    {
        var (stem, ext) = SplitName(Path.Combine(dir, name), name);
        var candidate = Path.Combine(dir, $"{stem} - Copy{ext}");
        for (int i = 2; Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{stem} - Copy ({i}){ext}");
        return candidate;
    }

    private static (string stem, string ext) SplitName(string fullPath, string name)
    {
        if (Directory.Exists(fullPath)) return (name, "");
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrEmpty(stem)) return (name, ""); // ".gitignore"
        return (stem, ext);
    }

    private static bool IsSameOrInside(string folder, string candidate)
    {
        var a = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        var b = Path.GetFullPath(candidate).TrimEnd('\\') + "\\";
        return b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies or moves items into <paramref name="destDir"/>, resolving name clashes.</summary>
    public static TransferResult Transfer(IList<string> sources, string destDir, bool move, IntPtr owner,
        Func<string, (ConflictChoice choice, bool applyToAll)> askConflict)
    {
        var result = new TransferResult();
        var from = new List<string>();
        var to = new List<string>();
        ConflictChoice? remembered = null;
        var now = DateTime.UtcNow;

        foreach (var raw in sources)
        {
            var src = raw.TrimEnd('\\');
            if (!Exists(src)) { result.Errors.Add($"'{src}' no longer exists."); continue; }
            var name = Path.GetFileName(src);
            var srcDir = Path.GetDirectoryName(src) ?? "";
            if (Directory.Exists(src) && IsSameOrInside(src, destDir))
            {
                result.Errors.Add($"Can't {(move ? "move" : "copy")} '{name}' into itself.");
                continue;
            }

            string target;
            if (string.Equals(srcDir.TrimEnd('\\'), destDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                if (move) continue;                      // moving onto itself = nothing to do
                target = UniqueCopyName(destDir, name);  // Explorer's "- Copy"
            }
            else
            {
                target = Path.Combine(destDir, name);
                if (Exists(target) || to.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    var choice = remembered ?? ConflictChoice.Skip;
                    if (remembered == null)
                    {
                        var (c, all) = askConflict(target);
                        choice = c;
                        if (all) remembered = c;
                    }
                    if (choice == ConflictChoice.Cancel) { result.Cancelled = true; return result; }
                    if (choice == ConflictChoice.Skip) continue;
                    if (choice == ConflictChoice.KeepBoth)
                    {
                        target = UniqueName(destDir, name);
                        while (to.Contains(target, StringComparer.OrdinalIgnoreCase))
                            target = UniqueName(destDir, Path.GetFileName(target) + "_");
                    }
                    else // Replace: recycle the old one first so Undo can bring it back
                    {
                        if (Exists(target))
                        {
                            if (!Delete(new[] { target }, permanent: false, owner, silent: true))
                            {
                                result.Errors.Add($"Couldn't replace '{target}'.");
                                continue;
                            }
                            result.Replaced.Add((target, now));
                        }
                    }
                }
            }
            from.Add(src);
            to.Add(target);
        }

        if (from.Count == 0) return result;
        bool ok = RunMulti(move ? Native.FO_MOVE : Native.FO_COPY, from, to, owner);
        if (!ok) result.Cancelled = true;

        for (int i = 0; i < from.Count; i++)
        {
            if (!Exists(to[i])) continue;
            if (move)
            {
                if (!Exists(from[i])) result.Moved.Add((from[i], to[i]));
            }
            else result.Created.Add(to[i]);
        }
        return result;
    }

    public static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The name can't be empty.";
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            return "A name can't contain any of these characters: \\ / : * ? \" < > |";
        if (name.Trim() is "." or "..") return "That name is reserved.";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "A name can't end with a space or a dot.";
        return null;
    }

    /// <summary>Renames a file or folder in place. Returns the new full path.</summary>
    public static string Rename(string path, string newName)
    {
        var err = ValidateName(newName);
        if (err != null) throw new IOException(err);
        var dir = Path.GetDirectoryName(path.TrimEnd('\\')) ?? throw new IOException("Can't rename a drive.");
        var newPath = Path.Combine(dir, newName);
        if (string.Equals(path, newPath, StringComparison.Ordinal)) return path;

        bool caseOnly = string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Exists(newPath)) throw new IOException($"'{newName}' already exists in this folder.");

        bool isDir = Directory.Exists(path);
        if (caseOnly)
        {
            var temp = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            if (isDir) { Directory.Move(path, temp); Directory.Move(temp, newPath); }
            else { File.Move(path, temp); File.Move(temp, newPath); }
        }
        else if (isDir) Directory.Move(path, newPath);
        else File.Move(path, newPath);
        return newPath;
    }

    public static string NewFolder(string dir)
    {
        var path = UniqueName(dir, "New folder");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = -1;
        do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
        return (v < 10 ? v.ToString("0.0") : v.ToString("0")) + " " + units[u];
    }
}
