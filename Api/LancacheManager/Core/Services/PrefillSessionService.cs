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
            .Where(b => b.Username == normalizedUsername && !b.IsLifted)
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
    /// Lifts a ban for a Steam user.
    /// </summary>
    public async Task<BannedSteamUser?> LiftBanAsync(int banId, string? liftedBy = null)
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
        string createdBySessionId,
        string? containerId,
        string? containerName,
        DateTime expiresAt)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = new PrefillSession
        {
            SessionId = sessionId,
            CreatedBySessionId = createdBySessionId,
            ContainerId = containerId,
            ContainerName = containerName,
            Status = "Active",
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
    /// Updates the Steam username for a session.
    /// Called when the user provides their username credential.
    /// </summary>
    public async Task SetSessionUsernameAsync(string sessionId, string username)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session != null)
        {
            session.SteamUsername = username.Trim().ToLowerInvariant();
            await context.SaveChangesAsync();

            _logger.LogDebug("Set username for session {SessionId}: {Username}", sessionId, username);
        }
    }

    /// <summary>
    /// Updates the authentication status for a session.
    /// </summary>
    public async Task SetSessionAuthenticatedAsync(string sessionId, bool isAuthenticated)
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
    public async Task SetSessionPrefillingAsync(string sessionId, bool isPrefilling)
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

        if (session != null && session.Status == "Active")
        {
            session.Status = "Terminated";
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
            .Where(s => s.Status == "Active")
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Gets all sessions (paginated).
    /// </summary>
    public async Task<(List<PrefillSession> Sessions, int TotalCount)> GetSessionsAsync(
        int page = 1,
        int pageSize = 20,
        string? statusFilter = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.PrefillSessions.AsQueryable();

        if (!string.IsNullOrEmpty(statusFilter))
        {
            query = query.Where(s => s.Status == statusFilter);
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
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);
    }

    /// <summary>
    /// Marks orphaned sessions (sessions marked as Active in DB but not in memory).
    /// Called on startup to detect containers that may still be running.
    /// </summary>
    public async Task<List<PrefillSession>> MarkOrphanedSessionsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var activeSessions = await context.PrefillSessions
            .Where(s => s.Status == "Active")
            .ToListAsync();

        foreach (var session in activeSessions)
        {
            session.Status = "Orphaned";
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
            .Where(s => s.Status == "Orphaned" && s.ContainerId != null)
            .Select(s => s.ContainerId!)
            .ToListAsync();
    }

    /// <summary>
    /// Marks an orphaned session as cleaned up.
    /// </summary>
    public async Task MarkOrphanedSessionCleanedAsync(string containerId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.ContainerId == containerId && s.Status == "Orphaned");

        if (session != null)
        {
            session.Status = "Cleaned";
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
    public async Task<PrefillHistoryEntry?> StartPrefillEntryAsync(
        string sessionId,
        string appId,
        string? appName)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check if this app was just completed or cached (within last 5 seconds) - don't create duplicate
        var recentCutoff = DateTime.UtcNow.AddSeconds(-5);
        var recentlyFinished = await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.AppId == appId)
            .Where(e => (e.Status == "Completed" || e.Status == "Cached") && e.CompletedAtUtc != null && e.CompletedAtUtc > recentCutoff)
            .AnyAsync();

        if (recentlyFinished)
        {
            _logger.LogDebug("Skipping InProgress entry for app {AppId} - was just completed or cached", appId);
            return null;
        }

        // Clean up any stale "InProgress" entries for this app in this session
        // This prevents duplicate entries when prefill is restarted or interrupted
        var staleEntries = await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.AppId == appId && e.Status == "InProgress")
            .ToListAsync();

        if (staleEntries.Count > 0)
        {
            foreach (var stale in staleEntries)
            {
                stale.Status = "Cancelled";
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
            Status = "InProgress"
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
    public async Task<PrefillHistoryEntry?> CompletePrefillEntryAsync(
        string sessionId,
        string appId,
        string status,
        long bytesDownloaded,
        long totalBytes,
        string? errorMessage = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var entry = await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.AppId == appId && e.Status == "InProgress")
            .OrderByDescending(e => e.StartedAtUtc)
            .FirstOrDefaultAsync();

        if (entry == null)
        {
            _logger.LogWarning("No in-progress prefill entry found for session {SessionId}, app {AppId}",
                sessionId, appId);
            return null;
        }

        entry.CompletedAtUtc = DateTime.UtcNow;
        entry.Status = status;
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
    public async Task<List<PrefillHistoryEntry>> GetPrefillHistoryAsync(string sessionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.StartedAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Gets the current in-progress prefill entry for a session.
    /// </summary>
    public async Task<PrefillHistoryEntry?> GetCurrentPrefillEntryAsync(string sessionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        return await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.Status == "InProgress")
            .OrderByDescending(e => e.StartedAtUtc)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Marks all in-progress entries for a session as cancelled.
    /// Called when a prefill operation is cancelled or session terminates.
    /// </summary>
    public async Task CancelPrefillEntriesAsync(string sessionId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var entries = await context.PrefillHistoryEntries
            .Where(e => e.SessionId == sessionId && e.Status == "InProgress")
            .ToListAsync();

        foreach (var entry in entries)
        {
            entry.CompletedAtUtc = DateTime.UtcNow;
            entry.Status = "Cancelled";
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
    /// Bans a user by session ID (looks up the username from the session).
    /// </summary>
    public async Task<BannedSteamUser?> BanUserBySessionAsync(
        string sessionId,
        string? reason = null,
        string? bannedBy = null,
        DateTime? expiresAt = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (string.IsNullOrEmpty(session?.SteamUsername))
        {
            _logger.LogWarning("Cannot ban user for session {SessionId} - no username found", sessionId);
            return null;
        }

        return await BanUserAsync(
            session.SteamUsername,
            reason,
            session.CreatedBySessionId,
            bannedBy,
            expiresAt);
    }

    #endregion
}
