using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>
/// The one icon (Segoe Fluent glyph + theme brush) for each place, so Quick Access, Favourites, tabs and This PC agree:
/// Desktop, Downloads, Documents, Pictures, Music and Videos keep their own icons, drives the drive icon, This PC the PC
/// icon, and any other folder the standard folder icon — all in the Quick Access colour (Accent).
/// </summary>
public static class LocationIcons
{
    public static readonly string Folder = G(0xE8B7);
    public static readonly string Favourite = G(0xE735);
    public static readonly string FavouriteOutline = G(0xE734);
    public static readonly string Clear = G(0xE894);
    public static readonly string Recent = G(0xE81C);
    public static readonly string Drive = G(0xEDA2);
    public static readonly string ThisPC = G(0xE7F8);
    public static readonly string Search = G(0xE721);
    public static readonly string Archive = G(0xE7B8);
    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint); // Segoe Fluent Icons / MDL2 glyph

    /// <summary>Quick Access and folder icons.</summary>
    public const string Brush = "Accent";

    public sealed record Place(string Name, string Path, string Glyph, string Brush);

    /// <summary>The special folders, in Quick Access order (only those that exist on this PC).</summary>
    public static IReadOnlyList<Place> QuickAccess()
    {
        var list = new List<Place>();
        void Add(string name, string? path, string glyph, string brush = Brush)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add(new Place(name, path, glyph, brush));
        }
        Add("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), G(0xE80F), "Fg2");
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), G(0xE7F4));
        Add("Downloads", AppSettings.DownloadsFolder, G(0xE896));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), G(0xE8A5));
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), G(0xE91B));
        Add("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), G(0xE8D6));
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), G(0xE714));
        return list;
    }

    private static IReadOnlyList<Place>? _cached;

    /// <summary>The glyph and brush for a folder path (a tab's location): its Quick Access icon if it is one of those places,
    /// the drive icon for a drive root, otherwise the standard folder icon.</summary>
    public static (string Glyph, string Brush) For(string path, bool thisPC = false, bool search = false, bool archive = false)
    {
        if (search) return (Search, "Fg2");
        if (thisPC) return (ThisPC, "Fg2");
        if (archive) return (Archive, Brush);
        var trimmed = path.TrimEnd('\\');
        foreach (var p in _cached ??= QuickAccess())
            if (AppSettings.SameFolder(p.Path, trimmed)) return (p.Glyph, p.Brush);
        if (Path.GetPathRoot(path) is { Length: > 0 } root && AppSettings.SameFolder(root, path)) return (Drive, "Fg2");
        return (Folder, Brush);
    }
}
