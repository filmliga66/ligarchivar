using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Net.Http.Json;
using FL.LigArchivar.Api.Models;

namespace FL.LigArchivar.Api.Tests;

public class RenameConfirmationTests
{
    private const string EventPath = "Digitalfoto/2018/A-Albverein/A_2018-05-01_Maiwanderung";
    private const string OtherEventPath = "Digitalfoto/2018/A-Albverein/A_2018-05-01_Andere";
    private const string DirectoryPath = "/archive/" + EventPath;

    [Theory]
    [InlineData(false, "A_2018-05-01_001")]
    [InlineData(true, "2026-06-01_10-00-00")]
    public async Task OnlyCurrentConfirmedPlanCanDeleteTheListedFiles(bool byDate, string newName)
    {
        var fs = CreateFileSystem();
        await using var factory = new ArchiveApiFactory(fs);
        using var client = await factory.CreateAuthenticatedClientAsync();
        var original = Snapshot(fs);

        var preview = await ReadPreview(await Rename(client, byDate));
        preview.FilesToDelete.Should().BeEquivalentTo("Raw.DNG", "raw_bearbeitet.dng");
        var groups = (await client.GetFromJsonAsync<EventDetailDto>($"/api/events?path={EventPath}"))!.Files;
        groups.Should().Contain(group => group.Name == "Raw" && group.IsOrphaned);
        groups.Should().Contain(group => group.Name.Equals("film", StringComparison.OrdinalIgnoreCase) && !group.IsOrphaned);
        Snapshot(fs).Should().BeEquivalentTo(original);

        await ReadPreview(await Rename(client, byDate, new string('x', 64)));
        Snapshot(fs).Should().BeEquivalentTo(original);

        // The same proof cannot be used for a different action or event.
        await ReadPreview(await Rename(client, !byDate, preview.ConfirmationToken));
        var otherBefore = Snapshot(fs, OtherEventPath);
        await ReadPreview(await Rename(client, byDate, preview.ConfirmationToken, path: OtherEventPath));
        Snapshot(fs, OtherEventPath).Should().BeEquivalentTo(otherBefore);
        if (!byDate)
        {
            await ReadPreview(await Rename(client, false, preview.ConfirmationToken, startNumber: 2));
            await ReadPreview(await Rename(client, false, preview.ConfirmationToken, fileOrder: ["film", "raw"]));
        }
        Snapshot(fs).Should().BeEquivalentTo(original);

        fs.File.WriteAllText($"{DirectoryPath}/Raw.DNG", "replacement with the same name");
        var replaced = Snapshot(fs);
        var withReplacement = await ReadPreview(await Rename(client, byDate, preview.ConfirmationToken));
        withReplacement.ConfirmationToken.Should().NotBe(preview.ConfirmationToken);
        withReplacement.FilesToDelete.Should().BeEquivalentTo(preview.FilesToDelete);
        Snapshot(fs).Should().BeEquivalentTo(replaced);

        fs.AddFile($"{DirectoryPath}/extra.DNG", "extra raw");
        var changed = Snapshot(fs);
        var refreshed = await ReadPreview(await Rename(client, byDate, withReplacement.ConfirmationToken));
        refreshed.ConfirmationToken.Should().NotBe(withReplacement.ConfirmationToken);
        refreshed.FilesToDelete.Should().BeEquivalentTo("Raw.DNG", "raw_bearbeitet.dng", "extra.DNG");
        Snapshot(fs).Should().BeEquivalentTo(changed);

        using var confirmed = await Rename(client, byDate, refreshed.ConfirmationToken);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        Snapshot(fs).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [$"{newName}.JPG"] = "photo",
            [$"{newName}.dng"] = "matching raw",
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleConfirmationCannotExecuteEvenIfNewPlanHasNoDeletions(bool byDate)
    {
        var fs = CreateFileSystem();
        await using var factory = new ArchiveApiFactory(fs);
        using var client = await factory.CreateAuthenticatedClientAsync();
        var preview = await ReadPreview(await Rename(client, byDate));

        fs.AddFile($"{DirectoryPath}/raw.png", "new matching image");
        var changed = Snapshot(fs);
        using var stale = await Rename(client, byDate, preview.ConfirmationToken);
        stale.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Snapshot(fs).Should().BeEquivalentTo(changed);

        using var fresh = await Rename(client, byDate);
        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
        Snapshot(fs).Values.Should().BeEquivalentTo(changed.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvableConflictReturns422WithoutWrites(bool byDate)
    {
        var fs = CreateFileSystem();
        fs.AddFile($"{DirectoryPath}/same_bearbeitet.jpg", "late conflict");
        fs.AddFile($"{DirectoryPath}/SAME_BEARBEITET_extra.JPG", "duplicate destination");
        await using var factory = new ArchiveApiFactory(fs);
        using var client = await factory.CreateAuthenticatedClientAsync();
        var original = Snapshot(fs);

        using var response = await Rename(client, byDate);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = await response.Content.ReadFromJsonAsync<Error>();
        error!.Message.Should().Contain("doppelt geplant").And.Contain("same_bearbeitet.jpg").And.Contain("SAME_BEARBEITET_extra.JPG");
        Snapshot(fs).Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task MoveFailureDoesNotOverwriteOrContinueAndReloadFailureDoesNotMaskIt()
    {
        var fs = new CollisionFileSystem();
        fs.AddFile($"{DirectoryPath}/first.jpg", "first");
        fs.AddFile($"{DirectoryPath}/second.jpg", "second");
        fs.AddFile($"{DirectoryPath}/third.jpg", "third");
        await using var factory = new ArchiveApiFactory(fs);
        using var client = await factory.CreateAuthenticatedClientAsync();

        using var response = await Rename(client, false, fileOrder: ["first", "second", "third"]);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var error = await response.Content.ReadFromJsonAsync<Error>();
        error!.Message.Should().Contain("second.jpg").And.Contain("A_2018-05-01_002.jpg")
            .And.Contain("Teiländerungen").And.Contain("neu laden");
        fs.ReloadFailed.Should().BeTrue();
        Snapshot(fs).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["A_2018-05-01_001.jpg"] = "first",
            ["A_2018-05-01_002.jpg"] = "external write",
            ["second.jpg"] = "second",
            ["third.jpg"] = "third",
        });
    }

    private static MockFileSystem CreateFileSystem() => new(new Dictionary<string, MockFileData>
    {
        [$"{DirectoryPath}/Raw.DNG"] = new("original raw"),
        [$"{DirectoryPath}/raw_bearbeitet.dng"] = new("edited raw"),
        [$"{DirectoryPath}/film.dng"] = new("matching raw"),
        [$"{DirectoryPath}/Film.JPG"] = new("photo") { LastWriteTime = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc) },
        [$"/archive/{OtherEventPath}/Raw.DNG"] = new("other raw"),
    });

    private static Dictionary<string, string> Snapshot(MockFileSystem fs, string eventPath = EventPath) =>
        fs.Directory.GetFiles($"/archive/{eventPath}").ToDictionary(path => fs.Path.GetFileName(path)!, fs.File.ReadAllText);

    private static Task<HttpResponseMessage> Rename(HttpClient client, bool byDate, string? token = null,
        int startNumber = 1, string?[]? fileOrder = null, string path = EventPath) => byDate
        ? client.PostAsJsonAsync($"/api/events/rename-by-datetime?path={Uri.EscapeDataString(path)}", new { confirmationToken = token })
        : client.PostAsJsonAsync($"/api/events/rename?path={Uri.EscapeDataString(path)}", new { startNumber, fileOrder, confirmationToken = token });

    private static async Task<Preview> ReadPreview(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var preview = await response.Content.ReadFromJsonAsync<Preview>();
            preview!.ConfirmationToken.Should().NotBeNullOrEmpty();
            return preview;
        }
    }

