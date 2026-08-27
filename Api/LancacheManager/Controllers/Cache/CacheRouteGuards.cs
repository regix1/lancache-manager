using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Request guards shared by the cache and game removal routes. CacheController and GamesController
/// have different constructors and dependency sets, so each guard takes the services it needs as
/// arguments instead of reaching for fields on a base class.
/// </summary>
internal static class CacheRouteGuards
{
    /// <summary>
    /// Duplicate of the service-level capability guard for UX: key-dependent endpoints
    /// (corruption detection/removal, service and game cache removal, eviction removal) reject with
    /// a clear message instead of failing deep inside the operation. The mutating services
    /// revalidate again at execution time — this check is presentation, not the safety net.
    /// </summary>
    internal static BadRequestObjectResult? DenyIfKeyDependentUnavailable(DatasourceCapabilityService capabilityService)
    {
        var denial = capabilityService.CheckAllCanMapLogicalObjects();
        return denial == null ? null : new BadRequestObjectResult(ApiResponse.Error(denial));
    }

    /// <summary>
    /// Checks cache and logs directory write permissions.
    /// Returns a BadRequest result with the PUID/PGID error message if either directory is
    /// read-only, or null when both are writable. Logs a warning with the given context.
    /// </summary>
    internal static BadRequestObjectResult? EnsureDirectoriesWritable(
        IPathResolver pathResolver,
        ILogger logger,
        string operationDescription,
        string logContext)
    {
        var cacheWritable = pathResolver.IsCacheWritable();
        var logsWritable = pathResolver.IsLogsWritable();

        if (cacheWritable && logsWritable)
            return null;

        var errors = new List<string>();
        if (!cacheWritable) errors.Add("cache directory is read-only");
        if (!logsWritable) errors.Add("logs directory is read-only");

        var errorMessage = $"Cannot {operationDescription}: {string.Join(" and ", errors)}. " +
            "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
            $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

        logger.LogWarning("{Context} Permission check failed: {Error}", logContext, errorMessage);
        return new BadRequestObjectResult(ApiResponse.Error(errorMessage));
    }

    /// <summary>
    /// Refuses a cache-file deletion unless the caller declared that scope. Returns a BadRequest
    /// result when the declared scope is missing or is anything other than
    /// <see cref="CacheRemovalScope.CacheFiles"/>, or null when the caller asked for a cache-file
    /// deletion.
    /// </summary>
    /// <remarks>
    /// The evicted-items panel removes records, not files, and reached a cache-file route once
    /// because nothing on the wire distinguished the two intents. Refusing an undeclared scope
    /// keeps a caller that means <see cref="CacheRemovalScope.EvictedRecords"/> from wiping cached
    /// files. Every route that unlinks files calls this first, ahead of every other check.
    /// </remarks>
    internal static BadRequestObjectResult? EnsureCacheFileScopeDeclared(
        ILogger logger,
        string? scope,
        string logContext)
    {
        if (string.Equals(scope, CacheRemovalScope.CacheFiles, StringComparison.Ordinal))
        {
            return null;
        }

        logger.LogWarning(
            "{Context} Refused: removal scope '{Scope}' does not authorize deleting cache files",
            logContext,
            string.IsNullOrWhiteSpace(scope) ? "(none)" : scope);

        return new BadRequestObjectResult(ApiResponse.Error(
            $"This route deletes cache files. Send scope={CacheRemovalScope.CacheFiles} to confirm that, " +
            $"or use the evicted routes for a {CacheRemovalScope.EvictedRecords} removal."));
    }
}
