namespace LancacheManager.Core.Services;

internal enum RemovalKind
{
    Steam,
    Epic,
    Named,
    Service
}

/// <summary>
/// What a removal deletes from the history. NewestLogEntryIdBefore is the newest LogEntries id when
/// the datasource's cache step began: a download with a newer row was logged during the removal
/// and its files may be on disk again, so the log step keeps it.
/// </summary>
internal sealed record RemovalSelection(
    IReadOnlyList<string> DatasourceNames,
    RemovalKind Kind,
    long? GameAppId = null,
    string? GameName = null,
    string? Service = null,
    long? NewestLogEntryIdBefore = null);

internal sealed record RemovalCleanupResult(int DownloadsDeleted, int LogEntriesDeleted);
