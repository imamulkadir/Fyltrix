using System.Windows.Controls;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>"Open in Visual Studio Code / IntelliJ IDEA / …" for folders (the IDEs are set up in Settings → Context menu).</summary>
public sealed partial class MainWindow
{
    /// <summary>One "Open in X" item when a single IDE is set up, an "Open in IDE" submenu for several, nothing for none.</summary>
    internal MenuItem? IdeMenuItem(string folder)
    {
        var ides = IdeCatalog.MenuEntries(Settings.Ides);
        if (ides.Count == 0) return null;
        MenuItem Item(IdeEntry e, string header) =>
            Theme.ImageMenuItem(header, ShellInfo.Icon(e.ResolvedProgram!, false), () => OpenInIde(e, folder));
        if (ides.Count == 1) return Item(ides[0], "Open in " + ides[0].Name);
        return Theme.SubMenu("Open in IDE", "", ides.Select(e => (object)Item(e, e.Name)));
    }

    private void AddIdeMenuItem(ItemsControl menu, string folder)
    {
        if (IdeMenuItem(folder) is { } item) menu.Items.Add(item);
    }

    internal void OpenInIde(IdeEntry ide, string folder)
    {
        try
        {
            ide.Open(folder);
            var name = Path.GetFileName(folder.TrimEnd('\\'));
            SetStatus($"Opening '{(name.Length > 0 ? name : folder)}' in {ide.Name}…");
        }
        catch (InvalidOperationException ex) { Dialogs.Error(this, "Open in " + ide.Name, ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { Dialogs.Error(this, "Open in " + ide.Name, $"{ide.Name} couldn't be started: {ex.Message}"); }
    }
}
