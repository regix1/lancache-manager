using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
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

    /// <summary>The longest a DNS name can be, so an oversized one is turned down before it is asked about.</summary>
    private const int MaxHostnameLength = 253;

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

    /// <summary>
    /// Returns reverse-DNS names for the client addresses currently visible in the tables.
    /// </summary>
    /// <remarks>
    /// Most recently active first up to <see cref="MaxClientsResolved"/>. Returns
    /// <c>Enabled = false</c> with no lookups performed when the hostname service is switched off.
    /// A guest is answered the same way until an admin allows guests to see client names, and is
    /// never told which DNS servers the app was configured to ask.
    /// </remarks>
    [HttpGet("")]
    [ProducesResponseType(typeof(ClientHostnamesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ClientHostnamesResponse>> GetHostnamesAsync(CancellationToken ct)
    {
        // The gate is here rather than in the browser: a guest that may not see names is answered
        // as though the lookup were off, so nothing has to be hidden client-side and no request
        // comes back as an error the page has to explain.
        var isAccountHolder = HttpContext.GetUserSession()?.SessionType.IsAccountHolder() == true;
        var maySeeNames = isAccountHolder || _hostnameService.IsVisibleToGuests();

        // Which servers the app asks is an admin's own configuration, so it travels only to an
        // account holder. It travels with the off state too, so the panel can show what is set
        // without the lookup having to be turned on to read it back.
        var settings = isAccountHolder ? _hostnameService.GetSettings() : new ClientHostnameSettings();

        if (!maySeeNames || !_hostnameService.IsEnabled())
        {
            return Ok(new ClientHostnamesResponse
            {
                Enabled = false,
                Settings = settings
            });
        }

        // Clients an admin hid or excluded from stats appear on no screen, so a name for one is a
        // reverse query that buys nothing and a slot taken from a machine that is on screen. The
        // filters are the ones both client-stats surfaces apply.
        var statsExcludedOnlyIps = _stateService.GetStatsExcludedOnlyClientIps();
        var query = _context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(_stateService.GetHiddenClientIps())
            .ApplyEvictedFilter(_stateService.GetEvictedDataMode())
            .ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);

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
            // One row past the cap on purpose: the lookup applies the same cap itself, and handing
            // it the extra address is what lets it tell the caller the list was cut instead of
            // answering as though every client on the network had been asked about.
            .Take(MaxClientsResolved + 1)
            .Select(c => c.ClientIp)
            .ToListAsync(ct);

        var outcome = await _hostnameService.ResolveAsync(clientIps, ct);

        return Ok(new ClientHostnamesResponse
        {
            Enabled = true,
            Hostnames = new Dictionary<string, string>(outcome.Hostnames, StringComparer.OrdinalIgnoreCase),
            Reason = outcome.Reason,
            Settings = settings
        });
    }

    /// <summary>
    /// Returns the addresses the network publishes for one hostname.
    /// </summary>
    /// <remarks>
    /// So a machine that has never downloaded anything can still be given a nickname. Admin-only
    /// because it is the nickname editor's own lookup and it sends a name typed into the browser
    /// out to the LAN's DNS server.
    /// </remarks>
    [HttpPost("resolve")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(ResolveClientAddressResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ResolveClientAddressResponse>> ResolveAddressesAsync(
        [FromBody] ResolveClientAddressRequest request,
        CancellationToken ct)
    {
        // The trailing dot is the root label and is accepted, because a name copied out of a zone
        // file carries one. Anything that is not a name is turned down here rather than sent on:
        // an address needs no lookup, and the picker offers it directly.
        var hostname = request.Hostname.Trim().TrimEnd('.');
        if (hostname.Length == 0 ||
            hostname.Length > MaxHostnameLength ||
            Uri.CheckHostName(hostname) != UriHostNameType.Dns)
        {
            return BadRequest(ApiResponse.Invalid("hostname is not a valid host name."));
        }

        var outcome = await _hostnameService.ResolveAddressesAsync(hostname, ct);

        return Ok(new ResolveClientAddressResponse
        {
            Hostname = hostname,
            Addresses = outcome.Addresses.ToList(),
            Reason = outcome.Reason
        });
    }

    /// <summary>
    /// Turns client hostname lookups on or off network-wide.
    /// </summary>
    /// <remarks>
    /// Broadcasts the change so every open client-stats view either starts resolving names or
    /// clears the ones it already showed.
    /// </remarks>
    [HttpPost("enabled")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(SetClientHostnameLookupResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetClientHostnameLookupResponse>> SetEnabledAsync(
        [FromBody] SetClientHostnameLookupRequest request)
    {
        _hostnameService.SetEnabled(request.Enabled);

        // The toggle relabels every client-stats row in every time range, so the broadcast has to
        // expire the whole dashboard batch, not just its live window.
        await _notifications.NotifyAllAsync(SignalREvents.ClientHostnamesChanged);

        return Ok(new SetClientHostnameLookupResponse { Enabled = request.Enabled });
    }

    /// <summary>
    /// Names the DNS server that hostname lookups ask first, or clears it.
    /// </summary>
    /// <remarks>
    /// For the networks where discovery cannot reach the server holding the reverse records: a
    /// container that only sees Docker's own resolver, or a router whose records sit behind a
    /// different DNS server than the one this host was handed. Only a private or loopback IPv4
    /// address is accepted, so this cannot be used to send the network's own machine names to a
    /// server on the internet. Clearing it hands the choice back to discovery.
    /// </remarks>
    [HttpPost("settings")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(ClientHostnameSettings), StatusCodes.Status200OK)]
    public async Task<ActionResult<ClientHostnameSettings>> SetSettingsAsync(
        [FromBody] ClientHostnameSettings request)
    {
        if (!_hostnameService.SetSettings(request))
        {
            return BadRequest(ApiResponse.Invalid(
                "resolver must be a private or loopback IPv4 address, or empty to discover one."));
        }

        // Changing which server is asked changes the names on every row, exactly as the toggle
        // does, so every open view is told to ask again rather than keep what the old server said.
        await _notifications.NotifyAllAsync(SignalREvents.ClientHostnamesChanged);

        return Ok(_hostnameService.GetSettings());
    }
}
