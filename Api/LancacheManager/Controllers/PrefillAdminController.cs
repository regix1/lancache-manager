using LancacheManager.Application.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Admin endpoints for managing prefill sessions and Steam user bans.
/// Requires authentication.
/// </summary>
[ApiController]
[Route("api/prefill-admin")]
[RequireAuth]
public class PrefillAdminController : ControllerBase
{
    private readonly PrefillSessionService _sessionService;
    private readonly SteamPrefillDaemonService _daemonService;
    private readonly ILogger<PrefillAdminController> _logger;

    public PrefillAdminController(
        PrefillSessionService sessionService,
        SteamPrefillDaemonService daemonService,
        ILogger<PrefillAdminController> logger)
    {
        _sessionService = sessionService;
        _daemonService = daemonService;
        _logger = logger;
    }

    private string? GetDeviceId() =>
        HttpContext.Items.TryGetValue("DeviceId", out var deviceId) ? deviceId as string : null;

    #region Session Management

    /// <summary>
    /// Gets all prefill sessions (paginated).
    /// </summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<PrefillSessionsResponse>> GetSessions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null)
    {
        var (sessions, totalCount) = await _sessionService.GetSessionsAsync(page, pageSize, status);

        // Also get in-memory sessions for live data
        var liveSessions = _daemonService.GetAllSessions();

        // Enrich DB sessions with live data
        var enrichedSessions = sessions.Select(s =>
        {
            var liveSession = liveSessions.FirstOrDefault(ls => ls.Id == s.SessionId);
            return new PrefillSessionDto
            {
                Id = s.Id,
                SessionId = s.SessionId,
                DeviceId = s.DeviceId,
                ContainerId = s.ContainerId,
                ContainerName = s.ContainerName,
                SteamUsernameHash = s.SteamUsernameHash,
                Status = liveSession?.Status.ToString() ?? s.Status,
                IsAuthenticated = liveSession?.AuthState == DaemonAuthState.Authenticated || s.IsAuthenticated,
                IsPrefilling = liveSession?.IsPrefilling ?? s.IsPrefilling,
                CreatedAtUtc = s.CreatedAtUtc,
                EndedAtUtc = s.EndedAtUtc,
                ExpiresAtUtc = s.ExpiresAtUtc,
                TerminationReason = s.TerminationReason,
                TerminatedBy = s.TerminatedBy,
                IsLive = liveSession != null
            };
        }).ToList();

        return Ok(new PrefillSessionsResponse
        {
            Sessions = enrichedSessions,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>
    /// Gets all currently active (in-memory) sessions.
    /// </summary>
    [HttpGet("sessions/active")]
    public ActionResult<List<DaemonSessionDto>> GetActiveSessions()
    {
        var sessions = _daemonService.GetAllSessions()
            .Select(DaemonSessionDto.FromSession)
            .ToList();
        return Ok(sessions);
    }

    /// <summary>
    /// Terminates a specific session.
    /// </summary>
    [HttpPost("sessions/{sessionId}/terminate")]
    public async Task<ActionResult> TerminateSession(
        string sessionId,
        [FromBody] TerminateSessionRequest? request = null)
    {
        var adminDeviceId = GetDeviceId();
        var reason = request?.Reason ?? "Terminated by admin";
        var force = request?.Force ?? false;

        _logger.LogWarning("Admin {AdminId} terminating session {SessionId}: {Reason}",
            adminDeviceId, sessionId, reason);

        await _daemonService.TerminateSessionAsync(sessionId, reason, force, adminDeviceId);

        return Ok(new { message = "Session terminated" });
    }

    /// <summary>
    /// Terminates all active sessions.
    /// </summary>
    [HttpPost("sessions/terminate-all")]
    public async Task<ActionResult> TerminateAllSessions([FromBody] TerminateSessionRequest? request = null)
    {
        var adminDeviceId = GetDeviceId();
        var reason = request?.Reason ?? "All sessions terminated by admin";
        var force = request?.Force ?? true;

        var sessions = _daemonService.GetAllSessions().ToList();
        var count = sessions.Count;

        _logger.LogWarning("Admin {AdminId} terminating all {Count} sessions: {Reason}",
            adminDeviceId, count, reason);

        foreach (var session in sessions)
        {
            await _daemonService.TerminateSessionAsync(session.Id, reason, force, adminDeviceId);
        }

        return Ok(new { message = $"Terminated {count} sessions" });
    }

    #endregion

    #region Ban Management

    /// <summary>
    /// Gets all active bans.
    /// </summary>
    [HttpGet("bans")]
    public async Task<ActionResult<List<BannedSteamUserDto>>> GetBans([FromQuery] bool includeLifted = false)
    {
        var bans = includeLifted
            ? await _sessionService.GetAllBansAsync()
            : await _sessionService.GetActiveBansAsync();

        return Ok(bans.Select(b => new BannedSteamUserDto
        {
            Id = b.Id,
            UsernameHash = b.UsernameHash,
            BanReason = b.BanReason,
            BannedDeviceId = b.BannedDeviceId,
            BannedAtUtc = b.BannedAtUtc,
            BannedBy = b.BannedBy,
            ExpiresAtUtc = b.ExpiresAtUtc,
            IsLifted = b.IsLifted,
            LiftedAtUtc = b.LiftedAtUtc,
            LiftedBy = b.LiftedBy,
            IsActive = !b.IsLifted && (b.ExpiresAtUtc == null || b.ExpiresAtUtc > DateTime.UtcNow)
        }).ToList());
    }

    /// <summary>
    /// Bans a Steam user by session ID.
    /// Looks up the username hash from the session.
    /// </summary>
    [HttpPost("bans/by-session/{sessionId}")]
    public async Task<ActionResult<BannedSteamUserDto>> BanBySession(
        string sessionId,
        [FromBody] BanRequest request)
    {
        var adminDeviceId = GetDeviceId();

        var ban = await _sessionService.BanUserBySessionAsync(
            sessionId,
            request.Reason,
            adminDeviceId,
            request.ExpiresAt);

        if (ban == null)
        {
            return BadRequest(new { message = "Could not ban user - session has no username hash. User may not have logged in yet." });
        }

        // Also terminate the session
        await _daemonService.TerminateSessionAsync(sessionId, "Banned by admin", true, adminDeviceId);

        _logger.LogWarning("Admin {AdminId} banned Steam user from session {SessionId}. Hash: {Hash}, Reason: {Reason}",
            adminDeviceId, sessionId, ban.UsernameHash[..8], request.Reason);

        return Ok(new BannedSteamUserDto
        {
            Id = ban.Id,
            UsernameHash = ban.UsernameHash,
            BanReason = ban.BanReason,
            BannedDeviceId = ban.BannedDeviceId,
            BannedAtUtc = ban.BannedAtUtc,
            BannedBy = ban.BannedBy,
            ExpiresAtUtc = ban.ExpiresAtUtc,
            IsLifted = ban.IsLifted,
            IsActive = true
        });
    }

    /// <summary>
    /// Bans a Steam user by username hash.
    /// </summary>
    [HttpPost("bans")]
    public async Task<ActionResult<BannedSteamUserDto>> BanByHash([FromBody] BanByHashRequest request)
    {
        if (string.IsNullOrEmpty(request.UsernameHash) || request.UsernameHash.Length != 64)
        {
            return BadRequest(new { message = "Invalid username hash. Must be a 64-character SHA-256 hash." });
        }

        var adminDeviceId = GetDeviceId();

        var ban = await _sessionService.BanUserAsync(
            request.UsernameHash,
            request.Reason,
            request.DeviceId,
            adminDeviceId,
            request.ExpiresAt);

        _logger.LogWarning("Admin {AdminId} banned Steam user by hash. Hash: {Hash}, Reason: {Reason}",
            adminDeviceId, ban.UsernameHash[..8], request.Reason);

        return Ok(new BannedSteamUserDto
        {
            Id = ban.Id,
            UsernameHash = ban.UsernameHash,
            BanReason = ban.BanReason,
            BannedDeviceId = ban.BannedDeviceId,
            BannedAtUtc = ban.BannedAtUtc,
            BannedBy = ban.BannedBy,
            ExpiresAtUtc = ban.ExpiresAtUtc,
            IsLifted = ban.IsLifted,
            IsActive = true
        });
    }

    /// <summary>
    /// Lifts a ban.
    /// </summary>
    [HttpPost("bans/{banId}/lift")]
    public async Task<ActionResult> LiftBan(int banId)
    {
        var adminDeviceId = GetDeviceId();

        var success = await _sessionService.LiftBanAsync(banId, adminDeviceId);

        if (!success)
        {
            return NotFound(new { message = "Ban not found or already lifted" });
        }

        _logger.LogInformation("Admin {AdminId} lifted ban {BanId}", adminDeviceId, banId);

        return Ok(new { message = "Ban lifted" });
    }

    #endregion
}

#region DTOs

public class PrefillSessionsResponse
{
    public List<PrefillSessionDto> Sessions { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class PrefillSessionDto
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public string? SteamUsernameHash { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; }
    public bool IsPrefilling { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? TerminationReason { get; set; }
    public string? TerminatedBy { get; set; }
    public bool IsLive { get; set; }
}

public class BannedSteamUserDto
{
    public int Id { get; set; }
    public string UsernameHash { get; set; } = string.Empty;
    public string? BanReason { get; set; }
    public string? BannedDeviceId { get; set; }
    public DateTime BannedAtUtc { get; set; }
    public string? BannedBy { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsLifted { get; set; }
    public DateTime? LiftedAtUtc { get; set; }
    public string? LiftedBy { get; set; }
    public bool IsActive { get; set; }
}

public class TerminateSessionRequest
{
    public string? Reason { get; set; }
    public bool Force { get; set; }
}

public class BanRequest
{
    public string? Reason { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class BanByHashRequest
{
    public string UsernameHash { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? DeviceId { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

#endregion
