using System.Text.Json;

namespace Fyltrix.Core.Tools;

public enum ColorLabel { None, Red, Orange, Yellow, Green, Blue, Purple, Gray }

public sealed class TagEntry
{
    public List<string> Tags { get; set; } = new();
    public ColorLabel Label { get; set; }
}

/// <summary>
/// Tags and colour labels, kept in %AppData%\FileLens\tags.json keyed by full path. Files are never modified.
/// Moves and renames done in Fyltrix carry the tags along; items deleted or moved elsewhere can be cleaned up.
/// </summary>
public sealed class TagStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private Dictionary<string, TagEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedFrom;

    public static TagStore Shared { get; } = new();
    public event EventHandler? Changed;

    public static string FilePath => Path.Combine(AppSettings.Folder, "tags.json");

    private void EnsureLoaded()
    {
        var path = FilePath;
        if (_loadedFrom == path) return;
        _loadedFrom = path;
        _entries = new Dictionary<string, TagEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, TagEntry>>(File.ReadAllText(path), Json);
                if (data != null) foreach (var (k, v) in data) _entries[k] = v;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Key(string path) => Path.GetFullPath(path).TrimEnd('\\');

    public TagEntry? Get(string path)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries.TryGetValue(Key(path), out var e) ? e : null;
        }
    }

    public void SetLabel(IEnumerable<string> paths, ColorLabel label)
    {
        lock (_gate)
        {
            EnsureLoaded();
            foreach (var p in paths) Update(Key(p), e => e.Label = label);
            Save();
        }
    }

    public void SetTags(IEnumerable<string> paths, IEnumerable<string> add, IEnumerable<string> remove)
    {
        var addList = Clean(add);
        var removeSet = new HashSet<string>(Clean(remove), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            EnsureLoaded();
            foreach (var p in paths)
                Update(Key(p), e =>
                {
                    e.Tags.RemoveAll(t => removeSet.Contains(t));
                    foreach (var t in addList) if (!e.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)) e.Tags.Add(t);
                    e.Tags.Sort(StringComparer.CurrentCultureIgnoreCase);
                });
            Save();
        }
    }

    private static List<string> Clean(IEnumerable<string> tags) =>
        tags.Select(t => t.Trim()).Where(t => t.Length > 0 && t.Length <= 60).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Update(string key, Action<TagEntry> change)
    {
        if (!_entries.TryGetValue(key, out var e)) e = new TagEntry();
        change(e);
        if (e.Tags.Count == 0 && e.Label == ColorLabel.None) _entries.Remove(key);
        else _entries[key] = e;
    }

    public List<string> AllTags()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries.Values.SelectMany(e => e.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public List<string> PathsWithTag(string tag)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries.Where(kv => kv.Value.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        }
    }

    public List<string> PathsWithLabel(ColorLabel label)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries.Where(kv => kv.Value.Label == label).Select(kv => kv.Key).ToList();
        }
    }

    /// <summary>Follows a rename or move made in Fyltrix (folders take their contents' tags along).</summary>
    public void Move(string from, string to)
    {
        var f = Key(from);
        var t = Key(to);
        lock (_gate)
        {
            EnsureLoaded();
            bool changed = false;
            foreach (var k in _entries.Keys.ToList())
            {
                string? target = null;
                if (k.Equals(f, StringComparison.OrdinalIgnoreCase)) target = t;
                else if (k.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) target = t + k.Substring(f.Length);
                if (target == null) continue;
                var e = _entries[k];
                _entries.Remove(k);
                _entries[target] = e;
                changed = true;
            }
            if (changed) Save();
        }
    }

    /// <summary>Copies tags along with a copy made in Fyltrix.</summary>
    public void Copy(string from, string to)
    {
        var f = Key(from);
        var t = Key(to);
        lock (_gate)
        {
            EnsureLoaded();
            var add = _entries.Where(kv => kv.Key.Equals(f, StringComparison.OrdinalIgnoreCase) || kv.Key.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase))
                .Select(kv => (t + kv.Key.Substring(f.Length), new TagEntry { Label = kv.Value.Label, Tags = kv.Value.Tags.ToList() })).ToList();
            foreach (var (k, e) in add) _entries[k] = e;
            if (add.Count > 0) Save();
        }
    }

    /// <summary>Drops entries whose files no longer exist. Returns how many were removed.</summary>
    public int RemoveMissing()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var gone = _entries.Keys.Where(k => !File.Exists(k) && !Directory.Exists(k)).ToList();
            foreach (var k in gone) _entries.Remove(k);
            if (gone.Count > 0) Save();
            return gone.Count;
        }
    }

    public static string? Color(ColorLabel l) => l switch
    {
        ColorLabel.Red => "#FF6B6B", ColorLabel.Orange => "#FFA94D", ColorLabel.Yellow => "#FFD43B", ColorLabel.Green => "#69DB7C",
        ColorLabel.Blue => "#4DABF7", ColorLabel.Purple => "#B197FC", ColorLabel.Gray => "#ADB5BD", _ => null,
    };
}
