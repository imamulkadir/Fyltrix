using System.Text;
using System.Text.RegularExpressions;

namespace Fyltrix.Core.Tools;

public enum NameMatch { Contains, Wildcard, Regex }
public enum ItemTypeFilter { All, FilesOnly, FoldersOnly }

/// <summary>An advanced search: name, extension, size, dates, content, tags and attributes. Saved filters are serialised as-is.</summary>
public sealed class SearchCriteria
{
    public string Title { get; set; } = "";
    public string Name { get; set; } = "";
    public NameMatch NameMode { get; set; } = NameMatch.Wildcard;
    public bool MatchCase { get; set; }
    /// <summary>".jpg .png" style list; empty = any.</summary>
    public string Extensions { get; set; } = "";
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public DateTime? ModifiedAfter { get; set; }
    public DateTime? ModifiedBefore { get; set; }
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public string Content { get; set; } = "";
    public bool ContentRegex { get; set; }
    public ItemTypeFilter Type { get; set; } = ItemTypeFilter.All;
    public bool IncludeHidden { get; set; }
    public bool ReadOnlyOnly { get; set; }
    /// <summary>Only items carrying this Fyltrix tag (case-insensitive).</summary>
    public string Tag { get; set; } = "";
    public long MaxContentBytes { get; set; } = 32L * 1024 * 1024;

    public bool HasContent => Content.Length > 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (Name.Length > 0) parts.Add($"name {(NameMode == NameMatch.Regex ? "~" : "")}'{Name}'");
        if (Extensions.Trim().Length > 0) parts.Add(Extensions.Trim());
        if (MinSize != null || MaxSize != null) parts.Add($"size {(MinSize != null ? FileOps.FormatSize(MinSize.Value) : "0")}–{(MaxSize != null ? FileOps.FormatSize(MaxSize.Value) : "∞")}");
        if (ModifiedAfter != null || ModifiedBefore != null) parts.Add($"modified {ModifiedAfter:d}–{ModifiedBefore:d}");
        if (CreatedAfter != null || CreatedBefore != null) parts.Add($"created {CreatedAfter:d}–{CreatedBefore:d}");
        if (Content.Length > 0) parts.Add($"containing '{Content}'");
        if (Tag.Length > 0) parts.Add($"tag {Tag}");
        if (Type != ItemTypeFilter.All) parts.Add(Type == ItemTypeFilter.FilesOnly ? "files" : "folders");
        return parts.Count == 0 ? "everything" : string.Join(", ", parts);
    }

    /// <summary>Checks the options and builds a matcher. Returns an error message for bad input.</summary>
    public string? Compile(out SearchMatcher? matcher)
    {
        matcher = null;
        Regex? nameRx = null, contentRx = null;
        var opts = (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant;
        try
        {
            if (Name.Length > 0)
            {
                nameRx = NameMode switch
                {
                    NameMatch.Regex => new Regex(Name, opts, TimeSpan.FromMilliseconds(200)),
                    NameMatch.Wildcard when Name.IndexOfAny(new[] { '*', '?' }) >= 0 =>
                        new Regex("^" + Regex.Escape(Name).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", opts),
                    _ => new Regex(Regex.Escape(Name), opts),
                };
            }
            if (Content.Length > 0)
                contentRx = new Regex(ContentRegex ? Content : Regex.Escape(Content), opts | RegexOptions.Multiline, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex) { return "Invalid regular expression: " + ex.Message; }
        if (MinSize > MaxSize) return "The minimum size is larger than the maximum.";
        var exts = Extensions.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.TrimStart('*').StartsWith('.') ? e.TrimStart('*') : "." + e.TrimStart('*')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        matcher = new SearchMatcher(this, nameRx, contentRx, exts);
        return null;
    }
}

public sealed class SearchMatcher
{
    private readonly SearchCriteria _c;
    private readonly Regex? _name, _content;
    private readonly HashSet<string> _exts;
    private readonly HashSet<string>? _tagged;

    public SearchMatcher(SearchCriteria c, Regex? name, Regex? content, HashSet<string> exts)
    {
        _c = c;
        _name = name;
        _content = content;
        _exts = exts;
        if (c.Tag.Length > 0) _tagged = new HashSet<string>(TagStore.Shared.PathsWithTag(c.Tag), StringComparer.OrdinalIgnoreCase);
    }

    public bool NeedsContent => _content != null;

    /// <summary>Everything except file content (cheap; uses the directory listing's data only).</summary>
    public bool MatchesMetadata(FileSystemInfo info)
    {
        bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
        if (_c.Type == ItemTypeFilter.FilesOnly && isDir) return false;
        if (_c.Type == ItemTypeFilter.FoldersOnly && !isDir) return false;
        if (_c.ReadOnlyOnly && (info.Attributes & FileAttributes.ReadOnly) == 0) return false;
        if (_name != null && !_name.IsMatch(info.Name)) return false;
        if (_exts.Count > 0 && (isDir || !_exts.Contains(info.Extension))) return false;
        if (_c.MinSize != null || _c.MaxSize != null)
        {
            if (isDir) return false;
            long len = ((FileInfo)info).Length;
            if (len < (_c.MinSize ?? 0) || len > (_c.MaxSize ?? long.MaxValue)) return false;
        }
        if (_c.ModifiedAfter is { } ma && info.LastWriteTime < ma) return false;
        if (_c.ModifiedBefore is { } mb && info.LastWriteTime >= mb) return false;
        if (_c.CreatedAfter is { } ca && info.CreationTime < ca) return false;
        if (_c.CreatedBefore is { } cb && info.CreationTime >= cb) return false;
        if (_tagged != null && !_tagged.Contains(info.FullName)) return false;
        if (_content != null && isDir) return false;
        return true;
    }

    /// <summary>Text search inside a file. Binary files (NUL bytes) and files above the size cap are skipped.</summary>
    public bool MatchesContent(string path, CancellationToken ct)
    {
        if (_content == null) return true;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length == 0 || fi.Length > _c.MaxContentBytes) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var head = new byte[Math.Min(8000, fi.Length)];
            int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (Array.IndexOf(head, (byte)0, 0, n) >= 0 && !(n >= 2 && (head[0] == 0xFF && head[1] == 0xFE || head[0] == 0xFE && head[1] == 0xFF)))
                return false; // binary
            fs.Position = 0;
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            // read in overlapping chunks so matches across chunk borders are found
            var buffer = new char[1 << 18];
            string carry = "";
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var text = carry + new string(buffer, 0, read);
                if (_content.IsMatch(text)) return true;
                carry = text.Length > 1024 ? text.Substring(text.Length - 1024) : text;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { return false; }
    }
}
