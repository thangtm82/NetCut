using System.Text.Json;
using NetCut.Models;

namespace NetCut.Services;

/// <summary>Snapshot based undo/redo for small and medium editor projects.</summary>
public sealed class ProjectHistory
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private const int Limit = 80;

    private static string Snapshot(ProjectDocument project) => JsonSerializer.Serialize(project);
    private static ProjectDocument Restore(string json) =>
        JsonSerializer.Deserialize<ProjectDocument>(json) ?? throw new InvalidDataException("Invalid history snapshot.");

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Record(ProjectDocument before)
    {
        _undo.Push(Snapshot(before));
        _redo.Clear();
        if (_undo.Count <= Limit) return;
        var last = _undo.Reverse().Take(Limit).Reverse().ToArray();
        _undo.Clear();
        foreach (var item in last) _undo.Push(item);
    }

    public ProjectDocument Undo(ProjectDocument current)
    {
        if (!CanUndo) return current;
        _redo.Push(Snapshot(current));
        return Restore(_undo.Pop());
    }

    public ProjectDocument Redo(ProjectDocument current)
    {
        if (!CanRedo) return current;
        _undo.Push(Snapshot(current));
        return Restore(_redo.Pop());
    }

    public void DiscardLast()
    {
        if (_undo.Count > 0) _undo.Pop();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
