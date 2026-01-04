using System.Security.Cryptography;
using System.Text;
using LancacheManager.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

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

    #region Username Hashing

    /// <summary>
    /// Hashes a Steam username using SHA-256.
    /// The username is normalized (lowercase, trimmed) before hashing.
    /// </summary>
    public static string HashUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return string.Empty;

        var normalized = username.Trim().ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    #endregion

    #region Ban Management

    /// <summary>
    /// Checks if a Steam username hash is banned.
    /// Returns true if the user is banned and the ban is active.
    /// </summary>
    public async Task<bool> IsUsernameBannedAsync(string usernameHash)
    {
        if (string.IsNullOrEmpty(usernameHash))
            return false;

        await using var context = await _contextFactory.CreateDbContextAsync();

        var ban = await context.BannedSteamUsers
            .Where(b => b.UsernameHash == usernameHash && !b.IsLifted)
            .Where(b => b.ExpiresAtUtc == null || b.ExpiresAtUtc > DateTime.UtcNow)
            .FirstOrDefaultAsync();

        return ban != null;
    }

    /// <summary>
    /// Checks if a plaintext Steam username is banned.
    /// </summary>
    public Task<bool> IsUsernameBannedByPlaintextAsync(string username)
    {
        var hash = HashUsername(username);
        return IsUsernameBannedAsync(hash);
    }

    /// <summary>
    /// Bans a Steam user by their username hash.
    /// </summary>
    public async Task<BannedSteamUser> BanUserAsync(
        string usernameHash,
        string? reason = null,
        string? deviceId = null,
        string? bannedBy = null,
        DateTime? expiresAt = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        // Check if already banned
        var existingBan = await context.BannedSteamUsers
            .Where(b => b.UsernameHash == usernameHash && !b.IsLifted)
            .FirstOrDefaultAsync();

        if (existingBan != null)
        {
            _logger.LogInformation("User with hash {Hash} is already banned", usernameHash[..8]);
            return existingBan;
        }

        var ban = new BannedSteamUser
        {
            UsernameHash = usernameHash,
            BanReason = reason,
            BannedDeviceId = deviceId,
            BannedBy = bannedBy ?? "admin",
            BannedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAt
        };

        context.BannedSteamUsers.Add(ban);
        await context.SaveChangesAsync();

        _logger.LogWarning("Banned Steam user with hash {Hash}. Reason: {Reason}",
            usernameHash[..8], reason ?? "No reason provided");

        return ban;
    }

    /// <summary>
    /// Lifts a ban for a Steam user.
    /// </summary>
    public async Task<bool> LiftBanAsync(int banId, string? liftedBy = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var ban = await context.BannedSteamUsers.FindAsync(banId);
        if (ban == null || ban.IsLifted)
            return false;

        ban.IsLifted = true;
        ban.LiftedAtUtc = DateTime.UtcNow;
        ban.LiftedBy = liftedBy ?? "admin";

        await context.SaveChangesAsync();

        _logger.LogInformation("Lifted ban {BanId} for user with hash {Hash}",
            banId, ban.UsernameHash[..8]);

        return true;
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
        string deviceId,
        string? containerId,
        string? containerName,
        DateTime expiresAt)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = new PrefillSession
        {
            SessionId = sessionId,
            DeviceId = deviceId,
            ContainerId = containerId,
            ContainerName = containerName,
            Status = "Active",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAt
        };

        context.PrefillSessions.Add(session);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created prefill session record {SessionId} for device {DeviceId}",
            sessionId, deviceId);

        return session;
    }

    /// <summary>
    /// Updates the Steam username hash for a session.
    /// Called when the user provides their username credential.
    /// </summary>
    public async Task SetSessionUsernameHashAsync(string sessionId, string usernameHash)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var session = await context.PrefillSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId);

        if (session != null)
        {
            session.SteamUsernameHash = usernameHash;
            await context.SaveChangesAsync();

            _logger.LogDebug("Set username hash for session {SessionId}", sessionId);
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

    #region Admin Operations

    /// <summary>
    /// Bans a user by session ID (looks up the username hash from the session).
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

        if (session?.SteamUsernameHash == null)
        {
            _logger.LogWarning("Cannot ban user for session {SessionId} - no username hash found", sessionId);
            return null;
        }

        return await BanUserAsync(
            session.SteamUsernameHash,
            reason,
            session.DeviceId,
            bannedBy,
            expiresAt);
    }

    #endregion
}
