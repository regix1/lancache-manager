using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    private async Task ValidateRemovalSelectionAsync(
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateRemovalSelectionAsync(context, selection, cancellationToken);
    }

    internal static async Task ValidateRemovalSelectionAsync(
        AppDbContext context,
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        var downloads = SelectRemovalDownloads(context, selection);
        var mismatch = await (
            from download in downloads
            join logEntry in context.LogEntries on download.Id equals logEntry.DownloadId
            where logEntry.Datasource != download.Datasource
            select logEntry.Id).AnyAsync(cancellationToken);
        if (mismatch)
        {
            throw new InvalidDataException(
                "Selected removal history contains a log entry attributed to a different datasource");
        }
    }

    private async Task<RemovalCleanupResult> CleanupRemovalAsync(
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await CleanupRemovalAsync(context, selection, cancellationToken);
    }

    internal static async Task<RemovalCleanupResult> CleanupRemovalAsync(
        AppDbContext context,
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        // Large set-based deletes can exceed Npgsql's 30-second default. A command timeout would
        // otherwise retry the whole transaction while the ordered table locks are held.
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

        // The pooled context can retry transient database failures only when it owns the complete
        // transaction. EF rejects a user transaction outside this retry block at the first query.
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (context.Database.IsNpgsql())
            {
                await context.Database.ExecuteSqlRawAsync(
                    "LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE",
                    cancellationToken);
                await context.Database.ExecuteSqlRawAsync(
                    "LOCK TABLE \"LogEntries\" IN SHARE ROW EXCLUSIVE MODE",
                    cancellationToken);
            }

            await ValidateRemovalSelectionAsync(context, selection, cancellationToken);
            var datasourceNames = selection.DatasourceNames
                .Select(name => name.ToLowerInvariant())
                .ToList();
            var downloads = SelectRemovalDownloads(context, selection);
            var downloadIds = downloads.Select(download => download.Id);
            var logEntriesDeleted = await context.LogEntries
                .Where(logEntry =>
                    datasourceNames.Contains(logEntry.Datasource.ToLower()) &&
                    logEntry.DownloadId.HasValue &&
                    downloadIds.Contains(logEntry.DownloadId.Value))
                .ExecuteDeleteAsync(cancellationToken);
            var downloadsDeleted = await downloads.ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RemovalCleanupResult(downloadsDeleted, logEntriesDeleted);
        });
    }

    internal static IQueryable<Download> SelectRemovalDownloads(
        AppDbContext context,
        RemovalSelection selection)
    {
        var datasourceNames = selection.DatasourceNames
            .Select(name => name.ToLowerInvariant())
            .ToList();
        var downloads = context.Downloads.Where(download =>
            datasourceNames.Contains(download.Datasource.ToLower()));

        return selection.Kind switch
        {
            RemovalKind.Steam => downloads.Where(download =>
                download.GameAppId == selection.GameAppId),
            RemovalKind.Epic => downloads.Where(download =>
                download.GameName == selection.GameName && download.EpicAppId != null),
            RemovalKind.Named => downloads.Where(download =>
                download.GameName == selection.GameName &&
                download.GameAppId == null &&
                download.EpicAppId == null &&
                download.Service.ToLower() == selection.Service),
            RemovalKind.Service => downloads.Where(download =>
                download.Service.ToLower() == selection.Service),
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
    }
}
