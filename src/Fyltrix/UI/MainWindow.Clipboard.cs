using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>Session clipboard history: the last file sets copied or cut in Fyltrix (not saved when Fyltrix closes).</summary>
public sealed partial class MainWindow
{
    internal sealed record ClipEntry(IReadOnlyList<string> Paths, bool Cut, DateTime When)
    {
        public string Title => Paths.Count == 1 ? Path.GetFileName(Paths[0].TrimEnd('\\')) : $"{Paths.Count} items ({Path.GetFileName(Paths[0].TrimEnd('\\'))}, …)";
    }

    private readonly List<ClipEntry> _clipHistory = new();
    internal IReadOnlyList<ClipEntry> ClipHistory => _clipHistory;

    private void CompleteCutPaste(string[] original, TransferResult result)
    {
        var moved = result.Moved.Select(p => p.from).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = original.Where(p => !moved.Contains(p) && FileOps.Exists(p)).ToArray();
        try
        {
            // Shell progress/conflict dialogs pump messages. Do not overwrite a new
            // clipboard selection made while this operation was in progress.
            var current = _clipboard.Read();
            if (current?.GetData(DataFormats.FileDrop) is not string[] paths ||
                !paths.SequenceEqual(original, StringComparer.OrdinalIgnoreCase) ||
                current.GetData("Preferred DropEffect") is not MemoryStream effect || effect.Length < 4)
                return;
            effect.Position = 0;
            var bytes = new byte[4];
            if (effect.Read(bytes, 0, 4) != 4 || (BitConverter.ToInt32(bytes, 0) & 2) == 0) return;
            if (remaining.Length == 0) _clipboard.Clear();
            else
            {
                var pending = new DataObject();
                var files = new StringCollection();
                files.AddRange(remaining);
                pending.SetFileDropList(files);
                pending.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(2)));
                _clipboard.Write(pending);
            }
            _cutPaths.Clear();
            _cutPaths.UnionWith(remaining);
            if (remaining.Length > 0)
                SetStatus(_statusRight.Text + $" {remaining.Length} item{(remaining.Length == 1 ? " remains" : "s remain")} cut.");
        }
        catch (Exception ex) { SetStatus("Move finished; couldn't update the clipboard: " + ex.Message); }
        finally
        {
            _cutPaths.RemoveWhere(p => !FileOps.Exists(p));
            foreach (var tab in _tabs) MarkCut(tab.View);
            RefreshClipboardState();
        }
    }

    private void RememberClipboard(List<string> paths, bool cut)
    {
        _clipHistory.RemoveAll(e => e.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase));
        _clipHistory.Insert(0, new ClipEntry(paths, cut, DateTime.Now));
        if (_clipHistory.Count > 15) _clipHistory.RemoveAt(15);
    }

    /// <summary>Puts an earlier set back on the clipboard (as a copy, so pasting never moves files unexpectedly).</summary>
    internal void RestoreClipboard(ClipEntry e)
    {
        var existing = e.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (existing.Length == 0) { SetStatus("Those items no longer exist."); return; }
        var sc = new StringCollection();
        sc.AddRange(existing);
        var data = new DataObject();
        data.SetFileDropList(sc);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(5)));
        try { _clipboard.Write(data); RefreshClipboardState(); }
        catch (System.Runtime.InteropServices.ExternalException ex) { SetStatus("Clipboard is busy: " + ex.Message); return; }
        _cutPaths.Clear();
        foreach (var t in _tabs) MarkCut(t.View);
        SetStatus($"On the clipboard again: {e.Title}. Paste with Ctrl+V.");
    }

    private object ClipboardHistoryMenu()
    {
        var items = _clipHistory.Select(e => (object)Theme.MenuItem($"{e.Title}  ·  {e.When:t}{(e.Cut ? " (was cut)" : "")}", null, () => RestoreClipboard(e))).ToList();
        if (items.Count == 0) items.Add(Theme.MenuItem("Nothing copied yet in this session", null, () => { }, enabled: false));
        return Theme.SubMenu("Clipboard history", "", items);
    }
}
