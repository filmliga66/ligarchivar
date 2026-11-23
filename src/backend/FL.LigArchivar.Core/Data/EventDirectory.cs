using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using FL.LigArchivar.Core.Utilities;

namespace FL.LigArchivar.Core.Data;

public class EventDirectory : IFileSystemItem
{
    private readonly ILogger _logger;

    private EventDirectory(
        IDirectoryInfo directory,
        IFileSystemItem? parent,
        bool isValid,
        string clubChar,
        string year,
        string month,
        string day,
        string eventName,
        ILogger logger)
    {
        Name = directory.Name;
        Directory = directory;
        Parent = parent;
        IsValid = isValid;
        ClubChar = clubChar;
        Year = year;
        Month = month;
        Day = day;
        EventName = eventName;
        FilePrefix = clubChar + "_" + year + "-" + month + "-" + day + "_";
        Children = ImmutableList<DataFiles>.Empty;
        _logger = logger;
    }

    public string Name { get; }
    public bool IsValid { get; }
    public IFileSystemItem? Parent { get; }
    public IDirectoryInfo Directory { get; }
    public string ClubChar { get; }
    public string Year { get; }
    public string Month { get; }
    public string Day { get; }
    public string EventName { get; }
    public string FilePrefix { get; }
    public IImmutableList<DataFiles> Children { get; private set; }

    public void LoadChildren()
    {
        var files = Directory.GetFiles();
        var groups = new Dictionary<string, DataFiles>(StringComparer.OrdinalIgnoreCase);
        var children = ImmutableList.CreateBuilder<DataFiles>();

        foreach (var file in files)
        {
            var instance = new DataFile(file);
            if (!groups.TryGetValue(instance.Name, out var existing))
            {
                var dataFiles = new DataFiles(instance, this);
                groups.Add(instance.Name, dataFiles);
                children.Add(dataFiles);
            }
            else
            {
                existing.AddFile(instance);
            }
        }

        Children = children.ToImmutable();
    }

    public RenamePlan PlanRename(int startNumber, IReadOnlyList<string>? fileOrder = null)
    {
        var targets = new RenameTargets(Directory);
        var groups = GetOrderedChildren(fileOrder).Where(group => !group.IsIgnored);
        long number = startNumber;
        return new RenamePlan(Directory.FullName, groups.Select(group =>
        {
            var entries = group.PlanDestinations(group.IsDngOnly ? null : $"{FilePrefix}{number++:000}")
                .ToImmutableArray();
            targets.Reserve(entries);
            return entries;
        }).ToImmutableArray());
    }

    public RenamePlan PlanRenameByDateTime()
    {
        var targets = new RenameTargets(Directory);
        var groups = Children.Where(group => !group.IsIgnored).ToArray();
        // Different extensions at the same date base name would merge groups.
        // Keep existing base names occupied, even when their sources will move or be deleted.
        var usedNames = Children.Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = new Dictionary<DataFiles, ImmutableArray<RenameEntry>>();
        var byDate = new List<(DataFiles Group, string DateName)>();

        // Reserve all matching date names first, irrespective of filesystem order.
        foreach (var group in groups)
        {
            if (group.IsDngOnly)
            {
                planned.Add(group, group.PlanDestinations(null).ToImmutableArray());
                continue;
            }

            var dateName = group.GetDateBaseName();
            if (!IsMatchingDateName(group.Name, dateName))
            {
                byDate.Add((group, dateName));
                continue;
            }

            var entries = group.Files.Select(file => new RenameEntry(file.FullName, file.FullName)).ToImmutableArray();
            targets.Reserve(entries);
            planned.Add(group, entries);
        }

        foreach (var (group, dateName) in byDate)
        {
            for (var suffix = 1; ; suffix++)
            {
                var candidate = suffix == 1 ? dateName : FormattableString.Invariant($"{dateName}_{suffix}");
                var entries = group.PlanDestinations(candidate).ToImmutableArray();
                // Changing the base name can never fix duplicate destinations within a group.
                targets.EnsureDistinct(entries);
                if (usedNames.Contains(candidate) || !targets.TryReserve(entries, out _))
                    continue;

                usedNames.Add(candidate);
                planned.Add(group, entries);
                break;
            }
        }

        return new RenamePlan(Directory.FullName, groups.Select(group => planned[group]).ToImmutableArray());
    }

