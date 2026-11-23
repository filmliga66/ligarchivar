using System.IO.Abstractions.TestingHelpers;
using FL.LigArchivar.Core.Data;
using Xunit;

namespace FL.LigArchivar.Core.Tests.Data;

public class EventDirectoryTests
{
    private const string EventPath = "Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung";
    private const string EventFolder = "/archiv/" + EventPath;
    private static readonly DateTime TenUtc = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
    private readonly ArchiveRoot _root;

    [Theory]
    [InlineData("A_2018-05-01_011.jpg", false)]
    [InlineData("a_2018-05-01_011.JPG", false)]
    [InlineData("a_2018-05-01_011.JPG", true)]
    public void PlanRename_LateOccupiedTarget_LeavesAllPathsAndContentsUnchanged(string occupant, bool directory)
    {
        var sources = new Dictionary<string, string>
        {
            ["raw.dng"] = "raw",
            ["first.jpg"] = "first",
            ["second.jpg"] = "second",
        };
        if (!directory)
            sources.Add(occupant, "occupant");
        var (fs, eventDirectory) = CreateEvent(sources);
        if (directory)
            fs.AddDirectory($"{EventFolder}/{occupant}");

        var plan = () => eventDirectory.PlanRename(10, ["raw", "first", "second"]);

        var entryType = directory ? "Verzeichnis" : "Datei";
        plan.Should().Throw<RenameException>().WithMessage($"*second.jpg*A_2018-05-01_011.jpg*{entryType} '{occupant}'*");
        Snapshot(fs).Should().BeEquivalentTo(sources);
        fs.Directory.GetDirectories(EventFolder).Select(path => fs.Path.GetFileName(path))
            .Should().BeEquivalentTo(directory ? new[] { occupant } : []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Planning_LateDuplicateTargets_LeavesAllPathsAndContentsUnchanged(bool byDate)
    {
        var sources = new Dictionary<string, string>
        {
            ["raw.dng"] = "raw",
            ["first.jpg"] = "first",
            ["other_bearbeitet.jpg"] = "other",
            ["Other_BEARBEITET_extra.jpg"] = "duplicate",
        };
        var (fs, eventDirectory) = CreateEvent(sources);
        var plan = () => byDate ? eventDirectory.PlanRenameByDateTime() : eventDirectory.PlanRename(10);

        plan.Should().Throw<RenameException>().WithMessage("*doppelt geplant*other_bearbeitet.jpg*Other_BEARBEITET_extra.jpg*");
        Snapshot(fs).Should().BeEquivalentTo(sources);
    }

    [Fact]
    public void PlanRename_RejectsCaseOnlySelfTarget()
    {
        var sources = new Dictionary<string, string> { ["a_2018-05-01_010.jpg"] = "photo" };
        var (fs, eventDirectory) = CreateEvent(sources);

        var plan = () => eventDirectory.PlanRename(10);

        plan.Should().Throw<RenameException>().WithMessage("*a_2018-05-01_010.jpg*A_2018-05-01_010.jpg*belegt*");
        Snapshot(fs).Should().BeEquivalentTo(sources);
    }

    [Fact]
    public void PlanRename_ExactSelfTargetCannotShareItsDestination()
    {
        var sources = new Dictionary<string, string>
        {
            ["A_2018-05-01_010_bearbeitet.jpg"] = "already named",
            ["A_2018-05-01_010_BEARBEITET_extra.JPG"] = "other source",
        };
        var (fs, eventDirectory) = CreateEvent(sources);

        var plan = () => eventDirectory.PlanRename(10);

        plan.Should().Throw<RenameException>().WithMessage("*doppelt geplant*010_bearbeitet.jpg*010_BEARBEITET_extra.JPG*");
        Snapshot(fs).Should().BeEquivalentTo(sources);
    }

    [Fact]
    public void Rename_UsesFirstCaseInsensitiveFileOrderEntryAndOnlyDeletesPureDngGroups()
    {
        var (fs, eventDirectory) = CreateEvent(new Dictionary<string, string>
        {
            ["Film.JPG"] = "jpeg",
            ["film.dng"] = "raw with jpeg",
            ["Scan.DNG"] = "raw with png",
            ["scan.png"] = "png",
            ["orphan.DNG"] = "pure raw",
            ["ORPHAN_bearbeitet.dng"] = "pure raw edit",
            ["A_2018-05-01_012.jpg"] = "already correctly numbered",
        });

        var plan = eventDirectory.PlanRename(10, ["orphan", "missing", "SCAN", "scan", "film"]);
        Snapshot(fs).Should().HaveCount(7);
        eventDirectory.ExecuteRename(plan, allowDngDeletion: true);

        Snapshot(fs).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["A_2018-05-01_010.DNG"] = "raw with png",
            ["A_2018-05-01_010.png"] = "png",
            ["A_2018-05-01_011.JPG"] = "jpeg",
            ["A_2018-05-01_011.dng"] = "raw with jpeg",
            ["A_2018-05-01_012.jpg"] = "already correctly numbered",
        });
    }

