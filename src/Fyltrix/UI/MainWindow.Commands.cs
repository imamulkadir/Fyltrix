using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Fyltrix.Core;

namespace Fyltrix.UI;

public sealed partial class MainWindow
{
    internal static Func<IFileClipboard> ClipboardFactory = () => new WindowsFileClipboard();
    private readonly IFileClipboard _clipboard = ClipboardFactory();
    private bool _clipboardFiles, _clipboardImage, _destinationWritable, _commandsClosed;
    private string? _destinationChecked;
    private int _destinationVersion;
    private HwndSource? _clipboardSource;
    private bool _clipboardListening;
    private readonly System.Windows.Threading.DispatcherTimer _clipboardRetry = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private int _clipboardRetries;

    private void StartClipboardNotifications()
    {
        _clipboardRetry.Tick += (_, _) => { _clipboardRetry.Stop(); RefreshClipboardState(retry: true); };
        _clipboardSource = HwndSource.FromHwnd(Hwnd);
        _clipboardSource?.AddHook(ClipboardHook);
        _clipboardListening = AddClipboardFormatListener(Hwnd);
        RefreshClipboardState();
    }

    private void StopClipboardNotifications()
    {
        _commandsClosed = true;
        _clipboardRetry.Stop();
        ++_destinationVersion;
        if (_clipboardListening && _clipboardSource != null) RemoveClipboardFormatListener(_clipboardSource.Handle);
        _clipboardSource?.RemoveHook(ClipboardHook);
        _clipboardSource = null;
    }

