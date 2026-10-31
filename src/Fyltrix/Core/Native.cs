using System.Runtime.InteropServices;

namespace Fyltrix.Core;

/// <summary>Win32 / Shell P/Invoke declarations. Nothing here needs admin rights.</summary>
internal static class Native
{
    // ---------- Shell file info (icons, type names) ----------
    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_TYPENAME = 0x000000400;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- SHFileOperation (copy / move / delete with native progress + Recycle Bin) ----------
    public const uint FO_MOVE = 0x0001;
    public const uint FO_COPY = 0x0002;
    public const uint FO_DELETE = 0x0003;
    public const uint FO_RENAME = 0x0004;

    public const ushort FOF_MULTIDESTFILES = 0x0001;
    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    // x64 layout: default (8-byte) packing is correct.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    // ---------- Natural sort ("file2" before "file10"), same as Explorer ----------
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string psz1, string psz2);

    // ---------- Dark title bar ----------
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ---------- Association lookup (preview handler CLSID) ----------
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryString(uint flags, uint str, string pszAssoc, string? pszExtra,
        [Out] char[]? pszOut, ref uint pcchOut);

    public const uint ASSOCF_INIT_DEFAULTTOSTAR = 0x00000004;
    public const uint ASSOCF_NOTRUNCATE = 0x00000020;
    public const uint ASSOCSTR_SHELLEXTENSION = 16;

    // ---------- ShellExecuteEx (Properties dialog, Open with) ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    public const int SW_SHOW = 5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    // ---------- Alternate data streams (Mark of the Web) ----------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool DeleteFile(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFileAttributes(string lpFileName);

    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    // ---------- File identity (hard links share it) ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

    /// <summary>(volume, file index) identifying the file on disk, or null if it can't be read.</summary>
    public static (uint volume, ulong index, uint links)? FileIdentity(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(h, out var i)) return null;
            return (i.VolumeSerialNumber, ((ulong)i.FileIndexHigh << 32) | i.FileIndexLow, i.NumberOfLinks);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ---------- Identity of files *and folders*, and finding an item again after a rename/move ----------
    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileRaw(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type; // 0 = FileIdType (64-bit index)
        public long FileId;
        public long Padding; // the union is 16 bytes (FILE_ID_128)
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenFileById(Microsoft.Win32.SafeHandles.SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id, uint access, FileShare share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, [Out] char[] buffer, uint size, uint flags);

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenForAttributes(string path) =>
        CreateFileRaw(path, FILE_READ_ATTRIBUTES, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

    /// <summary>"volume:index" for a file or folder (stable across renames and moves on the same volume), or null.</summary>
    public static string? ItemId(string path)
    {
        using var h = OpenForAttributes(path);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var i)) return null;
        return $"{i.VolumeSerialNumber:x8}:{((ulong)i.FileIndexHigh << 32) | i.FileIndexLow:x16}";
    }

    /// <summary>Where the item with <paramref name="id"/> (from <see cref="ItemId"/>) is now, looking on the volume of <paramref name="lastKnownPath"/>.</summary>
    public static string? PathOfItemId(string id, string lastKnownPath)
    {
        var parts = id.Split(':');
        if (parts.Length != 2 || !ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var index)) return null;
        var root = Path.GetPathRoot(lastKnownPath);
        if (string.IsNullOrEmpty(root)) return null;
        using var volume = OpenForAttributes(root);
        if (volume.IsInvalid) return null;
        var desc = new FILE_ID_DESCRIPTOR { dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(), Type = 0, FileId = (long)index };
        using var h = OpenFileById(volume, ref desc, FILE_READ_ATTRIBUTES, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
        if (h.IsInvalid) return null;
        // the same index on another volume would be a different item
        if (!GetFileInformationByHandle(h, out var info) || $"{info.VolumeSerialNumber:x8}" != parts[0]) return null;
        var buf = new char[32768];
        uint n = GetFinalPathNameByHandle(h, buf, (uint)buf.Length, 0);
        if (n == 0 || n >= buf.Length) return null;
        var p = new string(buf, 0, (int)n);
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + p[8..];
        return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    public static void ShowProperties(string path, IntPtr owner)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };
        ShellExecuteEx(ref info);
    }

    /// <summary>True when the file carries the "downloaded from the internet" mark.</summary>
    public static bool HasMarkOfTheWeb(string path)
        => GetFileAttributes(path + ":Zone.Identifier") != INVALID_FILE_ATTRIBUTES;

    /// <summary>Removes the Zone.Identifier stream (same as ticking "Unblock" in Properties).</summary>
    public static bool Unblock(string path) => DeleteFile(path + ":Zone.Identifier");
}
