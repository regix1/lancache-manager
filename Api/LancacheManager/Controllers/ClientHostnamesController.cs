using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// Reverse-DNS names for the client addresses that appear in the tables, and the global toggle
/// that turns the lookup on. Reading the names is ordinary authenticated use; flipping the toggle
/// changes what every viewer sees, so it is admin-only.
/// </summary>
[ApiController]
[Route("api/clients/hostnames")]
[Authorize]
public class ClientHostnamesController : ControllerBase
{
    /// <summary>
    /// How many client addresses one response covers, most recently active first. A name is a
    /// cosmetic label for rows that are on screen, so the oldest addresses can wait for a later
    /// request rather than widening the DNS fan-out.
    /// </summary>
    private const int MaxClientsResolved = 256;

    private readonly AppDbContext _context;
    private readonly IClientHostnameService _hostnameService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IStateService _stateService;

    public ClientHostnamesController(
        AppDbContext context,
        IClientHostnameService hostnameService,
        ISignalRNotificationService notifications,
        IStateService stateService)
    {
        _context = context;
        _hostnameService = hostnameService;
        _notifications = notifications;
        _stateService = stateService;
    }

    [HttpGet("")]
    public async Task<ActionResult<ClientHostnamesResponse>> GetHostnamesAsync(CancellationToken ct)
    {
        if (!_hostnameService.IsEnabled())
        {
            return Ok(new ClientHostnamesResponse { Enabled = false });
        }

        // Clients an admin hid or excluded from stats appear on no screen, so a name for one is a
        // reverse query that buys nothing and a slot taken from a machine that is on screen. The
        // filters are the ones both client-stats surfaces apply. [34]
        var statsExcludedOnlyIps = _stateService.GetStatsExcludedOnlyClientIps();
        var query = _context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(_stateService.GetHiddenClientIps())
            .ApplyEvictedFilter(_stateService.GetEvictedDataMode());
        if (statsExcludedOnlyIps.Count > 0)
        {
            query = query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp));
        }

        // The recency cut is part of the query so the database returns at most MaxClientsResolved
        // rows, the same shape the top-clients grouping already uses.
        var clientIps = await query
            .GroupBy(d => d.ClientIp)
            .Select(g => new
            {
                ClientIp = g.Key,
                LastActivityUtc = g.Max(d => d.StartTimeUtc)
            })
            .OrderByDescending(c => c.LastActivityUtc)
            .Take(MaxClientsResolved)
            .Select(c => c.ClientIp)
            .ToListAsync(ct);

        var hostnames = await _hostnameService.ResolveAsync(clientIps, ct);

        return Ok(new ClientHostnamesResponse
        {
            Enabled = true,
            Hostnames = new Dictionary<string, string>(hostnames, StringComparer.OrdinalIgnoreCase)
        });
    }

    [HttpPost("enabled")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<SetClientHostnameLookupResponse>> SetEnabledAsync(
        [FromBody] SetClientHostnameLookupRequest request)
    {
        _hostnameService.SetEnabled(request.Enabled);

        // The toggle relabels every client-stats row in every time range, so the broadcast has to
        // expire the whole dashboard batch, not just its live window.
        await _notifications.NotifyAllAsync(SignalREvents.ClientHostnamesChanged);

        return Ok(new SetClientHostnameLookupResponse { Enabled = request.Enabled });
    }
}
