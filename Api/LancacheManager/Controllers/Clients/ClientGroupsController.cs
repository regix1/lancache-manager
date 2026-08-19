using System.Net;
using LancacheManager.Controllers.Base;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for client group management
/// Handles CRUD operations for client groups and member management
/// </summary>
[ApiController]
[Route("api/client-groups")]
// The policy sits at class level so it also covers the list and by-id reads inherited from
// CrudControllerBase, which EventsController inherits too and which stay guest-readable there.
[Authorize(Policy = "AccountHolder")]
public class ClientGroupsController : CrudControllerBase<ClientGroup, ClientGroupDto, CreateClientGroupRequest, UpdateClientGroupRequest, long>
{
    private readonly IClientGroupsService _clientGroupsRepository;
    private readonly IDashboardBatchService _dashboardBatchService;

    protected override string ResourceName => "Client group";

    public ClientGroupsController(
        IClientGroupsService clientGroupsRepository,
        ISignalRNotificationService notifications,
        ILogger<ClientGroupsController> logger,
        IDashboardBatchService dashboardBatchService)
        : base(clientGroupsRepository, notifications, logger)
    {
        _clientGroupsRepository = clientGroupsRepository;
        _dashboardBatchService = dashboardBatchService;
    }

    // ===== Abstract Method Implementations =====

    protected override ClientGroupDto ToDto(ClientGroup group) => group.ToDto();

    protected override ClientGroup FromCreateRequest(CreateClientGroupRequest request)
    {
        return new ClientGroup
        {
            Nickname = request.Nickname.Trim(),
            Description = request.Description?.Trim(),
            SeparateMemberRows = request.SeparateMemberRows
        };
    }

    protected override void ApplyUpdate(ClientGroup entity, UpdateClientGroupRequest request)
    {
        entity.Nickname = request.Nickname.Trim();
        entity.Description = request.Description?.Trim();
        entity.SeparateMemberRows = request.SeparateMemberRows!.Value;
    }

    /// <summary>
    /// Validates a client-group create request against the database.
    /// </summary>
    /// <remarks>
    /// Basic validation (required fields, format) is handled by FluentValidation.
    /// This method handles business logic validation that requires database access.
    /// </remarks>
    protected override async Task ValidateCreateAsync(CreateClientGroupRequest request, CancellationToken ct)
    {
        // Basic validation is handled automatically by FluentValidation (see CreateClientGroupRequestValidator)
        // Check for duplicate nickname (business logic validation)
        var existing = await _clientGroupsRepository.GetByNicknameAsync(request.Nickname, ct);
        if (existing != null)
        {
            throw new ValidationException("A client group with this nickname already exists");
        }
    }

    /// <summary>
    /// Validates a client-group update request against the database.
    /// </summary>
    /// <remarks>
    /// Basic validation (required fields, format) is handled by FluentValidation.
    /// This method handles business logic validation that requires database access.
    /// </remarks>
    protected override async Task ValidateUpdateAsync(long id, UpdateClientGroupRequest request, ClientGroup existingEntity, CancellationToken ct)
    {
        // Basic validation is handled automatically by FluentValidation (see UpdateClientGroupRequestValidator)
        // Check for duplicate nickname (excluding self) - business logic validation
        var duplicate = await _clientGroupsRepository.GetByNicknameAsync(request.Nickname, ct);
        if (duplicate != null && duplicate.Id != id)
        {
            throw new ValidationException("A client group with this nickname already exists");
        }
    }

    // ===== SignalR Notifications =====

    // The dashboard batch's clients section carries group nicknames, so every membership or
    // nickname write must expire the live batch BEFORE the event goes out - otherwise the
    // refetch the event triggers is served the previous nickname for the rest of the window.

