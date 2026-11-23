using System.Collections.Immutable;
using System.IO.Abstractions;

namespace FL.LigArchivar.Core.Data;

internal sealed class RenameTargets
{
    private record Occupant(string Path, bool IsDirectory);

    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private readonly IPath _path;
    private readonly ILookup<string, Occupant> _occupied;
    private readonly Dictionary<string, RenameEntry> _reserved = new(Comparer);

    internal RenameTargets(IDirectoryInfo directory)
    {
        _path = directory.FileSystem.Path;
        _occupied = directory.EnumerateFileSystemInfos()
            .Select(info => new Occupant(info.FullName, info is IDirectoryInfo))
            .ToLookup(entry => entry.Path, Comparer);
    }

    internal void EnsureDistinct(ImmutableArray<RenameEntry> entries)
    {
        var seen = new Dictionary<string, RenameEntry>(Comparer);
        foreach (var entry in entries)
        {
            if (entry.DestinationPath is not { } target)
                continue;
            if (seen.TryGetValue(target, out var other))
                throw new RenameException(DuplicateTarget(entry, other));
            seen.Add(target, entry);
        }
    }

    internal void Reserve(ImmutableArray<RenameEntry> entries)
    {
        EnsureDistinct(entries);
        if (!TryReserve(entries, out var conflict))
            throw new RenameException(conflict!);
    }

    // The caller has already checked for duplicate targets within this group.
    internal bool TryReserve(ImmutableArray<RenameEntry> entries, out string? conflict)
    {
        foreach (var entry in entries)
        {
            if (entry.DestinationPath is not { } target)
                continue;
            if (_reserved.TryGetValue(target, out var other))
            {
                conflict = DuplicateTarget(entry, other);
                return false;
            }
            foreach (var occupant in _occupied[target])
            {
                if (!occupant.IsDirectory && target == entry.SourcePath && target == occupant.Path)
                    continue;
                var type = occupant.IsDirectory ? "Verzeichnis" : "Datei";
                conflict = $"'{Name(entry.SourcePath)}' kann nicht nach '{Name(target)}' umbenannt werden: "
                    + $"{type} '{Name(occupant.Path)}' belegt das Ziel bereits.";
                return false;
            }
        }

        foreach (var entry in entries)
        {
            if (entry.DestinationPath is { } target)
                _reserved.Add(target, entry);
        }
        conflict = null;
        return true;
    }

    private string DuplicateTarget(RenameEntry entry, RenameEntry other) =>
        $"Ziel '{Name(entry.DestinationPath!)}' ist doppelt geplant: '{Name(other.SourcePath)}' und '{Name(entry.SourcePath)}'.";

    private string Name(string path) => _path.GetFileName(path);
}
