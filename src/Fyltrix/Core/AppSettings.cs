using System.Text.Json;

namespace Fyltrix.Core;

public enum SearchScope { Subfolders, CurrentFolder }
public enum ThemeMode { Dark, Light, System }
/// <summary>The preview pane when Fyltrix starts: as it was left, or always on / off.</summary>
public enum PreviewStartup { Remember, Show, Hide }
/// <summary>Where a new tab (Ctrl+T, +) opens.</summary>
public enum NewTabTarget { CurrentFolder, StartupFolder, ThisPC }
public enum UpdateMode { ManualOnly, NotifyOnly, DownloadAutomatically, InstallOnExit }

/// <summary>Per-user settings stored in %AppData%\FileLens\settings.json (no registry, no admin).</summary>
public sealed class AppSettings
{
    [System.Text.Json.Serialization.JsonConverter(typeof(UpdateModeConverter))]
    public UpdateMode UpdateMode { get; set; } = ApplicationDefaults.Updates;
    public List<string> Favourites { get; set; } = new();
    public List<string> OpenTabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 850;
    public bool WindowMaximized { get; set; }
    public bool PreviewVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 230;
    public double PreviewWidth { get; set; } = 560;
    public bool ShowHidden { get; set; }
    /// <summary>The default view, for folders that have no view of their own (see <see cref="FolderViews"/>): 0 = Details;
    /// 48 / 96 / 160 / 256 = thumbnails at that size. (Before views were per folder this was the one view for everything,
    /// so an existing choice carries over as the default.)</summary>
    public int ViewTileSize { get; set; }

    // ---------- per-folder views ----------

    /// <summary>The views Fyltrix has: Details (0) and thumbnails at four sizes.</summary>
    public static readonly int[] ViewSizes = { 0, 48, 96, 160, 256 };
    public const string ThisPCViewKey = "::thispc";
    public const string SearchViewKey = "::search";
    /// <summary>Above this many saved folder views, entries for local folders that no longer exist are dropped.</summary>
    internal static int MaxFolderViews = 5000;

    private Dictionary<string, int> _folderViews = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The view each folder was last set to, by normalised path (case-insensitive, no trailing separator). Kept here,
    /// never in the folders themselves. Folders not listed use <see cref="ViewTileSize"/>.</summary>
    public Dictionary<string, int> FolderViews
    {
        get => _folderViews;
        set
        {
            // rebuilt with the case-insensitive comparer (JSON gives a plain one) and normalised keys, so older or hand-edited
            // files can't hold the same folder twice
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (value != null)
                foreach (var (k, v) in value)
                    if (IsView(v) && !string.IsNullOrWhiteSpace(k)) d[ViewKey(k)] = v;
            _folderViews = d;
        }
    }

    public static bool IsView(int size) => Array.IndexOf(ViewSizes, size) >= 0;