    [Fact]
    public void Rename_DateUsesFirstFreeSuffixAndKeepsExistingNamesBeforeAssigningNewOnes()
    {
        var (fs, eventDirectory) = CreateEvent(new Dictionary<string, string>
        {
            ["Scan.DNG"] = "raw with png",
            ["scan.png"] = "png",
            ["second.mp3"] = "mp3",
            ["2026-06-01_10-00-00_4.jpg"] = "already named",
            ["2026-06-01_10-00-00_4_BEARBEITET.JPG"] = "already named edit",
            ["2026-06-01_10-00-00.jpg"] = "will move away",
        });
        fs.AddDirectory($"{EventFolder}/2026-06-01_10-00-00_2.PNG");
        fs.File.SetLastWriteTimeUtc($"{EventFolder}/2026-06-01_10-00-00.jpg", TenUtc.AddHours(1));
        eventDirectory.LoadChildren();

        eventDirectory.ExecuteRename(eventDirectory.PlanRenameByDateTime());

        Snapshot(fs).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["2026-06-01_10-00-00_3.DNG"] = "raw with png",
            ["2026-06-01_10-00-00_3.png"] = "png",
            ["2026-06-01_10-00-00_2.mp3"] = "mp3",
            ["2026-06-01_10-00-00_4.jpg"] = "already named",
            ["2026-06-01_10-00-00_4_BEARBEITET.JPG"] = "already named edit",
            ["2026-06-01_11-00-00.jpg"] = "will move away",
        });
    }

    [Fact]
    public void Rename_DateKeepsLegacyTimestampPriorityWhenExtensionsAreUppercase()
    {
        var (fs, eventDirectory) = CreateEvent(new Dictionary<string, string>
        {
            ["Film.JPG"] = "photo",
            ["film.mp3"] = "audio",
            ["FILM.dng"] = "raw",
        });
        fs.File.SetLastWriteTimeUtc($"{EventFolder}/film.mp3", TenUtc.AddHours(1));
        eventDirectory.LoadChildren();

        eventDirectory.ExecuteRename(eventDirectory.PlanRenameByDateTime());

        Snapshot(fs).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["2026-06-01_11-00-00.JPG"] = "photo",
            ["2026-06-01_11-00-00.mp3"] = "audio",
            ["2026-06-01_11-00-00.dng"] = "raw",
        });
    }

    [Fact]
    public void Rename_IncomingJpegDoesNotChangeTheDngDeletionDecision()
    {
        var (fs, eventDirectory) = CreateEvent(new Dictionary<string, string>
        {
            ["photo.jpg"] = "photo",
            ["A_2018-05-01_010.dng"] = "pure raw",
        });

        eventDirectory.ExecuteRename(eventDirectory.PlanRename(10), allowDngDeletion: true);

        Snapshot(fs).Should().BeEquivalentTo(new Dictionary<string, string> { ["A_2018-05-01_010.jpg"] = "photo" });
    }

    private static (MockFileSystem FileSystem, EventDirectory Event) CreateEvent(Dictionary<string, string> sources)
    {
        var fs = new MockFileSystem(sources.ToDictionary(f => $"{EventFolder}/{f.Key}",
            f => new MockFileData(f.Value) { LastWriteTime = TenUtc }));
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var eventDirectory = (EventDirectory)root!.GetChild(EventPath)!;
        eventDirectory.LoadChildren();
        return (fs, eventDirectory);
    }

    private static Dictionary<string, string> Snapshot(MockFileSystem fs) =>
        fs.Directory.GetFiles(EventFolder).ToDictionary(path => fs.Path.GetFileName(path)!, fs.File.ReadAllText);

    public EventDirectoryTests()
    {
        var fs = FileSystemForTesting.Create();
        var created = ArchiveRoot.TryCreate("/archiv", fs, out _root!);
        created.Should().BeTrue();
        _root.Should().NotBeNull();
    }


    [Fact]
    public void LoadChildren_ReturnsCorrectFileCount()
    {
        // Arrange
        var uut = _root.GetChild("Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung")
            as EventDirectory;
        uut.Should().NotBeNull();

        // Act
        uut!.LoadChildren();

        // Assert
        uut.Children.Should().HaveCount(5);
    }

    [Fact]
    public void Rename_RenumbersFilesFromGivenStart()
    {
        // Arrange
        var uut = _root.GetChild("Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung")
            as EventDirectory;
        uut.Should().NotBeNull();
        uut!.LoadChildren();

        // Act
        uut.ExecuteRename(uut.PlanRename(10));

        // Assert
        uut.Children.Should().HaveCount(5);
        uut.Children[0].Name.Should().Be("A_2018-05-01_010");
        uut.Children[4].Name.Should().Be("A_2018-05-01_014");
    }

    [Fact]
    public void Rename_WithSpecificFileOrder_RenumbersInCorrectOrder()
    {
        // Arrange
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_001.jpg", new MockFileData("") },
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_002.jpg", new MockFileData("") },
        }, "/archiv");
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var uut = root!.GetChild("Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung") as EventDirectory;
        uut!.LoadChildren();

        // Custom order: 002, 001. 
        // 002 should become 010, 001 should become 011
        var order = new[]
        {
            "A_2018-05-01_002",
            "A_2018-05-01_001",
        };

        // Act
        uut.ExecuteRename(uut.PlanRename(10, order));

        // Assert
        uut.Children.Should().HaveCount(2);
        
        // We can't easily verify which was which without more complex mocking, 
        // but we can verify they were renamed to the new numbers.
        uut.Children.Select(c => c.Name).Should().BeEquivalentTo(new[]
        {
            "A_2018-05-01_010",
            "A_2018-05-01_011",
        });
    }

    [Fact]
    public void Rename_RequiresExplicitPermissionToDeleteDngFiles()
    {
        // Arrange
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_001.dng", new MockFileData("") },
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_002.jpg", new MockFileData("") },
        }, "/archiv");
        fs.AddDirectory("/archiv"); // Ensure /archiv exists and is a directory
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var uut = root!.GetChild("Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung") as EventDirectory;
        uut!.LoadChildren();
        uut.Children.Should().HaveCount(2);

        // Act
        var plan = uut.PlanRename(10);
        var unconfirmed = () => uut.ExecuteRename(plan);
        unconfirmed.Should().Throw<RenameException>().WithMessage("*bestätigt*");
        uut.Children.Should().HaveCount(2);
        uut.ExecuteRename(plan, allowDngDeletion: true);

        // Assert
        uut.Children.Should().HaveCount(1);
        uut.Children[0].Name.Should().Be("A_2018-05-01_010");
        fs.FileExists("/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_001.dng").Should().BeFalse();
    }

    [Fact]
    public void RenameToFileDateTime_RenamesFilesBasedOnLastWriteTime()
    {
        // Arrange
        var time1 = new DateTime(2018, 05, 01, 10, 0, 0, DateTimeKind.Utc);
        var time2 = new DateTime(2018, 05, 01, 11, 0, 0, DateTimeKind.Utc);
        
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_001.jpg", new MockFileData("") { LastWriteTime = time1 } },
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_002.jpg", new MockFileData("") { LastWriteTime = time2 } },
        }, "/archiv");
        fs.AddDirectory("/archiv"); 
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var uut = root!.GetChild("Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung") as EventDirectory;
        uut!.LoadChildren();

        // Act
        uut.ExecuteRename(uut.PlanRenameByDateTime());

        // Assert
        uut.Children.Should().HaveCount(2);
        uut.Children.Any(c => c.Name == "2018-05-01_10-00-00").Should().BeTrue();
        uut.Children.Any(c => c.Name == "2018-05-01_11-00-00").Should().BeTrue();
    }

    [Fact]
    public void RenameToFileDateTime_SameTimestamp_AppendsCounter()
    {
        // Arrange: two files with identical LastWriteTimeUtc → would collide
        var sameTime = new DateTime(2018, 05, 01, 10, 0, 0, DateTimeKind.Utc);

        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_001.jpg", new MockFileData("") { LastWriteTime = sameTime } },
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_002.jpg", new MockFileData("") { LastWriteTime = sameTime } },
        }, "/archiv");
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var uut = root!.GetChild("Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung") as EventDirectory;
        uut!.LoadChildren();

        // Act — must not throw
        uut.ExecuteRename(uut.PlanRenameByDateTime());

        // Assert: one gets the raw name, the other gets the _2 suffix
        var names = uut.Children.Select(c => c.Name).ToArray();
        names.Should().Contain("2018-05-01_10-00-00");
        names.Should().Contain("2018-05-01_10-00-00_2");
    }

    [Fact]
    public void RenameToFileDateTime_ExistingDatetimeNamedFile_DoesNotCollide()
    {
        // Arrange: one file already has the datetime name on disk (prior rename),
        // a second file has the same timestamp → counter suffix avoids collision.
        var sameTime = new DateTime(2018, 05, 01, 10, 0, 0, DateTimeKind.Utc);

        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            // Already renamed in a prior run
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/2018-05-01_10-00-00.jpg", new MockFileData("") { LastWriteTime = sameTime } },
            // New file with same timestamp that still has the old archive name
            { "/archiv/Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung/A_2018-05-01_002.jpg", new MockFileData("") { LastWriteTime = sameTime } },
        }, "/archiv");
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var uut = root!.GetChild("Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung") as EventDirectory;
        uut!.LoadChildren();

        // Act — must not throw
        uut.ExecuteRename(uut.PlanRenameByDateTime());

        // Assert: the old-format file is now renamed with _2 suffix
        var names = uut.Children.Select(c => c.Name).ToArray();
        names.Should().Contain("2018-05-01_10-00-00");
        names.Should().Contain("2018-05-01_10-00-00_2");
    }

    [Fact]
    public void TryCreate_ParsesEventDirectoryCorrectly()
    {
        // Arrange
        var fs = FileSystemForTesting.Create();
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var uut = root!.GetChild("Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung");

        // Assert
        var eventDir = uut.Should().BeOfType<EventDirectory>().Subject;
        eventDir.ClubChar.Should().Be("A");
        eventDir.Year.Should().Be("2018");
        eventDir.Month.Should().Be("05");
        eventDir.Day.Should().Be("01");
        eventDir.EventName.Should().Be("Maiwanderung");
    }

    [Fact]
    public void TryCreate_ReturnsFalse_WhenRegexDoesNotMatch()
    {
        // Arrange
        var fs = new MockFileSystem();
        fs.AddDirectory("/archiv");
        var dirInfo = fs.DirectoryInfo.New("/archiv/Digitalfoto/2018/A-Albverein/InvalidName");
        
        // We need a parent for TryCreate
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var parent = root!.GetChild("Digitalfoto/2018/A-Albverein");

        // Act
        var result = EventDirectory.TryCreate(dirInfo, parent, out var eventDir);

        // Assert
        result.Should().BeFalse();
        eventDir.Should().BeNull();
    }

    [Fact]
    public void TryCreate_SetsIsValidFalse_WhenClubCharOrYearMismatch()
    {
        // Arrange
        var fs = new MockFileSystem();
        fs.AddDirectory("/archiv");
        // Name says B_2019 but parent is A-Albverein (A) and 2018
        var dirInfo = fs.DirectoryInfo.New("/archiv/Digitalfoto/2018/A-Albverein/B_2019-05-01_Event");
        
        ArchiveRoot.TryCreate("/archiv", fs, out var root).Should().BeTrue();
        var parent = root!.GetChild("Digitalfoto/2018/A-Albverein");

        // Act
        var result = EventDirectory.TryCreate(dirInfo, parent, out var eventDir);

        // Assert
        result.Should().BeTrue();
        eventDir.Should().NotBeNull();
        eventDir.IsValid.Should().BeFalse();
        ((EventDirectory)eventDir).ClubChar.Should().Be("B");
        ((EventDirectory)eventDir).Year.Should().Be("2019");
    }
}
