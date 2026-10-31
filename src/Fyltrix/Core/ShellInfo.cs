using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fyltrix.Core;

/// <summary>Shell icons and type names ("Microsoft Edge PDF Document"), cached per extension.</summary>
public static class ShellInfo
{
    // Used from folder listings on background threads, so the caches are locked. Bounded: most keys are an extension (a few
    // hundred at most), but .exe/.lnk/.ico/.url icons are per file, which would otherwise grow with every folder visited.
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> TypeCache = new(StringComparer.OrdinalIgnoreCase);
    internal const int MaxIcons = 3000, MaxTypes = 2000;

    internal static int CachedIcons { get { lock (IconCache) return IconCache.Count; } }
    private static readonly HashSet<string> PerFileIcon = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".cpl", ".msc", ".appref-ms" };

    /// <param name="assumeType">Use the generic icon for the extension (for items that don't exist on disk, e.g. archive entries or New-menu templates).</param>
    public static ImageSource? Icon(string path, bool isDirectory, bool large = false, bool assumeType = false)
    {
        string key;
        bool useAttributes;
        if (isDirectory)
        {
            bool isRoot = !assumeType && Path.GetPathRoot(path)?.TrimEnd('\\').Length == path.TrimEnd('\\').Length;
            key = isRoot ? "drive:" + path : "<dir>";
            useAttributes = !isRoot;
        }
        else
        {
            var ext = Path.GetExtension(path);
            if (PerFileIcon.Contains(ext) && !assumeType) { key = path; useAttributes = false; }
            else { key = ext.Length == 0 ? "<noext>" : ext; useAttributes = true; }
        }
        key = (large ? "L|" : "S|") + key;
        lock (IconCache) if (IconCache.TryGetValue(key, out var cached)) return cached;

        ImageSource? img = null;
        try
        {
            var info = new Native.SHFILEINFO();
            uint flags = Native.SHGFI_ICON | (large ? Native.SHGFI_LARGEICON : Native.SHGFI_SMALLICON);
            if (useAttributes) flags |= Native.SHGFI_USEFILEATTRIBUTES;
            uint attrs = isDirectory ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
            var res = Native.SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), flags);
            if (res != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    img = src;
                }
                finally { Native.DestroyIcon(info.hIcon); }
            }
        }
        catch { /* no icon */ }
        lock (IconCache)
        {
            if (IconCache.Count >= MaxIcons) IconCache.Clear(); // rebuilt on demand (each icon is one quick shell call)
            IconCache[key] = img;
        }
        return img;
    }

    public static string TypeName(string path, bool isDirectory)
    {
        if (isDirectory) return "File folder";
        var ext = Path.GetExtension(path);
        lock (TypeCache) if (TypeCache.TryGetValue(ext, out var t)) return t;
        string name = "";
        try
        {
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfo(string.IsNullOrEmpty(ext) ? "file" : "file" + ext, Native.FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
            name = info.szTypeName ?? "";
        }
        catch { }
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " File";
        lock (TypeCache)
        {
            if (TypeCache.Count >= MaxTypes) TypeCache.Clear();
            TypeCache[ext] = name;
        }
        return name;
    }
}
