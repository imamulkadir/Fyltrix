namespace Fyltrix.Launcher;

/// <summary>
/// Where Fyltrix keeps per-user data, shared by the application, the updater, the launcher and the release tools so they
/// all protect the same folders.
///   %AppData%\Fyltrix       settings, tags, automation, templates, shares, sync state, error.log, crash reports
///   %LocalAppData%\Fyltrix  WebView2 (preview engine profile), Thumbs, ArchiveCache, and the updater's Updates staging
/// Versions up to 0.1.10 used "FileLens" folders. Their settings are copied (never moved) into %AppData%\Fyltrix on the
/// first normal start, so an older version that is rolled back to still finds its own folder intact. Legacy folders are
/// never created or written by this version; they stay protected as user data.
/// </summary>
public static class DataFolders
{
    public const string Name = "Fyltrix";
    public const string LegacyName = "FileLens";
    /// <summary>Present in a roaming folder that is (or has become) the live Fyltrix profile.</summary>
    public const string ProfileMarker = ".fyltrix-profile";

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Roaming => Path.Combine(AppData, Name);
    public static string Local => Path.Combine(LocalAppData, Name);
    public static string LegacyRoaming => Path.Combine(AppData, LegacyName);
    public static string LegacyLocal => Path.Combine(LocalAppData, LegacyName);

    public static string WebView2 => Path.Combine(Local, "WebView2");
    public static string Thumbs => Path.Combine(Local, "Thumbs");
    public static string ArchiveCache => Path.Combine(Local, "ArchiveCache");
    public static string LegacyArchiveCache => Path.Combine(LegacyLocal, "ArchiveCache");

    /// <summary>User-data folders that installations, release outputs, signing keys and update layouts must never overlap.</summary>
    public static IReadOnlyList<string> Protected => [Roaming, Local, LegacyRoaming, LegacyLocal];

    /// <summary>The same protection for update staging, which itself lives under %LocalAppData%\Fyltrix\Updates: only the
    /// data folders beside it are protected there.</summary>
    public static IReadOnlyList<string> ProtectedFromStaging => [Roaming, LegacyRoaming, LegacyLocal, WebView2, Thumbs, ArchiveCache];

    public static bool IsLiveProfile(string folder) => File.Exists(Path.Combine(folder, ProfileMarker));

    /// <summary>The roaming folder holding the live settings: the Fyltrix folder once it is in use, otherwise (before the
    /// first start of a migrating version) the legacy folder when it exists. Used by the updater to snapshot the profile.</summary>
    public static string CurrentRoaming() => CurrentRoaming(Roaming, LegacyRoaming);

    public static string CurrentRoaming(string target, string legacy) =>
        IsLiveProfile(target) || !Directory.Exists(legacy) ? target : legacy;

    /// <summary>
    /// Makes <paramref name="target"/> the live profile and returns the folder to use for this session. Legacy contents are
    /// copied (the legacy folder is only read). The copy is assembled in a temporary sibling and moved into place, then
    /// marked; until the marker exists the target is never used, so an interrupted attempt is simply repeated. On any
    /// failure nothing is lost: the legacy folder is returned and used as before, and the next start tries again.
    /// </summary>
    public static string MigrateRoaming(string target, string legacy, Action<string>? log = null)
    {
        if (IsLiveProfile(target)) return target;
        using var mutex = new Mutex(false, @"Local\Fyltrix.ProfileMigration");
        bool owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) { log?.Invoke("profile migration busy in another process; using the existing profile this time"); return CurrentRoaming(target, legacy); }
            if (IsLiveProfile(target)) return target; // another instance finished it meanwhile

            bool hasLegacy = Directory.Exists(legacy) && Directory.EnumerateFileSystemEntries(legacy).Any();
            if (hasLegacy)
            {
                if (Directory.Exists(target))
                    CopyTree(legacy, target); // an unmarked target was never used live: complete it from the legacy data
                else
                {
                    string temp = target + ".migrating-" + Guid.NewGuid().ToString("N")[..8];
                    try
                    {
                        CopyTree(legacy, temp);
                        Directory.Move(temp, target);
                    }
                    finally { if (Directory.Exists(temp)) TryDelete(temp); }
                }
            }
            else Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, ProfileMarker),
                $"Fyltrix profile\n{(hasLegacy ? "copied from " + legacy : "new")}\n{DateTime.UtcNow:O}\n");
            if (hasLegacy) log?.Invoke($"settings copied from the previous {LegacyName} folder; it is kept unchanged for older versions");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log?.Invoke("profile migration failed, using the existing profile this time: " + ex.GetType().Name + ": " + ex.Message);
            return Directory.Exists(legacy) ? legacy : target;
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    /// <summary>Copies files (overwriting older copies in the destination) without following links or junctions.</summary>
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string to = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo) CopyTree(entry.FullName, to);
            else
            {
                File.Copy(entry.FullName, to, overwrite: true);
                var attributes = File.GetAttributes(to);
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(to, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