    private sealed record Preview(string[] FilesToDelete, string ConfirmationToken);
    private sealed record Error(string Message);

    private sealed class CollisionFileSystem : MockFileSystem
    {
        private readonly IFile _file;
        private readonly IDirectory _directory;
        private bool _failNextListing;

        public CollisionFileSystem()
        {
            _file = new CollisionFile(this);
            _directory = new FaultingDirectory(this);
        }

        public override IFile File => _file ?? base.File;
        public override IDirectory Directory => _directory ?? base.Directory;
        public bool ReloadFailed { get; private set; }

        private sealed class CollisionFile(CollisionFileSystem fs) : MockFile(fs)
        {
            public override void Move(string sourceFileName, string destFileName, bool overwrite)
            {
                if (sourceFileName == $"{DirectoryPath}/second.jpg")
                {
                    fs.AddFile(destFileName, "external write");
                    fs._failNextListing = true;
                }
                base.Move(sourceFileName, destFileName, overwrite);
            }
        }

        private sealed class FaultingDirectory(CollisionFileSystem fs) : MockDirectory(fs, "/")
        {
            public override string[] GetFiles(string path)
            {
                if (path == DirectoryPath && fs._failNextListing)
                {
                    fs._failNextListing = false;
                    fs.ReloadFailed = true;
                    throw new UnauthorizedAccessException("simulated reload failure");
                }
                return base.GetFiles(path);
            }
        }
    }
}
