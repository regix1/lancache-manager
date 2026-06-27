using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Service for managing prefill sessions and Steam user bans.
/// Handles session persistence, ban checking, and orphan container detection.
/// </summary>
public class PrefillSessionService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<PrefillSessionService> _logger;

    public PrefillSessionService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<PrefillSessionService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    #region Ban Management

    /// <summary>
    /// Checks if a Steam username is banned.
    /// Returns true if the user is banned and the ban is active.
    /// Comparison is case-insensitive.
    /// </summary>
    public async Task<bool> IsUsernameBannedAsync(string username)
    {
        if (string.IsNullOrEmpty(username))
            return false;

        var normalizedUsername = username.Trim().ToLowerInvariant();
        await using var context = await _contextFactory.CreateDbContextAsync();

        var ban = await context.BannedSteamUsers
            .AsNoTracking()
            .Where(b => b.Username == normalizedUsername && !b.IsLifted)
            .Where(b => b.ExpiresAtUtc == null || b.ExpiresAtUtc > DateTime.UtcNow)
            .FirstOrDefaultAsync();

        return ban != null;
    }

    /// <summary>
    /// Checks if a lancache-manager auth-session id (DaemonSession.UserId GUID) is banned.
    /// This is the enforcement path for anonymous services (e.g. Battle.net) that have no username.
    /// Returns true if there is an active ban for the given UserId.
    /// </summary>
    public async Task<bool> IsUserIdBannedAsync(Guid userId)
    {
        if (userId == Guid.Empty)
            return false;

        await using var context = await _contextFactory.CreateDbContextAsync();

        var ban = await context.BannedSteamUsers
            .AsNoTracking()
            .Where(b => b.BannedUserId == userId && !b.IsLifted)
            .Where(b => b.ExpiresAtUtc == null || b.ExpiresAtUtc > DateTime.UtcNow)
            .FirstOrDefaultAsync();

        return ban != null;
    }

    /// <summary>
    /// Bans a Steam user by their username.
    /// </summary>
    public async Task<BannedSteamUser> BanUserAsync(
        string username,
        string? reason = null,
        string? bannedBySessionId = null,
        string? bannedBy = null,
        DateTime? expiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required", nameof(username));

        var normalizedUsername = username.Trim().ToLowerInvariant();
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check if already banned
        var existingBan = await context.BannedSteamUsers
            .Where(b => b.Username == normalizedUsername && !b.IsLifted)
            .FirstOrDefaultAsync();

        if (existingBan != null)
        {
            _logger.LogInformation("User {Username} is already banned", username);
            return existingBan;
        }

        var ban = new BannedSteamUser
        {
            Username = normalizedUsername,
            BanReason = reason,
            BannedBySessionId = bannedBySessionId,
            BannedBy = bannedBy ?? "admin",
            BannedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAt
        };

        context.BannedSteamUsers.Add(ban);
        await context.SaveChangesAsync();

        _logger.LogWarning("Banned Steam user {Username} by session {BannedBySessionId}. Reason: {Reason}",
            username, bannedBySessionId ?? "admin", reason ?? "No reason provided");

        return ban;
    }

    /// <summary>
    /// Bans a prefill user by their lancache-manager auth-session id (DaemonSession.UserId GUID).
    /// Used for anonymous services (e.g. Battle.net) that have no game username to ban.
    /// </summary>
    public async Task<BannedSteamUser> BanByUserIdAsync(
        Guid bannedUserId,
        string? reason = null,
        string? bannedBySessionId = null,
        string? bannedBy = null,
        DateTime? expiresAt = null)
    {
        if (bannedUserId == Guid.Empty)
            throw new ArgumentException("BannedUserId is required", nameof(bannedUserId));

        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check if already banned
        var existingBan = await context.BannedSteamUsers
            .Where(b => b.BannedUserId == bannedUserId && !b.IsLifted)
            .FirstOrDefaultAsync();

        if (existingBan != null)
        {
            _logger.LogInformation("User id {BannedUserId} is already banned", bannedUserId);
            return existingBan;
        }

        var ban = new BannedSteamUser
        {
            Username = null,
            BannedUserId = bannedUserId,
            BanReason = reason,
            BannedBySessionId = bannedBySessionId,
            BannedBy = bannedBy ?? "admin",
            BannedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAt
        };

        context.BannedSteamUsers.Add(ban);
        await context.SaveChangesAsync();

        _logger.LogWarning("Banned prefill user id {BannedUserId} by session {BannedBySessionId}. Reason: {Reason}",
            bannedUserId, bannedBySessionId ?? "admin", reason ?? "No reason provided");

        return ban;
    }

    /// <summary>
    /// Lifts a ban for a Steam user.
    /// </summary>
    public async Task<BannedSteamUser?> LiftBanAsync(long banId, string? liftedBy = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var ban = await context.BannedSteamUsers.FindAsync(banId);
        if (ban == null || ban.IsLifted)
            return null;

        ban.IsLifted = true;
        ban.LiftedAtUtc = DateTime.UtcNow;
        ban.LiftedBy = liftedBy ?? "admin";

        await context.SaveChangesAsync();

        _logger.LogInformation("Lifted ban {BanId} for user {Username}",
            banId, ban.Username);

        return ban;
    }

    /// <summary>
    /// Gets all active bans.
    /// </summary>
    public async Task<List<BannedSteamUser>> GetActiveBansAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.BannedSteamUsers
            .AsNoTracking()
            .Where(b => !b.IsLifted)
            .Where(b => b.ExpiresAtUtc == null || b.ExpiresAtUtc > DateTime.UtcNow)
            .OrderByDescending(b => b.BannedAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Gets all bans (including lifted and expired).
    /// </summary>
    public async Task<List<BannedSteamUser>> GetAllBansAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.BannedSteamUsers
            .AsNoTracking()
            .OrderByDescending(b => b.BannedAtUtc)
            .ToListAsync();
    }

    #endregion

    #region Session Persistence

    /// <summary>
    /// Creates a new prefill session record in the database.
    /// </summary>
    public async Task<PrefillSession> CreateSessionAsync(
        string sessionId,
        Guid createdBySessionId,
        string? containerId,
        string? containerName,
        DateTime expiresAt,
        string platform = "Steam")
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = new PrefillSession
        {
            SessionId = sessionId,
            CreatedBySessionId = createdBySessionId,
            ContainerId = containerId,
            ContainerName = containerName,
            Platform = Enum.TryParse<PrefillPlatform>(platform, ignoreCase: true, out var parsedPlatform)
                ? parsedPlatform
                : PrefillPlatform.Steam,
            Status = PrefillSessionStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAt
        };

        context.PrefillSessions.Add(session);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created prefill session record {SessionId} for session {CreatedBySessionId}",
            sessionId, createdBySessionId);

        return session;
    }

    /// <summary>
    /// Re-activates a previously orphaned prefill session record in place. Used when a PERSISTENT
    /// daemon container survived a manager restart and is re-adopted: the prior record was just flipped
    /// to <see cref="PrefillSessionStatus.Orphaned"/> by <see cref="MarkOrphansAsync"/>, so this flips it
    /// back to <see cref="PrefillSessionStatus.Active"/> and refreshes its container id/name and expiry.
    /// Because <c>SessionId</c> is uniquely indexed, this must update in place rather than insert. Falls
    /// back to creating a fresh Active record only when no prior record exists (e.g. the DB was reset
    /// while the container kept running).
    /// </summary>
    public async Task<PrefillSession> ReactivateSessionAsync(
        string sessionId,
        Guid createdBySessionId,
        string? containerId,
        string? containerName,
        DateTime expiresAt,
        string platform = "Steam")
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session == null)
        {
            session = new PrefillSession
            {
                SessionId = sessionId,
                CreatedBySessionId = createdBySessionId,
                ContainerId = containerId,
                ContainerName = containerName,
                Platform = Enum.TryParse<PrefillPlatform>(platform, ignoreCase: true, out var parsedPlatform)
                    ? parsedPlatform
                    : PrefillPlatform.Steam,
                Status = PrefillSessionStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = expiresAt
            };
            context.PrefillSessions.Add(session);
        }
        else
        {
            session.Status = PrefillSessionStatus.Active;
            session.ContainerId = containerId;
            session.ContainerName = containerName;
            session.ExpiresAtUtc = expiresAt;
            session.EndedAtUtc = null;
            session.TerminationReason = null;
            session.TerminatedBy = null;
        }

        await context.SaveChangesAsync();

        _logger.LogInformation(
            "Reactivated prefill session record {SessionId} after re-adopting its running container", sessionId);

        return session;
    }

    /// <summary>
    /// Updates the Steam username for a session.
    /// Called when the user provides their username credential.
    /// </summary>
    public async Task SetUsernameAsync(string sessionId, string username)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session != null)
        {
            session.SteamUsername = username.Trim();
            await context.SaveChangesAsync();

            _logger.LogDebug("Set username for session {SessionId}: {Username}", sessionId, username);
        }
    }

    /// <summary>
    /// Updates the authentication status for a session.
    /// </summary>
    public async Task SetAuthenticatedAsync(string sessionId, bool isAuthenticated)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session != null)
        {
            session.IsAuthenticated = isAuthenticated;
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Updates the prefilling status for a session.
    /// </summary>
    public async Task SetPrefillingAsync(string sessionId, bool isPrefilling)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session != null)
        {
            session.IsPrefilling = isPrefilling;
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Marks a session as terminated.
    /// </summary>
    public async Task TerminateSessionAsync(
        string sessionId,
        string reason,
        string? terminatedBy = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session != null && session.Status == PrefillSessionStatus.Active)
        {
            session.Status = PrefillSessionStatus.Terminated;
            session.EndedAtUtc = DateTime.UtcNow;
            session.TerminationReason = reason;
            session.TerminatedBy = terminatedBy;
            await context.SaveChangesAsync();

            _logger.LogInformation("Terminated session {SessionId}: {Reason}", sessionId, reason);
        }
    }

    /// <summary>
    /// Gets all active sessions from the database.
    /// </summary>
    public async Task<List<PrefillSession>> GetActiveSessionsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.PrefillSessions
            .AsNoTracking()
            .Where(s => s.Status == PrefillSessionStatus.Active)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Gets all sessions (paginated).
    /// </summary>
    public async Task<(List<PrefillSession> Sessions, int TotalCount)> GetSessionsAsync(
        int page = 1,
        int pageSize = 20,
        string? statusFilter = null,
        string? platformFilter = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.PrefillSessions.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(statusFilter)
            && Enum.TryParse<PrefillSessionStatus>(statusFilter, ignoreCase: true, out var parsedStatus))
        {
            query = query.Where(s => s.Status == parsedStatus);
        }

        if (!string.IsNullOrEmpty(platformFilter)
            && Enum.TryParse<PrefillPlatform>(platformFilter, ignoreCase: true, out var parsedPlatformFilter))
        {
            query = query.Where(s => s.Platform == parsedPlatformFilter);
        }

        var totalCount = await query.CountAsync();

        var sessions = await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (sessions, totalCount);
    }

    /// <summary>
    /// Gets a session by its session ID.
    /// </summary>
    public async Task<PrefillSession?> GetSessionAsync(string sessionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.PrefillSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);
    }

    /// <summary>
    /// Marks orphaned sessions (sessions marked as Active in DB but not in memory).
    /// Called on startup to detect containers that may still be running.
    /// </summary>
    public async Task<List<PrefillSession>> MarkOrphansAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var activeSessions = await context.PrefillSessions
            .Where(s => s.Status == PrefillSessionStatus.Active)
            .ToListAsync();

        foreach (var session in activeSessions)
        {
            session.Status = PrefillSessionStatus.Orphaned;
            session.EndedAtUtc = DateTime.UtcNow;
            session.TerminationReason = "App restarted - session orphaned";
        }

        if (activeSessions.Count > 0)
        {
            await context.SaveChangesAsync();
            _logger.LogWarning("Marked {Count} sessions as orphaned after app restart", activeSessions.Count);
        }

        return activeSessions;
    }

    /// <summary>
    /// Gets orphaned sessions with their container IDs for cleanup.
    /// </summary>
    public async Task<List<string>> GetOrphanedContainerIdsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.PrefillSessions
            .AsNoTracking()
            .Where(s => s.Status == PrefillSessionStatus.Orphaned && s.ContainerId != null)
            .Select(s => s.ContainerId!)
            .ToListAsync();
    }

    /// <summary>
    /// Marks an orphaned session as cleaned up.
    /// </summary>
    public async Task MarkOrphanCleanedAsync(string containerId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.ContainerId == containerId && s.Status == PrefillSessionStatus.Orphaned);

        if (session != null)
        {
            session.Status = PrefillSessionStatus.Cleaned;
            session.TerminationReason = "Orphaned container terminated on startup";
            await context.SaveChangesAsync();
        }
    }

    #endregion

    #region Prefill History

    /// <summary>
    /// Records the start of a game prefill.
    /// Returns null if the app was recently completed (prevents duplicate entries).
    /// </summary>
    public async Task<PrefillHistoryEntry?> StartEntryAsync(
        string sessionId,
        string appId,
        string? appName)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check if this app was just completed or cached (within last 5 seconds) - don't create duplicate
        var recentCutoff = DateTime.UtcNow.AddSeconds(-5);
        var recentlyFinished = await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.AppId == appId)
            .Where(e => (e.Status == PrefillHistoryEntryStatus.Completed || e.Status == PrefillHistoryEntryStatus.Cached) && e.CompletedAtUtc != null && e.CompletedAtUtc > recentCutoff)
            .AnyAsync();

        if (recentlyFinished)
        {
            _logger.LogDebug("Skipping InProgress entry for app {AppId} - was just completed or cached", appId);
            return null;
        }

        // Clean up any stale "InProgress" entries for this app in this session
        // This prevents duplicate entries when prefill is restarted or interrupted
        var staleEntries = await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.AppId == appId && e.Status == PrefillHistoryEntryStatus.InProgress)
            .ToListAsync();

        if (staleEntries.Count > 0)
        {
            foreach (var stale in staleEntries)
            {
                stale.Status = PrefillHistoryEntryStatus.Cancelled;
                stale.CompletedAtUtc = DateTime.UtcNow;
                stale.ErrorMessage = "Superseded by new prefill operation";
            }
            _logger.LogDebug("Cancelled {Count} stale InProgress entries for app {AppId} in session {SessionId}",
                staleEntries.Count, appId, sessionId);
        }

        var entry = new PrefillHistoryEntry
        {
            SessionId = sessionId,
            AppId = appId,
            AppName = appName,
            StartedAtUtc = DateTime.UtcNow,
            Status = PrefillHistoryEntryStatus.InProgress
        };

        context.PrefillHistoryEntries.Add(entry);
        await context.SaveChangesAsync();

        _logger.LogDebug("Started prefill entry for session {SessionId}: {AppName} ({AppId})",
            sessionId, appName, appId);

        return entry;
    }

    /// <summary>
    /// Updates a prefill entry when the game download completes.
    /// </summary>
    public async Task<PrefillHistoryEntry?> CompleteEntryAsync(
        string sessionId,
        string appId,
        string status,
        long bytesDownloaded,
        long totalBytes,
        string? errorMessage = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var entry = await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.AppId == appId && e.Status == PrefillHistoryEntryStatus.InProgress)
            .OrderByDescending(e => e.StartedAtUtc)
            .FirstOrDefaultAsync();

        if (entry == null)
        {
            _logger.LogWarning("No in-progress prefill entry found for session {SessionId}, app {AppId}",
                sessionId, appId);
            return null;
        }

        entry.CompletedAtUtc = DateTime.UtcNow;
        entry.Status = Enum.TryParse<PrefillHistoryEntryStatus>(status, ignoreCase: true, out var parsedStatus)
            ? parsedStatus
            : PrefillHistoryEntryStatus.Completed;
        entry.BytesDownloaded = bytesDownloaded;
        entry.TotalBytes = totalBytes;
        entry.ErrorMessage = errorMessage;

        await context.SaveChangesAsync();

        _logger.LogInformation("Completed prefill entry for session {SessionId}: {AppName} ({AppId}) - {Status}, Bytes: {Bytes}/{Total}",
            sessionId, entry.AppName, appId, status, bytesDownloaded, totalBytes);

        return entry;
    }

    /// <summary>
    /// Gets prefill history for a session.
    /// </summary>
    public async Task<List<PrefillHistoryEntry>> GetHistoryAsync(string sessionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.PrefillHistoryEntries
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.StartedAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Gets the current in-progress prefill entry for a session.
    /// </summary>
    public async Task<PrefillHistoryEntry?> GetCurrentEntryAsync(string sessionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.PrefillHistoryEntries
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.Status == PrefillHistoryEntryStatus.InProgress)
            .OrderByDescending(e => e.StartedAtUtc)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Marks all in-progress entries for a session as cancelled.
    /// Called when a prefill operation is cancelled or session terminates.
    /// </summary>
    public async Task CancelEntriesAsync(string sessionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var entries = await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.Status == PrefillHistoryEntryStatus.InProgress)
            .ToListAsync();

        foreach (var entry in entries)
        {
            entry.CompletedAtUtc = DateTime.UtcNow;
            entry.Status = PrefillHistoryEntryStatus.Cancelled;
        }

        if (entries.Count > 0)
        {
            await context.SaveChangesAsync();
            _logger.LogDebug("Cancelled {Count} prefill entries for session {SessionId}", entries.Count, sessionId);
        }
    }

    #endregion

    #region Admin Operations

    /// <summary>
    /// Bans a user by session ID.
    /// For sessions that captured a game username (Steam/Epic) the ban keys on that username.
    /// For anonymous sessions with no username (e.g. Battle.net) the ban falls back to the
    /// shared lancache-manager auth-session id (<see cref="PrefillSession.CreatedBySessionId"/>,
    /// the DaemonSession.UserId GUID) so anonymous prefill users remain bannable.
    /// Returns null only when the session itself cannot be found.
    /// </summary>
    public async Task<BannedSteamUser?> BanUserBySessionAsync(
        string sessionId,
        string? reason = null,
        string? bannedBy = null,
        DateTime? expiresAt = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session == null)
        {
            _logger.LogWarning("Cannot ban user for session {SessionId} - session not found", sessionId);
            return null;
        }

        var bannedBySessionId = session.CreatedBySessionId == Guid.Empty
            ? null
            : session.CreatedBySessionId.ToString();

        // Username-based ban (Steam/Epic): preserve the existing behavior exactly.
        if (!string.IsNullOrEmpty(session.SteamUsername))
        {
            return await BanUserAsync(
                session.SteamUsername,
                reason,
                bannedBySessionId,
                bannedBy,
                expiresAt);
        }

        // Anonymous session (e.g. Battle.net): ban by the shared auth-session UserId.
        if (session.CreatedBySessionId == Guid.Empty)
        {
            _logger.LogWarning("Cannot ban user for session {SessionId} - no username and no auth-session id", sessionId);
            return null;
        }

        return await BanByUserIdAsync(
            session.CreatedBySessionId,
            reason,
            bannedBySessionId,
            bannedBy,
            expiresAt);
    }

    #endregion
}
