using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.UI;

public sealed partial class MainWindow
{
    internal Func<int, string, string?>? NumberedRenamePromptForTest { get; set; }
    internal Action<string>? NumberedRenameErrorForTest { get; set; }
    private bool _renamingSelection;

    private void RenameSelection()
    {
        if (_renamingSelection || View is not { } view || !CanExecute("rename")) return;
        if (view.SelectedItems.Count == 1) { view.BeginRename(); return; }

        var selected = view.SelectedItems.ToHashSet();
        var ordered = view.List.Items.Cast<FileItem>().Where(selected.Contains).ToList();
        if (ordered.Count != selected.Count || ordered.Count < 2) return;
        string folder = view.CurrentPath;
        _renamingSelection = true;
        using var updateWork = TrackUpdateWork();
        try
        {
            string initial = ordered[0].IsDirectory ? ordered[0].Name : Path.GetFileNameWithoutExtension(ordered[0].Name);
            if (initial.Length == 0) initial = ordered[0].Name;
            string? name = NumberedRenamePromptForTest != null
                ? NumberedRenamePromptForTest(ordered.Count, initial)
                : Dialogs.Prompt(this, "Rename selected items",
                    $"Base name for {ordered.Count} items. Extensions are kept; numbering follows the visible order. Existing names are skipped.",
                    initial, NumberedRename.ValidateBaseName);
            if (name == null) return;
            if (_commandsClosed) return;
            // The prompt pumps messages: never redirect the captured selection to a
            // different folder, or proceed if navigation/closure made it invalid.
            if (view.CurrentPath != folder || !_tabs.Any(t => t.View == view) ||
                !Availability(view, ordered, executing: true).Rename)
            { RenameSelectionError("The original selection is no longer available for renaming."); return; }

            var plan = NumberedRename.Plan(ordered.Select(i => i.FullPath).ToArray(), name);
            if (plan.All(p => p.Status == RenameStatus.Unchanged)) { SetStatus("The selected names are already unchanged."); return; }
            ReleasePreviews(ordered.Select(i => i.FullPath));
            var result = BatchRename.Execute(plan);
            if (result.Errors.Count > 0)
            {
                RenameSelectionError("Renaming failed. Completed steps were rolled back where possible.\n" + string.Join("\n", result.Errors));
                _ = view.Refresh(ordered.Select(i => i.FullPath));
                return;
            }
            foreach (var (from, to) in result.Renamed) TagStore.Shared.Move(from, to);
            _undo.Push(new BatchRenameUndo(result.Renamed));
            _ = view.Refresh(plan.Select(p => p.Target));
            RefreshOtherViews(view, folder);
            SetStatus($"Renamed {result.Renamed.Count} item(s). Ctrl+Z to undo.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { RenameSelectionError(ex.Message); }
        finally { _renamingSelection = false; }
    }

    private void RenameSelectionError(string message)
    {
        SetStatus(message);
        if (NumberedRenameErrorForTest != null) NumberedRenameErrorForTest(message);
        else Dialogs.Error(this, "Rename", message);
    }
}