    /// <summary>The key a folder's view is stored under: the full path without a trailing separator ("C:" for a drive root),
    /// compared case-insensitively. "" (This PC) and search results have keys of their own.</summary>
    public static string ViewKey(string folder)
    {
        if (string.IsNullOrEmpty(folder) || folder.StartsWith("::", StringComparison.Ordinal)) return string.IsNullOrEmpty(folder) ? ThisPCViewKey : folder;
        var p = folder.Trim();
        try { if (Path.IsPathFullyQualified(p)) p = Path.GetFullPath(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>The view for a folder: its own if it has one, otherwise the default.</summary>
    public int ViewFor(string key) =>
        RememberFolderViews && _folderViews.TryGetValue(key, out var v) && IsView(v) ? v : IsView(ViewTileSize) ? ViewTileSize : 0;

    public bool HasOwnView(string key) => _folderViews.ContainsKey(key);

    /// <summary>Remembers the view a folder was set to (only that folder's entry changes).</summary>
    public void SetFolderView(string key, int size)
    {
        if (!IsView(size)) return;
        _folderViews[key] = size;
        if (_folderViews.Count > MaxFolderViews) PruneFolderViews();
    }

    /// <summary>Forgets a folder's own view, so it follows the default again. True if it had one.</summary>
    public bool ResetFolderView(string key) => _folderViews.Remove(key);

    public void ResetAllFolderViews() => _folderViews.Clear();

    /// <summary>Drops entries for local folders that were deleted or moved away. Network paths are kept: checking them can
    /// stall on a disconnected share, and they may come back.</summary>
    internal void PruneFolderViews()
    {
        foreach (var key in _folderViews.Keys.ToList())
        {
            if (key.StartsWith("::", StringComparison.Ordinal) || key.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            var dir = key.Length == 2 && key[1] == ':' ? key + "\\" : key;
            bool gone;
            try { gone = !Directory.Exists(dir) && !File.Exists(dir) && DriveIsLocal(dir); } catch { gone = false; }
            if (gone) _folderViews.Remove(key);
        }
    }

    private static bool DriveIsLocal(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        try
        {
            var d = new DriveInfo(root);
            return d.DriveType is DriveType.Fixed && d.IsReady; // a removable or network drive may just be unplugged
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>A folder was renamed or moved inside Fyltrix: its view, and those of folders inside it, move with it.</summary>
    public void MoveFolderViews(string from, string to)
    {
        string f = ViewKey(from), t = ViewKey(to);
        foreach (var key in _folderViews.Keys.ToList())
        {
            string? moved = string.Equals(key, f, StringComparison.OrdinalIgnoreCase) ? t
                : key.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase) ? t + key[f.Length..] : null;
            if (moved == null) continue;
            int v = _folderViews[key];
            _folderViews.Remove(key);
            _folderViews[moved] = v;
        }
    }
    public bool HtmlScripts { get; set; }
    /// <summary>Folders listed in groups by date modified (Today, Yesterday, Last week…). Like Explorer, Downloads is grouped by default.</summary>
    public List<string> GroupByDateFolders { get; set; } = new() { DownloadsFolder };

    public bool IsGroupedByDate(string folder) => GroupByDateFolders.Any(f => SameFolder(f, folder));

    public void SetGroupedByDate(string folder, bool on)
    {
        GroupByDateFolders.RemoveAll(f => SameFolder(f, folder));
        if (on) GroupByDateFolders.Add(folder);
    }

    internal static bool SameFolder(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The Downloads folder where Windows keeps it (it can be moved, e.g. to D:\Downloads or into OneDrive).</summary>
    public static string DownloadsFolder
    {
        get
        {
            try
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0 && !string.IsNullOrEmpty(p)) return p;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string path);

    /// <summary>ffmpeg.exe (or its folder) chosen by the user; empty = look in PATH and common install folders.</summary>
    public string FfmpegPath { get; set; } = "";

    public List<CustomAction> CustomActions { get; set; } = new();

    // ---- context menu ----
    /// <summary>IDEs for "Open in …" on folders: detected ones (once the user has saved settings) and custom ones.
    /// Newly detected IDEs not in this list are shown too (see <see cref="IdeCatalog.Merge"/>).</summary>
    public List<IdeEntry> Ides { get; set; } = new();
    /// <summary>Extensions of Windows-registered New types (ShellNew) hidden from the New menu. Fyltrix' own templates use
    /// <see cref="Templates.FileTemplate.Enabled"/> in templates.json instead.</summary>
    public List<string> HiddenNewTypes { get; set; } = new();

    /// <summary>Automation rules only run when this is switched on (off by default).</summary>
    public bool AutomationEnabled { get; set; }

    // ---- search, recents ----
    /// <summary>Search box scope: Enter searches subfolders too (default), or typing only filters the current folder.</summary>
    public SearchScope SearchScope { get; set; } = SearchScope.Subfolders;
    public List<Tools.SearchCriteria> SavedSearches { get; set; } = new();
    public List<string> RecentFolders { get; set; } = new();

    public void AddRecent(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !TrackRecent) return;
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > 15) RecentFolders.RemoveRange(15, RecentFolders.Count - 15);
    }

    // ---- appearance ----
    /// <summary>Dark, Light, or follow Windows (applies when Fyltrix starts).</summary>
    public ThemeMode Theme { get; set; } = ApplicationDefaults.Theme;
    /// <summary>File names with their extension (report.pdf) or without (report), like Explorer's option.</summary>
    public bool ShowExtensions { get; set; } = true;
    public bool ShowTabIcons { get; set; } = true;
    public PreviewStartup PreviewAtStartup { get; set; } = PreviewStartup.Remember;

    // ---- navigation and tabs ----
    /// <summary>Reopen the tabs that were open when Fyltrix closed.</summary>
    public bool RestoreTabs { get; set; } = true;
    /// <summary>Opened at startup (when tabs aren't restored, or none were open). Empty = Desktop; "" for This PC is ThisPCStartup.</summary>
    public string StartupFolder { get; set; } = "";
    public NewTabTarget NewTabOpens { get; set; } = NewTabTarget.CurrentFolder;
    /// <summary>Ask before "Close other tabs" or closing the window closes more than one tab.</summary>
    public bool ConfirmCloseTabs { get; set; }

    // ---- sidebar ----
    public bool ShowQuickAccess { get; set; } = true;
    public bool ShowFavourites { get; set; } = true;
    public bool ShowRecent { get; set; } = true;
    /// <summary>Quick Access places that are hidden (by name: Home, Desktop, Downloads…).</summary>
    public List<string> HiddenQuickAccess { get; set; } = new();
    /// <summary>Sidebar sections folded up by clicking their title.</summary>
    public List<string> CollapsedSections { get; set; } = new();
    /// <summary>Keep folded sections folded after a restart (otherwise every section starts open).</summary>
    public bool RememberSidebarState { get; set; } = true;
    /// <summary>Remember folders visited (the sidebar's Recent list). Off: nothing new is added.</summary>
    public bool TrackRecent { get; set; } = true;

    // ---- views ----
    /// <summary>Each folder keeps its own view. Off: one view for every folder (the default view), as before.</summary>
    public bool RememberFolderViews { get; set; } = true;
    /// <summary>How folders are sorted when they open: a column name (Name, Modified, Type, Size) and direction.</summary>
    public string DefaultSort { get; set; } = "Name";
    public bool DefaultSortAscending { get; set; } = true;

    // ---- context menu and toolbar ----
    /// <summary>Right-click menu actions that are hidden (ids from ContextMenuActions).</summary>
    public List<string> HiddenMenuActions { get; set; } = new();
    /// <summary>Toolbar buttons that are hidden (ids from the toolbar catalogue).</summary>
    public List<string> HiddenToolbarButtons { get; set; } = ApplicationDefaults.HiddenToolbarButtons.ToList();
    public bool ShowFileOperationLabels { get; set; } = ApplicationDefaults.ShowFileOperationLabels;
    public bool ShowSecondaryToolbarLabels { get; set; } = ApplicationDefaults.ShowSecondaryToolbarLabels;
    /// <summary>Extra toolbar buttons: command palette commands by title, in order.</summary>
    public List<string> ToolbarExtras { get; set; } = ApplicationDefaults.ToolbarExtras.ToList();

    public bool ShowToolbarLabel(string id) => id == "new" || (ApplicationDefaults.FileOperationButtons.Contains(id)
        ? ShowFileOperationLabels : ShowSecondaryToolbarLabels);

    // ---- search ----
    /// <summary>What the search box finds: files and folders, files only, or folders only.</summary>
    public Tools.ItemTypeFilter SearchFinds { get; set; } = Tools.ItemTypeFilter.All;
    /// <summary>Typing in the search box filters the folder at once; off: only when Enter is pressed.</summary>
    public bool FilterWhileTyping { get; set; } = true;

    // ---- archives ----
    /// <summary>Double-click / Enter on a supported archive browses it like a folder.</summary>
    public bool OpenArchivesAsFolders { get; set; } = true;
    public double ArchiveMaxTotalGB { get; set; } = 64;
    public double ArchiveMaxRatio { get; set; } = 1000;
    public int ArchivePreviewMaxMB { get; set; } = 512;

    public Archives.ArchiveLimits ArchiveLimits() => new()
    {
        MaxTotalBytes = (long)(Math.Clamp(ArchiveMaxTotalGB, 0.01, 100_000) * 1024 * 1024 * 1024),
        MaxEntryBytes = (long)(Math.Clamp(ArchiveMaxTotalGB, 0.01, 100_000) * 1024 * 1024 * 1024),
        MaxRatio = Math.Max(2, ArchiveMaxRatio),
    };

    /// <summary>Where settings and logs live. Tests point this at a temporary folder.</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileLens");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    // The trial must fail rather than silently falling back to defaults; normal loading remains unchanged.
    internal static AppSettings LoadForUpdateTrial()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        if (new FileInfo(FilePath).Length > 8 * 1024 * 1024) throw new InvalidDataException("Settings exceed trial validation limit.");
        var strict = new JsonSerializerOptions(Options) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), strict) ?? throw new InvalidDataException("Invalid trial settings.");
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort */ }
    }
}
