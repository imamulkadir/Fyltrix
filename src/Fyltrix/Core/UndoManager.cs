namespace Fyltrix.Core;

public interface IUndoAction
{
    string Description { get; }
    /// <summary>Reverts the action. Returns an error message, or null on success.</summary>
    string? Undo(IntPtr owner);
    /// <summary>Paths touched by the undo, so the views can refresh.</summary>
    IEnumerable<string> AffectedFolders { get; }
}

public sealed class UndoManager
{
    private readonly Stack<IUndoAction> _stack = new();
    private const int Limit = 100;

    public event EventHandler? Changed;

    public bool CanUndo => _stack.Count > 0;
    public string? NextDescription => _stack.Count > 0 ? _stack.Peek().Description : null;

    public void Push(IUndoAction action)
    {
        _stack.Push(action);
        if (_stack.Count > Limit)
        {
            var keep = _stack.Take(Limit).Reverse().ToList();
            _stack.Clear();
            foreach (var a in keep) _stack.Push(a);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public (IUndoAction? action, string? error) Undo(IntPtr owner)
    {
        if (_stack.Count == 0) return (null, null);
        var action = _stack.Pop();
        string? error;
        try { error = action.Undo(owner); }
        catch (Exception ex) { error = ex.Message; }
        Changed?.Invoke(this, EventArgs.Empty);
        return (action, error);
    }
}

/// <summary>Undo for a copy or a new item: send the created items to the Recycle Bin.</summary>
public sealed class CreatedUndo : IUndoAction
{
    private readonly List<string> _created;
    private readonly List<(string original, DateTime when)> _replaced;
    public string Description { get; }

    public CreatedUndo(string description, IEnumerable<string> created, IEnumerable<(string, DateTime)>? replaced = null)
    {
        Description = description;
        _created = created.ToList();
        _replaced = replaced?.ToList() ?? new();
    }

    public IEnumerable<string> AffectedFolders => _created.Select(p => Path.GetDirectoryName(p) ?? p).Distinct();

    public string? Undo(IntPtr owner)
    {
        // Locate the replaced originals BEFORE recycling the copies (they share the same path).
        var entries = _replaced.Select(r => (r.original, entry: RecycleBin.Find(r.original, r.when.AddSeconds(-5)))).ToList();
        var existing = _created.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count > 0 && !FileOps.Delete(existing, permanent: false, owner, silent: true))
            return "Undo was cancelled.";
        var errors = entries.Select(e => RecycleBin.Restore(e.original, e.entry)).Where(e => e != null).ToList();
        return errors.Count > 0 ? string.Join("\n", errors) : null;
    }
}

/// <summary>Undo for a move: move every item back.</summary>
public sealed class MoveUndo : IUndoAction
{
    private readonly List<(string from, string to)> _moves;
    private readonly List<(string original, DateTime when)> _replaced;
    public string Description { get; }

    public MoveUndo(string description, IEnumerable<(string from, string to)> moves, IEnumerable<(string, DateTime)>? replaced = null)
    {
        Description = description;
        _moves = moves.ToList();
        _replaced = replaced?.ToList() ?? new();
    }

    public IEnumerable<string> AffectedFolders =>
        _moves.SelectMany(m => new[] { Path.GetDirectoryName(m.from), Path.GetDirectoryName(m.to) })
              .Where(p => p != null).Select(p => p!).Distinct();

    public string? Undo(IntPtr owner)
    {
        var back = _moves.Where(m => File.Exists(m.to) || Directory.Exists(m.to)).ToList();
        if (back.Count > 0 && !FileOps.RunMulti(Native.FO_MOVE, back.Select(m => m.to).ToList(), back.Select(m => m.from).ToList(), owner))
            return "Undo was cancelled.";
        var errors = _replaced.Select(r => RecycleBin.Restore(r.original, r.when)).Where(e => e != null).ToList();
        return errors.Count > 0 ? string.Join("\n", errors) : null;
    }
}

public sealed class RenameUndo : IUndoAction
{
    private readonly string _oldPath, _newPath;
    public string Description { get; }

    public RenameUndo(string oldPath, string newPath)
    {
        _oldPath = oldPath;
        _newPath = newPath;
        Description = $"Rename '{Path.GetFileName(newPath)}' back to '{Path.GetFileName(oldPath)}'";
    }

    public IEnumerable<string> AffectedFolders => new[] { Path.GetDirectoryName(_oldPath) ?? _oldPath };

    public string? Undo(IntPtr owner)
    {
        try { FileOps.Rename(_newPath, Path.GetFileName(_oldPath)); return null; }
        catch (Exception ex) { return ex.Message; }
    }
}

/// <summary>Undo for a batch rename: every item gets its old name back.</summary>
public sealed class BatchRenameUndo : IUndoAction
{
    private readonly List<(string from, string to)> _renamed;
    public string Description { get; }

    public BatchRenameUndo(IEnumerable<(string from, string to)> renamed)
    {
        _renamed = renamed.ToList();
        Description = $"Rename {_renamed.Count} item{(_renamed.Count == 1 ? "" : "s")} back";
    }

    public IEnumerable<string> AffectedFolders => _renamed.Select(r => Path.GetDirectoryName(r.from) ?? r.from).Distinct();

    public string? Undo(IntPtr owner)
    {
        var errors = Tools.BatchRename.Revert(_renamed);
        return errors.Count > 0 ? string.Join("\n", errors) : null;
    }
}

/// <summary>Undo for a delete to the Recycle Bin: restore each item to its original path.</summary>
public sealed class DeleteUndo : IUndoAction
{
    private readonly List<string> _paths;
    private readonly DateTime _whenUtc;
    public string Description { get; }

    public DeleteUndo(IEnumerable<string> paths, DateTime whenUtc)
    {
        _paths = paths.ToList();
        _whenUtc = whenUtc;
        Description = _paths.Count == 1 ? $"Restore '{Path.GetFileName(_paths[0])}'" : $"Restore {_paths.Count} deleted items";
    }

    public IEnumerable<string> AffectedFolders => _paths.Select(p => Path.GetDirectoryName(p) ?? p).Distinct();

    public string? Undo(IntPtr owner)
    {
        var errors = _paths.Select(p => RecycleBin.Restore(p, _whenUtc)).Where(e => e != null).ToList();
        return errors.Count > 0 ? string.Join("\n", errors) : null;
    }
}