    public void ExecuteRename(RenamePlan plan, bool allowDngDeletion = false, ILogger? logger = null)
    {
        if (plan.DirectoryPath != Directory.FullName)
            throw new ArgumentException("The rename plan belongs to a different event.", nameof(plan));
        if (plan.HasDeletions && !allowDngDeletion)
            throw new RenameException("Das endgültige Löschen der DNG-Dateien muss bestätigt werden.");

        logger ??= _logger;
        var file = Directory.FileSystem.File;
        try
        {
            foreach (var group in plan.Groups)
            foreach (var entry in group)
            {
                if (entry.DestinationPath == entry.SourcePath)
                    continue;

                try
                {
                    if (entry.DestinationPath is null)
                    {
                        logger.LogInformation("Deleting file '{Source}'.", entry.SourcePath);
                        file.Delete(entry.SourcePath);
                    }
                    else
                    {
                        logger.LogInformation("Moving '{Source}' to '{Dest}'.", entry.SourcePath, entry.DestinationPath);
                        file.Move(entry.SourcePath, entry.DestinationPath, overwrite: false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new RenameExecutionException(entry.SourcePath, entry.DestinationPath, ex);
                }
            }
        }
        catch (RenameExecutionException)
        {
            try
            {
                LoadChildren();
            }
            catch (Exception reloadError) when (reloadError is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(reloadError, "Could not reload '{Event}' after failed rename.", Directory.FullName);
            }
            throw;
        }

        try
        {
            LoadChildren();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RenameExecutionException(Name, ex);
        }
    }

    private static bool IsMatchingDateName(string name, string dateName)
    {
        if (name == dateName)
            return true;

        // Canonical suffixes are decimal integers starting at 2, without leading zeros.
        if (!name.StartsWith(dateName + "_", StringComparison.Ordinal))
            return false;
        var suffix = name.AsSpan(dateName.Length + 1);
        return !suffix.IsEmpty && suffix[0] != '0' && !suffix.ContainsAnyExceptInRange('0', '9')
            && (suffix.Length > 1 || suffix[0] >= '2');
    }

    // Delegate-compatible overload used by FileSystemItemBase / ClubDirectory
    public static bool TryCreate(
        IDirectoryInfo eventDirectory,
        IFileSystemItem? parent,
        out IFileSystemItem directory)
        => TryCreate(eventDirectory, parent, out directory, null);

    public static bool TryCreate(
        IDirectoryInfo eventDirectory,
        IFileSystemItem? parent,
        out IFileSystemItem directory,
        ILogger? logger)
    {
        directory = null!;

        var name = eventDirectory.Name;
        var expectedClubChar = parent.GetClubChar();
        var expectedYear = parent.GetYear();

        var regex = new Regex(Patterns.EventDirectory);
        var match = regex.Match(name);

        if (!match.Success || match.Groups.Count != 6)
            return false;

        var actualClubChar = match.Groups[1].Value;
        var actualYear = match.Groups[2].Value;
        var actualMonth = match.Groups[3].Value;
        var actualDay = match.Groups[4].Value;
        var actualEventName = match.Groups[5].Value;

        var isValid = expectedClubChar == actualClubChar && expectedYear == actualYear;

        directory = new EventDirectory(
            eventDirectory, parent, isValid,
            actualClubChar, actualYear, actualMonth, actualDay, actualEventName,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return true;
    }

    private IEnumerable<DataFiles> GetOrderedChildren(IReadOnlyList<string>? fileOrder)
    {
        if (fileOrder == null || fileOrder.Count == 0)
            return Children;

        var lookup = Children.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<DataFiles>();

        foreach (var name in fileOrder)
        {
            if (name is not null && lookup.Remove(name, out var child))
                ordered.Add(child);
        }

        // Append any children not in fileOrder at the end
        foreach (var child in Children)
        {
            if (lookup.ContainsKey(child.Name))
                ordered.Add(child);
        }

        return ordered;
    }
}
