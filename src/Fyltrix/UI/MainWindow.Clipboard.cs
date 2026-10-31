using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

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
