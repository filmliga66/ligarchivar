using System.Buffers;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using FL.LigArchivar.Api.Models;
using FL.LigArchivar.Core;
using FL.LigArchivar.Core.Data;

namespace FL.LigArchivar.Api.Services;

/// <summary>
/// Thin service that bridges the API layer with the Core domain model.
/// Caches the archive tree and serializes write operations.
/// </summary>
public sealed class ArchiveService : IDisposable
{
    private readonly string _archiveRoot;

    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ArchiveService> _logger;

    // Write operations are serialized; if already locked, 409 is returned.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _confirmationKey = RandomNumberGenerator.GetBytes(32);

    // Simple in-memory cache: reload tree on every read request (archive is local I/O).
    // For large archives, consider caching with a stale-on-write invalidation strategy.

    public ArchiveService(IFileSystem fileSystem, ILogger<ArchiveService> logger, IConfiguration configuration)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _archiveRoot = configuration["ARCHIVE_ROOT"] ?? "/archive";
    }

    // ── Tree ──────────────────────────────────────────────────────────────────

    public TreeNodeDto[] GetTree(string? relativePath = null)
    {
        if (!Core.ArchiveRoot.TryCreate(_archiveRoot, _fileSystem, out var root) || root == null)
        {
            _logger.LogWarning("Archive root '{ArchiveRoot}' does not exist or could not be loaded.", _archiveRoot);
            return [];
        }

        IEnumerable<IFileSystemItem> items = root.Children;

        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var node = root.GetChild(relativePath);
            if (node is IFileSystemItemWithChildren container)
                items = container.Children;
            else
                return [];
        }

        return items.Select(item => MapToTreeNode(item, relativePath)).ToArray();
    }

    // ── Event ─────────────────────────────────────────────────────────────────

    public EventDetailDto? GetEvent(string path)
    {
        if (!ValidatePath(path)) return null;

        if (!Core.ArchiveRoot.TryCreate(_archiveRoot, _fileSystem, out var root) || root == null)
            return null;

        var node = root.GetChild(path);
        if (node is not EventDirectory eventDir)
            return null;

        eventDir.LoadChildren();
        return MapToEventDetail(eventDir, path);
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    public RenameOutcome Rename(string path, RenameRequestDto request) =>
        Rename(path, request.StartNumber, request.FileOrder, request.ConfirmationToken);

    public RenameOutcome RenameByDateTime(string path, string? confirmationToken) =>
        Rename(path, null, null, confirmationToken);

    private RenameOutcome Rename(string path, int? startNumber, string[]? fileOrder, string? confirmationToken)
    {
        if (!ValidatePath(path))
            return new RenameFailure(StatusCodes.Status400BadRequest, "Invalid path.");

        if (!_writeLock.Wait(0))
            return new RenameFailure(StatusCodes.Status409Conflict, "A rename operation is already in progress.");

        try
        {
            if (!Core.ArchiveRoot.TryCreate(_archiveRoot, _fileSystem, out var root) || root == null)
                return new RenameFailure(StatusCodes.Status422UnprocessableEntity, "Das Archiv wurde nicht gefunden.");

            var node = root.GetChild(path);
            if (node is not EventDirectory eventDir)
                return new RenameFailure(StatusCodes.Status404NotFound, "Die Veranstaltung wurde nicht gefunden.");

            eventDir.LoadChildren();
            var plan = startNumber is int number
                ? eventDir.PlanRename(number, fileOrder)
                : eventDir.PlanRenameByDateTime();
            var confirmed = false;
            if (plan.HasDeletions)
            {
                var expected = CreateConfirmationProof(eventDir.Directory.FullName, startNumber, fileOrder, plan);
                Span<byte> supplied = stackalloc byte[32];
                confirmed = confirmationToken?.Length == expected.Length * 2
                    && Convert.FromHexString(confirmationToken.AsSpan(), supplied, out _, out var written) == OperationStatus.Done
                    && written == expected.Length
                    && CryptographicOperations.FixedTimeEquals(expected, supplied);
                if (!confirmed)
                {
                    var names = plan.Entries.Where(entry => entry.DestinationPath is null)
                        .Select(entry => _fileSystem.Path.GetFileName(entry.SourcePath)).ToArray();
                    return new RenameNeedsConfirmation(new RenamePreviewDto(names, Convert.ToHexString(expected)));
                }
            }
            else if (confirmationToken is not null)
            {
                return new RenameFailure(StatusCodes.Status422UnprocessableEntity,
                    "Der Umbenennungsplan hat sich geändert. Bitte die Aktion neu starten.");
            }

            eventDir.ExecuteRename(plan, allowDngDeletion: confirmed, logger: _logger);
            try
            {
                return new RenameCompleted(MapToEventDetail(eventDir, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RenameExecutionException(path, ex);
            }
        }
        catch (RenameException ex)
        {
            _logger.LogWarning("Rename preflight failed for '{Path}': {Message}", path, ex.Message);
            return new RenameFailure(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (RenameExecutionException ex)
        {
            _logger.LogError(ex, "Rename execution failed for '{Path}'.", path);
            return new RenameFailure(StatusCodes.Status500InternalServerError, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not prepare rename for '{Path}'.", path);
            return new RenameFailure(StatusCodes.Status500InternalServerError,
                $"Die Dateien für '{path}' konnten nicht geprüft werden. Es wurde nichts geändert.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private byte[] CreateConfirmationProof(string eventDirectory, int? startNumber, string[]? fileOrder, RenamePlan plan)
    {
        // The numbered variant has a start number; the date variant does not.
        // Recompute the proof under the write lock from the paths and current file metadata.
        var entries = plan.Entries.Select(entry =>
        {
            var file = _fileSystem.FileInfo.New(entry.SourcePath);
            return new { entry.SourcePath, entry.DestinationPath, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc };
        }).ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { eventDirectory, startNumber, fileOrder, entries });
        return HMACSHA256.HashData(_confirmationKey, payload);
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static TreeNodeDto MapToTreeNode(IFileSystemItem item, string? parentPath)
    {
        var nodePath = string.IsNullOrEmpty(parentPath)
            ? item.Name
            : parentPath.TrimEnd('/') + "/" + item.Name;

        var nodeType = item switch
        {
            AssetDirectory => "asset",
            YearDirectory => "year",
            ClubDirectory => "club",
            EventDirectory => "event",
            IgnoredFileSystemItem => "ignored",
            InvalidFileSystemItem => "invalid",
            _ => "unknown"
        };

        TreeNodeDto[]? children = null;
        if (item is IFileSystemItemWithChildren container && item is not EventDirectory)
            children = container.Children.Select(c => MapToTreeNode(c, nodePath)).ToArray();

        return new TreeNodeDto(item.Name, nodePath, item.IsValid, nodeType, children);
    }

    private static EventDetailDto MapToEventDetail(EventDirectory eventDir, string path)
    {
        var files = eventDir.Children
            .Where(f => !f.IsIgnored)
            .Select(f => new FileGroupDto(
                f.Name,
                f.Files.Select(fi => fi.Extension).ToArray(),
                f.Files.Select(fi => fi.Property).Where(p => !string.IsNullOrEmpty(p)).ToArray(),
                f.IsValid,
                f.IsDngOnly,
                f.Files.Count > 0 ? f.Files[0].LastWriteTimeUtc : DateTime.MinValue
            ))
            .ToArray();

        return new EventDetailDto(
            eventDir.Name,
            path,
            eventDir.FilePrefix,
            eventDir.IsValid,
            eventDir.IsInPictures(),
            files);
    }

    // ── Path validation ───────────────────────────────────────────────────────

    public static bool ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Prevent directory traversal
        if (path.Contains("..")) return false;
        if (path.Contains('\0')) return false;

        // Must not be rooted
        if (System.IO.Path.IsPathRooted(path)) return false;

        return true;
    }

    public void Dispose()
    {
        _writeLock.Dispose();
    }
}

public abstract record RenameOutcome;
public sealed record RenameCompleted(EventDetailDto Event) : RenameOutcome;
public sealed record RenameNeedsConfirmation(RenamePreviewDto Preview) : RenameOutcome;
public sealed record RenameFailure(int StatusCode, string Message) : RenameOutcome;
