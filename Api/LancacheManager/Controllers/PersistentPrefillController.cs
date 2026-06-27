using System.Security.Cryptography;
using System.Text;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Admin-only management of persistent (long-lived) prefill daemon sessions. These sessions own a
/// stable named auth volume so an admin login survives stop/start cycles; the reaper never tears
/// them down and instead flags <see cref="DaemonSession.NeedsRelogin"/> once past their expiry.
/// Mirrors <see cref="ScheduledPrefillConfigController"/> for base route/auth style and reuses the
/// exact daemon-per-platform resolution + system user id derivation from <c>ScheduledPrefillService</c>.
/// </summary>
[ApiController]
[Route("api/system/prefill/persistent")]
[Authorize(Policy = "AdminOnly")]
public class PersistentPrefillController : ControllerBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IStateService _stateService;
    private readonly PrefillCacheService _cacheService;

    /// <summary>
    /// Deterministic pseudo-user Guid that owns every persistent session, derived identically to
    /// <c>ScheduledPrefillService.DeriveSystemUserId()</c> (SHA-256 of
    /// <see cref="ScheduledPrefillConstants.SystemUserId"/>) so both code paths agree on identity.
    /// </summary>
    private readonly Guid _systemUserId = DeriveSystemUserId();

    public PersistentPrefillController(
        IServiceProvider serviceProvider,
        IStateService stateService,
        PrefillCacheService cacheService)
    {
        _serviceProvider = serviceProvider;
        _stateService = stateService;
        _cacheService = cacheService;
    }

    /// <summary>
    /// Starts a persistent admin-owned session for the given platform.
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult<DaemonSessionDto>> StartAsync([FromBody] StartPersistentSessionRequest request)
    {
        var daemon = ResolveDaemon(_serviceProvider, request.Service);
        if (daemon is null)
        {
            return BadRequest($"No daemon registered for service '{request.Service}'");
        }

        DaemonSession session = await daemon.StartPersistentSessionAsync(request.Service, _systemUserId);
        return Ok(DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Stops a persistent session, preserving its named auth volume for a later restart.
    /// </summary>
    [HttpPost("stop")]
    public async Task<ActionResult> StopAsync([FromBody] StopPersistentSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return BadRequest("sessionId is required");
        }

        foreach (var daemon in ResolveAllDaemons(_serviceProvider))
        {
            if (daemon.GetSession(request.SessionId) is not null)
            {
                await daemon.StopPersistentSessionAsync(request.SessionId, terminatedBy: "admin");
                return Ok();
            }
        }

        return Ok();
    }

    /// <summary>
    /// Lists every persistent session across all daemons.
    /// </summary>
    [HttpGet("list")]
    public async Task<ActionResult<List<PersistentPrefillSessionDto>>> ListAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var results = new List<PersistentPrefillSessionDto>();

        foreach (var daemon in ResolveAllDaemons(_serviceProvider))
        {
            foreach (DaemonSession session in daemon.GetAllSessions())
            {
                if (!session.IsPersistent)
                {
                    continue;
                }

                var remaining = (session.ExpiresAt - nowUtc).TotalSeconds;
                long remainingSeconds = remaining > 0 ? (long)remaining : 0L;
                var isRunning = session.Status == DaemonSessionStatus.Active;

                // Query the daemon's REAL token expiry only for running sessions. Resilient: a single
                // failing/slow status call must never sink the whole list, so it is per-session try/caught.
                DateTimeOffset? daemonAuthExpiresAtUtc = null;
                if (isRunning)
                {
                    try
                    {
                        var status = await daemon.GetSessionStatusAsync(session.Id, cancellationToken);
                        daemonAuthExpiresAtUtc = status?.AuthExpiryUtc;
                    }
                    catch
                    {
                        daemonAuthExpiresAtUtc = null;
                    }
                }

                results.Add(new PersistentPrefillSessionDto
                {
                    SessionId = session.Id,
                    Service = ParsePlatform(session.Platform),
                    IsRunning = isRunning,
                    AuthExpiresAtUtc = session.ExpiresAt,
                    AuthTimeRemainingSeconds = remainingSeconds,
                    NeedsRelogin = session.NeedsRelogin,
                    DaemonAuthExpiresAtUtc = daemonAuthExpiresAtUtc
                });
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Lists the owned games (and up-to-date cached app ids) for the RUNNING persistent session of a
    /// platform. This is the AdminOnly analogue of the user-scoped
    /// <c>GET {service}/sessions/{id}/games</c> route: that route enforces
    /// <c>ValidateSessionOwnership</c> (session.UserId == caller), which always 403s for persistent
    /// system-owned sessions whose owner is the derived system user, not the admin's session id.
    /// Bypassing ownership is safe here because the endpoint is <c>[Authorize(Policy = "AdminOnly")]</c>
    /// and is hard-restricted to sessions whose <see cref="DaemonSession.IsPersistent"/> is true.
    /// Reuses the exact same daemon method the user route calls
    /// (<see cref="PrefillDaemonServiceBase.GetOwnedGamesAsync(string, CancellationToken)"/>) so there
    /// is no game-list duplication.
    /// </summary>
    [HttpGet("games")]
    public async Task<ActionResult<PersistentPrefillGamesDto>> GetGamesAsync(
        [FromQuery] PrefillPlatform service,
        CancellationToken cancellationToken)
    {
        var daemon = ResolveDaemon(_serviceProvider, service);
        if (daemon is null)
        {
            return BadRequest($"No daemon registered for service '{service}'");
        }

        var session = daemon.GetAllSessions()
            .FirstOrDefault(s => s.IsPersistent && s.Status == DaemonSessionStatus.Active);
        if (session is null)
        {
            return NotFound($"No running persistent session for service '{service}'");
        }

        // Defense-in-depth: never operate on a non-persistent session here.
        if (!session.IsPersistent)
        {
            return Forbid();
        }

        var games = await daemon.GetOwnedGamesAsync(session.Id, cancellationToken);

        var cachedAppIds = new List<string>();
        var cachedApps = await _cacheService.GetCachedAppsAsync();
        var candidateAppIds = cachedApps.Select(a => a.AppId.ToString()).ToList();
        if (candidateAppIds.Count > 0)
        {
            var status = await daemon.GetCacheStatusAsync(session.Id, candidateAppIds, cancellationToken);
            cachedAppIds = status.Apps.Where(a => a.IsUpToDate).Select(a => a.AppId).ToList();
        }

        return Ok(new PersistentPrefillGamesDto
        {
            Games = games,
            CachedAppIds = cachedAppIds
        });
    }

    /// <summary>
    /// Returns the admin-configured persistent login validity window in days.
    /// </summary>
    [HttpGet("validity")]
    public ActionResult<PersistentLoginValidityDto> GetValidity()
    {
        return Ok(new PersistentLoginValidityDto
        {
            Days = _stateService.GetAdminPersistentLoginValidityDays()
        });
    }

    /// <summary>
    /// Updates the admin-configured persistent login validity window (1-365 days).
    /// </summary>
    [HttpPut("validity")]
    public ActionResult<PersistentLoginValidityDto> SetValidity([FromBody] PersistentLoginValidityDto request)
    {
        try
        {
            _stateService.SetAdminPersistentLoginValidityDays(request.Days);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(ex.Message);
        }

        return Ok(new PersistentLoginValidityDto
        {
            Days = _stateService.GetAdminPersistentLoginValidityDays()
        });
    }

    /// <summary>
    /// Returns the admin-configured guest temp-container max lifetime in hours.
    /// </summary>
    [HttpGet("guest-lifetime")]
    public ActionResult<GuestPrefillLifetimeDto> GetGuestLifetime()
    {
        return Ok(new GuestPrefillLifetimeDto
        {
            Hours = _stateService.GetGuestPrefillMaxLifetimeHours()
        });
    }

    /// <summary>
    /// Updates the admin-configured guest temp-container max lifetime (1-3 hours).
    /// </summary>
    [HttpPut("guest-lifetime")]
    public ActionResult<GuestPrefillLifetimeDto> SetGuestLifetime([FromBody] GuestPrefillLifetimeDto request)
    {
        try
        {
            _stateService.SetGuestPrefillMaxLifetimeHours(request.Hours);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(ex.Message);
        }

        return Ok(new GuestPrefillLifetimeDto
        {
            Hours = _stateService.GetGuestPrefillMaxLifetimeHours()
        });
    }

    /// <summary>
    /// Resolves the concrete daemon singleton for a platform. Mirrors
    /// <c>ScheduledPrefillService.ResolveDaemon</c> exactly.
    /// </summary>
    private static PrefillDaemonServiceBase? ResolveDaemon(IServiceProvider provider, PrefillPlatform platform)
    {
        switch (platform)
        {
            case PrefillPlatform.Steam:
                return provider.GetRequiredService<SteamDaemonService>();
            case PrefillPlatform.Epic:
                return provider.GetRequiredService<EpicPrefillDaemonService>();
            case PrefillPlatform.Xbox:
                return provider.GetRequiredService<XboxPrefillDaemonService>();
            case PrefillPlatform.BattleNet:
                return provider.GetRequiredService<BattleNetDaemonService>();
            case PrefillPlatform.Riot:
                return provider.GetRequiredService<RiotDaemonService>();
            default:
                return null;
        }
    }

    private static IEnumerable<PrefillDaemonServiceBase> ResolveAllDaemons(IServiceProvider provider)
    {
        yield return provider.GetRequiredService<SteamDaemonService>();
        yield return provider.GetRequiredService<EpicPrefillDaemonService>();
        yield return provider.GetRequiredService<XboxPrefillDaemonService>();
        yield return provider.GetRequiredService<BattleNetDaemonService>();
        yield return provider.GetRequiredService<RiotDaemonService>();
    }

    private static PrefillPlatform ParsePlatform(string platformName)
    {
        if (Enum.TryParse<PrefillPlatform>(platformName, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return PrefillPlatform.Steam;
    }

    /// <summary>
    /// Derives the stable system user Guid identically to <c>ScheduledPrefillService.DeriveSystemUserId()</c>.
    /// </summary>
    private static Guid DeriveSystemUserId()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ScheduledPrefillConstants.SystemUserId));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}

/// <summary>Request body for starting a persistent session.</summary>
public sealed class StartPersistentSessionRequest
{
    /// <summary>Platform whose daemon should own the persistent session.</summary>
    public required PrefillPlatform Service { get; init; }
}

/// <summary>Request body for stopping a persistent session.</summary>
public sealed class StopPersistentSessionRequest
{
    /// <summary>Id of the persistent session to stop.</summary>
    public required string SessionId { get; init; }
}

/// <summary>Typed view of a persistent prefill session.</summary>
public sealed class PersistentPrefillSessionDto
{
    /// <summary>Daemon session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>Platform that owns the session.</summary>
    public required PrefillPlatform Service { get; init; }

    /// <summary>True while the daemon session is in the Active status.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>UTC instant at which the persistent login validity expires (DaemonSession.ExpiresAt).</summary>
    public required DateTime AuthExpiresAtUtc { get; init; }

    /// <summary>Seconds remaining until <see cref="AuthExpiresAtUtc"/> (0 once elapsed).</summary>
    public required long AuthTimeRemainingSeconds { get; init; }

    /// <summary>True when the session is past expiry and the admin must re-authenticate in place.</summary>
    public required bool NeedsRelogin { get; init; }

    /// <summary>
    /// The daemon's REAL underlying token expiry queried live from its <c>status</c> command
    /// (Steam JWT ValidTo / Epic refresh_expires_at / Xbox refresh-token expiry). Distinct from
    /// <see cref="AuthExpiresAtUtc"/>, which is the manager's 90-day persistent login-validity window.
    /// Null when the session is not running, the status call fails, or the daemon does not report it.
    /// </summary>
    public DateTimeOffset? DaemonAuthExpiresAtUtc { get; init; }
}

/// <summary>Persistent login validity window, in days.</summary>
public sealed class PersistentLoginValidityDto
{
    /// <summary>Validity window in days (1-365).</summary>
    public required int Days { get; init; }
}

/// <summary>Guest temp-container max lifetime, in hours.</summary>
public sealed class GuestPrefillLifetimeDto
{
    /// <summary>Max guest container lifetime in hours (1-3).</summary>
    public required int Hours { get; init; }
}

/// <summary>
/// Owned games plus up-to-date cached app ids for a persistent session. Matches the shape the
/// frontend GameSelectionModal expects (games[], cachedAppIds[]).
/// </summary>
public sealed class PersistentPrefillGamesDto
{
    /// <summary>Owned games for the persistent session (same payload as the user games route).</summary>
    public required List<OwnedGame> Games { get; init; }

    /// <summary>App ids whose cached content is up to date for the session.</summary>
    public required List<string> CachedAppIds { get; init; }
}
