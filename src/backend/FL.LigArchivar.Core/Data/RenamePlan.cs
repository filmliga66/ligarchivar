using System.Collections.Immutable;

namespace FL.LigArchivar.Core.Data;

/// <summary>A preflight-checked set of operations, in group execution order.</summary>
public sealed class RenamePlan
{
    internal RenamePlan(string directoryPath, ImmutableArray<ImmutableArray<RenameEntry>> groups)
    {
        DirectoryPath = directoryPath;
        Groups = groups;
    }

    internal string DirectoryPath { get; }
    internal ImmutableArray<ImmutableArray<RenameEntry>> Groups { get; }
    public IEnumerable<RenameEntry> Entries => Groups.SelectMany(group => group);
    public bool HasDeletions => Entries.Any(entry => entry.DestinationPath is null);
}

/// <summary>A null destination means deletion; an exact self-target means no change.</summary>
public sealed record RenameEntry(string SourcePath, string? DestinationPath);
