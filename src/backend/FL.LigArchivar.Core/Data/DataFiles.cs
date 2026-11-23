using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using FL.LigArchivar.Core.Utilities;

namespace FL.LigArchivar.Core.Data;

public class DataFiles
{
    private IImmutableList<DataFile> _files;

    public DataFiles(DataFile dataFile, EventDirectory parent)
    {
        Name = dataFile.Name;
        IsValid = GetIsValid(dataFile.Name, parent);
        Parent = parent;
        _files = ImmutableList.Create(dataFile);
    }

    public string Name { get; }

    public bool IsIgnored => _files.All(f => f.NameWithExtension == "Thumbs.db");

    public bool IsValid { get; }

    public bool IsDngOnly => _files.All(f => f.Extension.Equals(".dng", StringComparison.OrdinalIgnoreCase));

    public EventDirectory Parent { get; }

    public IImmutableList<DataFile> Files => _files;

    public void AddFile(DataFile file)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(file.Name, Name))
            throw new InvalidOperationException(
                $"Cannot add a file with name '{file.Name}' to the DataFiles with name '{Name}'.");

        _files = _files.Add(file);
    }

    internal IEnumerable<RenameEntry> PlanDestinations(string? baseName) => _files.Select(file =>
        new RenameEntry(file.FullName, baseName is null ? null : file.Directory.FileSystem.Path.Combine(
            file.Directory.FullName, baseName + file.Property + file.Extension)));

    internal string GetDateBaseName()
    {
        // Preserve the legacy, case-sensitive timestamp priority independently of grouping.
        var file = _files.FirstOrDefault(f => f.Extension == ".jpg")
            ?? _files.FirstOrDefault(f => f.Extension == ".mp3")
            ?? _files.FirstOrDefault(f => f.Extension == ".mts")
            ?? _files[0];
        return file.LastWriteTimeUtc.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
    }

    private static bool GetIsValid(string name, EventDirectory parent)
    {
        var regex = new Regex(Patterns.DataFile);
        var match = regex.Match(name);

        if (!match.Success || match.Groups.Count != 8)
            return false;

        var clubChar = match.Groups[1].Value;
        if (clubChar != parent.ClubChar) return false;

        var year = match.Groups[2].Value;
        if (year != parent.Year) return false;

        var month = match.Groups[3].Value;
        if (month != parent.Month) return false;

        var day = match.Groups[4].Value;
        if (day != parent.Day) return false;

        return true;
    }
}