    protected override async Task OnCreatedAsync(ClientGroup entity, ClientGroupDto dto)
    {
        _dashboardBatchService.InvalidateLiveCache();
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupCreated, dto);
    }

    protected override async Task OnUpdatedAsync(ClientGroup entity, ClientGroupDto dto)
    {
        _dashboardBatchService.InvalidateLiveCache();
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupUpdated, dto);
    }

    protected override async Task OnDeletedAsync(long id)
    {
        _dashboardBatchService.InvalidateLiveCache();
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupDeleted, id);
    }

    // ===== Post-Create Hook =====

    protected override async Task<ClientGroup> PostCreateAsync(ClientGroup entity, CreateClientGroupRequest request, CancellationToken ct)
    {
        // Add initial IPs if provided
        if (request.InitialIps?.Count > 0)
        {
            var desiredIps = NormalizeMemberIps(request.InitialIps, out _);
            await _clientGroupsRepository.SetMembersAsync(entity.Id, desiredIps, ct);

            // Refresh to get updated members
            entity = await _clientGroupsRepository.GetByIdAsync(entity.Id, ct) ?? entity;
        }
        return entity;
    }

    // ===== Override Create to return Created with location =====

    /// <summary>
    /// Creates a client group, optionally assigning initial addresses to it.
    /// </summary>
    /// <remarks>
    /// An address that could not be taken (already owned by another group, or not a valid
    /// address) is named in <see cref="CreateClientGroupResponse.RejectedIps"/> rather than
    /// silently dropped; if none of the requested addresses could be taken, the group is not kept.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(CreateClientGroupResponse), StatusCodes.Status201Created)]
    public override async Task<IActionResult> CreateAsync([FromBody] CreateClientGroupRequest request, CancellationToken ct = default)
    {
        await ValidateCreateAsync(request, ct);

        var entity = FromCreateRequest(request);
        var created = await _repository.CreateAsync(entity, ct);
        created = await PostCreateAsync(created, request, ct);

        var dto = ToDto(created);

        // An address the caller asked for that is not on the group afterwards did not make it - it is
        // already named by another group, or it was not an address. Naming it here is what stops a
        // group being created with fewer addresses than the user chose while the response reads as a
        // clean success.
        var rejectedIps = RejectedInitialIps(request.InitialIps, dto.MemberIps);

        // Not one of the addresses the caller chose could be taken, so keeping the group would leave a
        // nickname holding nothing they asked for behind a response that reads as a success. Removing
        // it puts them back where they started, with the addresses that blocked it named. A create
        // that asked for no addresses is a different thing and still succeeds.
        if (rejectedIps.Count > 0 && dto.MemberIps.Count == 0)
        {
            await _clientGroupsRepository.DeleteAsync(created.Id, ct);

            _logger.LogWarning(
                "Discarded {Resource} {Nickname}: none of the {Count} requested addresses could be taken",
                ResourceName, created.Nickname, rejectedIps.Count);

            return Conflict(new RejectedClientIpsResponse
            {
                Error = RejectedClientIpsResponse.ErrorCode,
                RejectedIps = rejectedIps
            });
        }

        await OnCreatedAsync(created, dto);

        _logger.LogInformation("Created {Resource}: {Id}", ResourceName, created.Id);

        var response = new CreateClientGroupResponse
        {
            Id = dto.Id,
            Nickname = dto.Nickname,
            Description = dto.Description,
            SeparateMemberRows = dto.SeparateMemberRows,
            CreatedAtUtc = dto.CreatedAtUtc,
            UpdatedAtUtc = dto.UpdatedAtUtc,
            MemberIps = dto.MemberIps,
            RejectedIps = rejectedIps
        };

        return Created($"/api/client-groups/{created.Id}", response);
    }

    // ===== Custom Endpoints (not part of standard CRUD) =====

    /// <summary>
    /// Replace every IP in a client group with the full list the caller supplies
    /// </summary>
    /// <remarks>
    /// Basic per-item format validation is handled automatically by FluentValidation (see
    /// SetMembersRequestValidator). This method normalizes the list a second time so it can name the
    /// entries it turned down instead of failing the whole save on one of them.
    /// </remarks>
    [HttpPut("{id:long}/members")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(SetMembersResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetMembersAsync(long id, [FromBody] SetMembersRequest request, CancellationToken ct = default)
    {
        var group = await _clientGroupsRepository.GetByIdOrThrowAsync(id, ResourceName, ct);

        // The list is the whole membership, so saving one built from a copy someone else has since
        // changed erases their change with nothing to show for it. Handing the group back as it now
        // stands lets the caller start again from the current addresses without asking twice. A
        // caller that sends no stamp is not tracking the version and saves as before.
        if (request.ExpectedUpdatedAtUtc is { } expectedUpdatedAt && !IsUnchangedSince(group, expectedUpdatedAt))
        {
            return Conflict(new ClientGroupChangedResponse
            {
                Error = ClientGroupChangedResponse.ErrorCode,
                CurrentGroup = ToDto(group)
            });
        }

        var desiredIps = NormalizeMemberIps(request.ClientIps, out var invalidIps);
        if (invalidIps.Count > 0)
        {
            return BadRequest(new InvalidClientIpsResponse
            {
                Error = "One or more addresses are not valid. Please correct them and try again.",
                InvalidIps = invalidIps
            });
        }

        var rejectedIps = await _clientGroupsRepository.SetMembersAsync(id, desiredIps, ct);

        var updated = await _clientGroupsRepository.GetByIdAsync(id, ct);
        if (updated is null)
        {
            // A delete that landed between the save and this re-read leaves nothing to report,
            // and a 500 would hide an outcome the caller can act on.
            return NotFound();
        }

        var dto = ToDto(updated);

        // Membership decides how client stats rows are built in every time range, not just the live
        // one, so this goes out as a group update: the notification dispatch expires the whole
        // dashboard batch before the event reaches any client, which a live-only expiry cannot do.
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupUpdated, dto);

        return Ok(new SetMembersResponse { Group = dto, RejectedIps = rejectedIps });
    }

    /// <summary>
    /// Returns the full IP-address-to-group mapping in one call.
    /// </summary>
    /// <remarks>
    /// For callers that need to resolve many addresses at once (client stats, dashboard batch)
    /// without a lookup per address. Has no UI caller of its own; it exists as a bulk-lookup API
    /// for scripted or external use.
    /// </remarks>
    [HttpGet("mapping")]
    [ProducesResponseType(typeof(Dictionary<string, ClientGroupAssignment>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Dictionary<string, ClientGroupAssignment>>> GetMappingAsync(CancellationToken ct = default)
    {
        var mapping = await _clientGroupsRepository.GetIpMappingAsync(ct);
        return Ok(mapping);
    }

    /// <summary>
    /// Updates a client group's fields (nickname, description, row-display setting).
    /// </summary>
    /// <remarks>
    /// Membership is not touched here; addresses are managed separately through
    /// <see cref="SetMembersAsync"/>. A request carrying <c>ExpectedUpdatedAtUtc</c> that no
    /// longer matches the stored group is rejected with the group as it now stands, so a caller
    /// editing a stale copy does not silently overwrite someone else's change.
    /// </remarks>
    [HttpPut("{id}")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(ClientGroupDto), StatusCodes.Status200OK)]
    public override async Task<IActionResult> UpdateAsync(long id, [FromBody] UpdateClientGroupRequest request, CancellationToken ct = default)
    {
        var group = await _clientGroupsRepository.GetByIdOrThrowAsync(id, ResourceName, ct);

        // An edit session writes the fields before it writes the addresses, and this write moves the
        // stamp, so a stamp checked only on the address save can never see anything but what this
        // write just produced. Checking it here, at the first write, is what lets it be compared
        // against the copy the editor started from. Handing the group back as it now stands lets the
        // caller start again from it without asking twice, and a caller that sends no stamp is not
        // tracking the version and writes as before.
        if (request.ExpectedUpdatedAtUtc is { } expectedUpdatedAt && !IsUnchangedSince(group, expectedUpdatedAt))
        {
            return Conflict(new ClientGroupChangedResponse
            {
                Error = ClientGroupChangedResponse.ErrorCode,
                CurrentGroup = ToDto(group)
            });
        }

        return await base.UpdateAsync(id, request, ct);
    }

    /// <summary>
    /// Deletes a client group.
    /// </summary>
    /// <remarks>
    /// Member addresses are not deleted; they simply stop being assigned to a group and revert to
    /// reporting individually.
    /// </remarks>
    [HttpDelete("{id}")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public override Task<IActionResult> DeleteAsync(long id, CancellationToken ct = default)
        => base.DeleteAsync(id, ct);

    // ===== Address list handling =====

    /// <summary>
    /// Whether the group still carries the stamp the caller read it at. A group that has never been
    /// stamped has nothing to compare against, so an expectation against one never matches - the
    /// caller is working from something this server did not hand out.
    /// </summary>
    private static bool IsUnchangedSince(ClientGroup group, DateTime expectedUpdatedAt)
    {
        if (group.UpdatedAtUtc is not { } currentUpdatedAt)
        {
            return false;
        }

        // A stamp sent back with a zone offset rather than the Z it was handed out with still names
        // the same instant, so it is compared as one.
        var expectedUtc = expectedUpdatedAt.Kind == DateTimeKind.Local
            ? expectedUpdatedAt.ToUniversalTime()
            : expectedUpdatedAt;

        return currentUpdatedAt.Ticks == expectedUtc.Ticks;
    }

    /// <summary>
    /// Trims, parses and de-duplicates a requested address list, collecting the entries that are not
    /// addresses into <paramref name="invalidIps"/> rather than discarding them. Blank entries are
    /// dropped silently - an empty row in the payload is not something to report back.
    /// </summary>
    private static List<string> NormalizeMemberIps(IEnumerable<string>? clientIps, out List<string> invalidIps)
    {
        invalidIps = new List<string>();
        var normalized = new List<string>();

        if (clientIps == null)
        {
            return normalized;
        }

        foreach (var rawIp in clientIps)
        {
            var trimmed = rawIp?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var parsed))
            {
                invalidIps.Add(trimmed);
                continue;
            }

            var normalizedIp = parsed.ToString();
            if (!normalized.Contains(normalizedIp, StringComparer.Ordinal))
            {
                normalized.Add(normalizedIp);
            }
        }

        return normalized;
    }

    /// <summary>
    /// The addresses a create request asked for that the new group does not hold, whatever the reason.
    /// </summary>
    private static List<string> RejectedInitialIps(IEnumerable<string>? initialIps, List<string> memberIps)
    {
        if (initialIps == null)
        {
            return new List<string>();
        }

        var applied = memberIps.ToHashSet(StringComparer.Ordinal);

        return initialIps
            .Select(ip => ip?.Trim())
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => IPAddress.TryParse(ip, out var parsed) ? parsed.ToString() : ip!)
            .Distinct(StringComparer.Ordinal)
            .Where(ip => !applied.Contains(ip))
            .ToList();
    }
}
