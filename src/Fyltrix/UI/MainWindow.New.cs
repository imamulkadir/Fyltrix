using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fyltrix.Core;
using Fyltrix.Core.Templates;

namespace Fyltrix.UI;

/// <summary>The New menu: folder, built-in and custom templates with the shell's own file-type icons, and Windows' registered ShellNew types.</summary>
public sealed partial class MainWindow
{
    private List<FileTemplate> _templates = FileTemplates.Load();
    private Task<List<ShellNewEntry>>? _shellNew;

    private void StartShellNewDiscovery()
    {
        var known = _templates.Select(t => t.Extension).ToList();
        _shellNew = Task.Run(() => FileTemplates.DiscoverShellNew(known));
    }

    private static System.Windows.Media.ImageSource? TypeIcon(string extension) =>
        ShellInfo.Icon("file" + extension, false, assumeType: true);

    /// <summary>Items for a "New" submenu (or the toolbar dropdown).</summary>
    private List<object> NewMenuItems()
    {
        var items = new List<object>
        {
            Theme.ImageMenuItem("Folder", ShellInfo.Icon(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), true), NewFolder, "Ctrl+Shift+N"),
        };
        string? lastCategory = null;
        foreach (var t in _templates.Where(t => t.Enabled))
        {
            if (t.Category != lastCategory) { items.Add(new Separator()); lastCategory = t.Category; }
            var template = t;
            var mi = Theme.ImageMenuItem($"{t.Name}", TypeIcon(t.Extension), () => CreateFromTemplate(template), t.Extension);
            items.Add(mi);
        }
        var windowsTypes = WindowsNewTypes();
        if (windowsTypes.Count > 0)
        {
            var hidden = new HashSet<string>(Settings.HiddenNewTypes, StringComparer.OrdinalIgnoreCase);
            var others = windowsTypes.Where(e => !hidden.Contains(e.Extension))
                .Select(e => (object)Theme.ImageMenuItem(e.Name, TypeIcon(e.Extension), () => CreateFromShellNew(e), e.Extension)).ToList();
            if (others.Count > 0)
            {
                items.Add(new Separator());
                items.Add(Theme.SubMenu("Other types (from Windows)", "", others));
            }
        }
        items.Add(new Separator());
        items.Add(Theme.MenuItem("Manage templates…", "", ManageTemplates));
        return items;
    }

    /// <summary>Windows' registered New types that no Fyltrix template covers (a hidden template still covers its type),
    /// i.e. the "Other types (from Windows)" submenu before the user's hidden list is applied. Empty while discovery runs.</summary>
    internal List<ShellNewEntry> WindowsNewTypes()
    {
        if (_shellNew is not { IsCompletedSuccessfully: true }) return new List<ShellNewEntry>();
        var custom = new HashSet<string>(_templates.Select(t => t.Extension), StringComparer.OrdinalIgnoreCase);
        return _shellNew.Result.Where(e => !custom.Contains(e.Extension)).ToList();
    }

    /// <summary>Re-reads templates.json after the templates or settings window changed it, so the next menu shows the change.</summary>
    internal void ReloadTemplates() => _templates = FileTemplates.Load();

    private void ShowNewMenu(UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var i in NewMenuItems()) menu.Items.Add(i);
        menu.IsOpen = true;
    }

    internal async void CreateFromTemplate(FileTemplate t)
    {
        var view = View;
        if (view == null || !CanExecute("new")) { SetStatus("Open a writable folder first."); return; }
        var dir = view.CurrentPath;
        string? name = null;
        if (t.NeedsNameFirst)
        {
            name = Dialogs.Prompt(this, "New " + t.Name, "File name", t.DefaultFileName, n =>
                FileOps.ValidateName(n.Trim()) ?? (FileOps.Exists(Path.Combine(dir, n.Trim())) ? $"'{n.Trim()}' already exists here." : null));
            if (name == null) return;
            name = name.Trim();
            if (!name.EndsWith(t.Extension, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(name).Length == 0) name += t.Extension;
        }
        try
        {
            var path = FileTemplates.Create(dir, t, name);
            _undo.Push(new CreatedUndo($"Create '{Path.GetFileName(path)}'", new[] { path }));
            await view.Refresh(new[] { path });
            if (name == null) view.BeginRename();
            else SetStatus($"Created '{Path.GetFileName(path)}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(this, "New file", ex.Message);
        }
    }

    private async void CreateFromShellNew(ShellNewEntry e)
    {
        var view = View;
        if (view == null || !CanExecute("new")) return;
        try
        {
            var path = FileTemplates.CreateFromShellNew(view.CurrentPath, e);
            _undo.Push(new CreatedUndo($"Create '{Path.GetFileName(path)}'", new[] { path }));
            await view.Refresh(new[] { path });
            view.BeginRename();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(this, "New file", ex.Message);
        }
    }

    private void ManageTemplates()
    {
        var w = new TemplatesWindow(this, _templates);
        if (w.ShowDialog() != true) return;
        _templates = FileTemplates.Load();
        StartShellNewDiscovery();
        SetStatus("Templates saved.");
    }
}
