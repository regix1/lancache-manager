namespace LancacheManager.Core.Services;

internal enum RemovalKind
{
    Steam,
    Epic,
    Named,
    Service
}

internal sealed record RemovalSelection(
    IReadOnlyList<string> DatasourceNames,
    RemovalKind Kind,
    long? GameAppId = null,
    string? GameName = null,
    string? Service = null);

internal sealed record RemovalCleanupResult(int DownloadsDeleted, int LogEntriesDeleted);
