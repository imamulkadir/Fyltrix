using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Fyltrix.Core;

public sealed class FileItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public FileItem(string fullPath, bool isDirectory, long size, DateTime modified, bool isHidden = false, string? displayName = null)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Size = size;
        Modified = modified;
        IsHidden = isHidden;
        _name = displayName ?? Path.GetFileName(fullPath.TrimEnd('\\'));
        if (string.IsNullOrEmpty(_name)) _name = fullPath;
    }

    public static FileItem FromInfo(FileSystemInfo info)
    {
        bool dir = (info.Attributes & FileAttributes.Directory) != 0;
        long size = dir ? -1 : ((FileInfo)info).Length;
        bool hidden = (info.Attributes & FileAttributes.Hidden) != 0;
        return new FileItem(info.FullName, dir, size, info.LastWriteTime, hidden);
    }

    /// <summary>An entry inside an archive. <see cref="FullPath"/> is then a virtual path (archive path + entry path).</summary>
    public static FileItem FromArchiveEntry(string archiveFile, Archives.ArchiveEntryInfo e) =>
        new(Archives.ArchiveBrowser.VirtualPath(archiveFile, e.Path), e.IsDirectory, e.IsDirectory ? -1 : e.Size, e.Modified ?? DateTime.MinValue)
        {
            ArchiveFile = archiveFile,
            ArchiveKey = e.Path,
            IsEncrypted = e.IsEncrypted,
        };

    public string? ArchiveFile { get; init; }
    public string? ArchiveKey { get; init; }
    public bool IsVirtual => ArchiveFile != null;
    public bool IsEncrypted { get; init; }

    private string _name;
    public string Name { get => _name; set { _name = value; Raise(); Raise(nameof(DisplayName)); } }

    /// <summary>Settings → "Show file extensions" off: names are shown without their extension (like Explorer). The real
    /// name (<see cref="Name"/>) is still what renames, sorts and searches use.</summary>
    public static bool HideExtensions { get; set; }

    /// <summary>The name as shown in the list.</summary>
    public string DisplayName =>
        HideExtensions && !IsDirectory && !IsDrive && Path.GetFileNameWithoutExtension(_name) is { Length: > 0 } stem && stem.Length < _name.Length ? stem : _name;

    public string FullPath { get; private set; }
    public bool IsDirectory { get; }
    public bool IsDrive { get; init; }
    public long Size { get; }
    public DateTime Modified { get; }
    public bool IsHidden { get; }

    // ---------- drives (This PC): capacity and free space, -1 when unknown (not ready, not responding) ----------
    public long DriveTotal { get; init; } = -1;
    public long DriveFree { get; init; } = -1;
    public bool HasUsage => IsDrive && DriveTotal > 0 && DriveFree >= 0;
    public double UsedFraction => HasUsage ? Math.Clamp(1 - (double)DriveFree / DriveTotal, 0, 1) : 0;
    /// <summary>Under 10 % free, like Explorer: the bar turns red.</summary>
    public bool IsLowSpace => HasUsage && (double)DriveFree / DriveTotal < 0.10;
    public string UsageText => HasUsage ? $"{FileOps.FormatSize(DriveFree)} free of {FileOps.FormatSize(DriveTotal)}" : "";
    public string Tip => !HasUsage ? Name
        : string.Join(Environment.NewLine, Name,
            $"Used: {FileOps.FormatSize(DriveTotal - DriveFree)} ({UsedFraction:P0})",
            $"Free: {FileOps.FormatSize(DriveFree)} ({DriveFree:N0} bytes)",
            $"Total: {FileOps.FormatSize(DriveTotal)} ({DriveTotal:N0} bytes)");

    /// <summary>The usage tooltip for a drive; null (no tooltip) for anything else.</summary>
    public string? UsageTip => HasUsage ? Tip : null;

    public string Extension => IsDirectory ? "" : Path.GetExtension(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";

    public string SizeText => IsDirectory || Size < 0 ? "" : FileOps.FormatSize(Size);
    public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("g");
    public double Opacity => IsHidden ? 0.55 : 1.0;

    private string _typeName = "";
    public string TypeName { get => _typeName; set { _typeName = value; Raise(); } }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; Raise(); } }

    private bool _isEditing;
    public bool IsEditing { get => _isEditing; set { _isEditing = value; Raise(); } }

    private string _editName = "";
    public string EditName { get => _editName; set { _editName = value; Raise(); } }

    private bool _isCut;
    /// <summary>Dimmed while it sits on the clipboard as "cut".</summary>
    public bool IsCut { get => _isCut; set { _isCut = value; Raise(); Raise(nameof(ItemOpacity)); } }
    public double ItemOpacity => IsCut ? 0.45 : Opacity;

    private Brush? _labelBrush;
    public Brush? LabelBrush { get => _labelBrush; private set { _labelBrush = value; Raise(); Raise(nameof(HasLabel)); } }
    public bool HasLabel => _labelBrush != null;

    private string _tagsText = "";
    public string TagsText { get => _tagsText; private set { _tagsText = value; Raise(); } }

    private IReadOnlyList<string> _tags = Array.Empty<string>();
    /// <summary>Shown as small chips after the name (there is no Tags column).</summary>
    public IReadOnlyList<string> Tags { get => _tags; private set { _tags = value; Raise(); Raise(nameof(HasTags)); } }
    public bool HasTags => _tags.Count > 0;

    public void ApplyTags(Tools.TagEntry? entry)
    {
        var color = entry == null ? null : Tools.TagStore.Color(entry.Label);
        if (color == null) LabelBrush = null;
        else
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            b.Freeze();
            LabelBrush = b;
        }
        TagsText = entry == null ? "" : string.Join(", ", entry.Tags);
        Tags = entry == null || entry.Tags.Count == 0 ? Array.Empty<string>() : entry.Tags.ToList();
    }

    public void LoadShellInfo()
    {
        if (IsDrive)
        {
            Icon = ShellInfo.Icon(FullPath, true);
            return;
        }
        Icon = ShellInfo.Icon(FullPath, IsDirectory, assumeType: IsVirtual);
        TypeName = FileTypeNames.Short(FullPath, IsDirectory, ShellInfo.TypeName(FullPath, IsDirectory)); // "PDF", not "Microsoft Edge PDF Document"
    }
}
