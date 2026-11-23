namespace FL.LigArchivar.Core;

public sealed class RenameExecutionException : Exception
{
    private const string ReloadHint = "Es kann bereits Teiländerungen geben. Bitte die Dateiliste neu laden.";

    internal RenameExecutionException(string source, string? destination, Exception inner)
        : base(destination is null
            ? $"Datei '{Path.GetFileName(source)}' konnte nicht gelöscht werden. {ReloadHint}"
            : $"Datei '{Path.GetFileName(source)}' konnte nicht nach '{Path.GetFileName(destination)}' verschoben werden. {ReloadHint}", inner)
    {
    }

    public RenameExecutionException(string directory, Exception inner)
        : base($"Die Dateiliste für '{directory}' konnte nach dem Umbenennen nicht geladen werden. {ReloadHint}", inner)
    {
    }
}
