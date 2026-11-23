namespace FL.LigArchivar.Api.Models;

public record TreeNodeDto(
    string Name,
    string Path,
    bool IsValid,
    string NodeType,
    TreeNodeDto[]? Children
);

public record EventDetailDto(
    string Name,
    string Path,
    string FilePrefix,
    bool IsValid,
    bool IsInPictures,
    FileGroupDto[] Files
);

public record FileGroupDto(
    string Name,
    string[] Extensions,
    string[] Properties,
    bool IsValid,
    bool IsOrphaned,
    DateTime LastWriteTimeUtc
);

public record RenameRequestDto(int StartNumber, string[]? FileOrder = null, string? ConfirmationToken = null);

public record RenameByDateTimeRequestDto(string? ConfirmationToken = null);

public record RenamePreviewDto(string[] FilesToDelete, string ConfirmationToken);

public record LoginRequestDto(string Username, string Password);

public record AuthStatusDto(bool Authenticated);