    private IntPtr ClipboardHook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x031D) RefreshClipboardState(); // WM_CLIPBOARDUPDATE, no polling
        return IntPtr.Zero;
    }

    internal void RefreshClipboardState(bool retry = false)
    {
        if (_commandsClosed) return;
        if (!retry) { _clipboardRetry.Stop(); _clipboardRetries = 3; }
        try
        {
            var data = _clipboard.Read();
            _clipboardFiles = data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };
            _clipboardImage = data != null && ClipboardImageFile.HasImage(data);
        }
        catch
        {
            _clipboardFiles = _clipboardImage = false;
            if (_clipboardRetries-- > 0) _clipboardRetry.Start(); // bounded retry for another app holding the clipboard
        }
        UpdateToolbarStates();
    }

    private async void CheckDestination()
    {
        var view = View;
        var path = view?.CurrentPath;
        int version = ++_destinationVersion;
        _destinationChecked = path;
        _destinationWritable = false;
        UpdateToolbarStates();
        bool writable = view?.CanModify == true && path != null && await Task.Run(() => FileAccessPolicy.WritableDirectory(path));
        if (_commandsClosed || version != _destinationVersion || View != view || View?.CurrentPath != path) return;
        _destinationWritable = writable;
        UpdateToolbarStates();
    }

    internal sealed record CommandState(bool New, bool Cut, bool Copy, bool Paste, bool Rename, bool Delete,
        bool QuickView, bool Compare, bool View, string CompareReason)
    {
        internal bool Allows(string id) => id switch
        {
            "new" or "newfolder" => New, "cut" => Cut, "copy" => Copy, "paste" => Paste,
            "rename" => Rename, "delete" => Delete, "quickview" => QuickView, "compare" => Compare,
            "view" or "sort" => View, _ => true,
        };
    }

    internal CommandState Availability(FolderView? view = null, IList<FileItem>? selection = null, bool executing = false)
    {
        view ??= View;
        var sel = selection ?? view?.SelectedItems ?? new List<FileItem>();
        bool archive = view?.IsArchive == true;
        bool valid = sel.Count > 0 && sel.All(i => !i.IsDrive && (i.IsVirtual
            ? archive && File.Exists(view!.ArchiveFile)
            : (view?.IsSearchResults == true || view != null && AppSettings.SameFolder(i.Folder, view.CurrentPath))
                && (i.IsDirectory ? Directory.Exists(i.FullPath) : File.Exists(i.FullPath))));
        bool real = valid && !archive && sel.All(i => !i.IsVirtual);
        bool mutable = real && sel.All(i => FileAccessPolicy.Deletable(i.FullPath));
        bool readable = valid && (archive || sel.All(i => FileAccessPolicy.Readable(i.FullPath)));
        bool destination = view?.CanModify == true && (executing
            ? FileAccessPolicy.WritableDirectory(view.CurrentPath)
            : view == View && _destinationChecked == view.CurrentPath && _destinationWritable);
        bool quick = readable && sel.Count == 1 && !sel[0].IsDirectory
            && (sel[0].IsVirtual || SupportsQuickView(sel[0].FullPath));
        string reason = sel.Count != 2 ? "Select exactly two compatible files to compare."
            : sel.Any(i => i.IsDirectory) ? "Compare requires files. Use Tools → Compare folders for folders."
            : !real || !readable ? "Both files must be available and readable; extract archive entries first."
            : !CompareWindow.Compatible(sel[0].FullPath, sel[1].FullPath) ? "These files use incompatible comparison formats." : "";
        bool location = view != null && (view.IsThisPC || view.IsArchive && File.Exists(view.ArchiveFile) || Directory.Exists(view.CurrentPath));
        return new(destination, mutable, readable, destination && (_clipboardFiles || _clipboardImage),
            mutable && sel.Count == 1, mutable, quick, reason.Length == 0, location, reason);
    }

    private bool CanExecute(string id)
    {
        if (id is not ("new" or "newfolder" or "cut" or "copy" or "paste" or "rename" or "delete" or "quickview" or "compare" or "view" or "sort")) return true;
        if (id == "paste") RefreshClipboardState();
        return Availability(executing: true).Allows(id);
    }

    private void RenameSelection()
    {
        if (CanExecute("rename")) View?.BeginRename();
    }

    private static bool SupportsQuickView(string path)
    {
        var kind = FileKinds.KindFor(path, false);
        return kind switch
        {
            PreviewKind.None => false,
            PreviewKind.Auto => true, // the existing viewer handles unknown binaries as hex
            PreviewKind.ShellHandler => Fyltrix.Preview.ShellPreviewHost.FindHandler(path) != null,
            _ => true,
        };
    }

    // Used by the real keyboard handler as well as off-screen tests; no simulated desktop input is needed.
    internal bool HandleFileShortcut(System.Windows.Input.Key key, bool ctrl, bool shift)
    {
        if (ctrl && shift && key == System.Windows.Input.Key.N) NewFolder();
        else if (ctrl && !shift && key == System.Windows.Input.Key.C) CopySelection(false);
        else if (ctrl && key == System.Windows.Input.Key.X) CopySelection(true);
        else if (ctrl && key == System.Windows.Input.Key.V) Paste();
        else if (ctrl && key == System.Windows.Input.Key.D) CompareSelection();
        else if (key == System.Windows.Input.Key.F2)
        {
            if (View?.SelectedItems.Count > 1 && CanExecute("delete")) BatchRenameSelection();
            else RenameSelection();
        }
        else if (key == System.Windows.Input.Key.Delete) DeleteSelection(shift);
        else if (key == System.Windows.Input.Key.Space && !ctrl) ToggleQuickView(null);
        else return false;
        return true;
    }

    private void UpdateToolbarStates()
    {
        if (_commandsClosed || View == null) return;
        var state = Availability();
        foreach (var (id, _, element) in _toolbar)
            if (id != null && element is Button button)
            {
                button.IsEnabled = id == "undo" ? _undo.CanUndo : state.Allows(id);
                if (id == "compare") button.ToolTip = state.Compare ? "Compare two files (Ctrl+D)" : state.CompareReason;
            }
        // Toolbar customization offers actions, not navigation places. Avoid disk/drive queries on each selection/search batch.
        var extraCommands = _toolbarExtras.Children.Count == 0 ? null : PaletteEntries("", state, includePlaces: false).Select(e => e.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Button button in _toolbarExtras.Children)
            if (button.Tag is string title)
                button.IsEnabled = extraCommands!.Contains(title);
    }

    private void ApplyMenuAvailability(ItemsControl menu, FolderView view, IList<FileItem> selection, CommandState? sharedState = null)
    {
        var state = sharedState ?? Availability(view, selection, executing: true);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            string header = item.Header as string ?? "";
            var id = ContextMenuActions.IdFor(header);
            if (id is "new" or "cut" or "copy" or "paste" or "rename" or "delete" or "quickview" or "compare")
            {
                // Folder comparison remains a separate Tools action.
                if (header == "Compare these folders") continue;
                item.IsEnabled = header == "Paste into folder" && selection.Count == 1
                    ? (_clipboardFiles || _clipboardImage) && FileAccessPolicy.WritableDirectory(selection[0].FullPath)
                    : state.Allows(id);
                if (id == "compare") item.ToolTip = state.CompareReason;
                ToolTipService.SetShowOnDisabled(item, true);
            }
            if (header is "New folder" or "New") item.IsEnabled = state.New;
            if (header == "Delete permanently") item.IsEnabled = state.Delete;
            if (item.HasItems) ApplyMenuAvailability(item, view, selection, state);
        }
    }

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
