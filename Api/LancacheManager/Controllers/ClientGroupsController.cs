using LancacheManager.Controllers.Base;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for client group management
/// Handles CRUD operations for client groups and member management
/// </summary>
[ApiController]
[Route("api/client-groups")]
[Authorize]
public class ClientGroupsController : CrudControllerBase<ClientGroup, ClientGroupDto, CreateClientGroupRequest, UpdateClientGroupRequest, int>
{
    private readonly IClientGroupsService _clientGroupsRepository;

    protected override string ResourceName => "Client group";

    public ClientGroupsController(
        IClientGroupsService clientGroupsRepository,
        ISignalRNotificationService notifications,
        ILogger<ClientGroupsController> logger)
        : base(clientGroupsRepository, notifications, logger)
    {
        _clientGroupsRepository = clientGroupsRepository;
    }

    // ===== Abstract Method Implementations =====

    protected override ClientGroupDto ToDto(ClientGroup group) => group.ToDto();

    protected override ClientGroup FromCreateRequest(CreateClientGroupRequest request)
    {
        return new ClientGroup
        {
            Nickname = request.Nickname.Trim(),
            Description = request.Description?.Trim()
        };
    }

    protected override void ApplyUpdate(ClientGroup entity, UpdateClientGroupRequest request)
    {
        entity.Nickname = request.Nickname.Trim();
        entity.Description = request.Description?.Trim();
    }

    /// <remarks>
    /// Basic validation (required fields, format) is handled by FluentValidation.
    /// This method handles business logic validation that requires database access.
    /// </remarks>
    protected override async Task ValidateCreateRequestAsync(CreateClientGroupRequest request, CancellationToken ct)
    {
        // Basic validation is handled automatically by FluentValidation (see CreateClientGroupRequestValidator)
        // Check for duplicate nickname (business logic validation)
        var existing = await _clientGroupsRepository.GetGroupByNicknameAsync(request.Nickname, ct);
        if (existing != null)
        {
            throw new ValidationException("A client group with this nickname already exists");
        }
    }

    /// <remarks>
    /// Basic validation (required fields, format) is handled by FluentValidation.
    /// This method handles business logic validation that requires database access.
    /// </remarks>
    protected override async Task ValidateUpdateRequestAsync(int id, UpdateClientGroupRequest request, ClientGroup existingEntity, CancellationToken ct)
    {
        // Basic validation is handled automatically by FluentValidation (see UpdateClientGroupRequestValidator)
        // Check for duplicate nickname (excluding self) - business logic validation
        var duplicate = await _clientGroupsRepository.GetGroupByNicknameAsync(request.Nickname, ct);
        if (duplicate != null && duplicate.Id != id)
        {
            throw new ValidationException("A client group with this nickname already exists");
        }
    }

    // ===== SignalR Notifications =====

    protected override async Task OnCreatedAsync(ClientGroup entity, ClientGroupDto dto)
    {
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupCreated, dto);
    }

    protected override async Task OnUpdatedAsync(ClientGroup entity, ClientGroupDto dto)
    {
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupUpdated, dto);
    }

    protected override async Task OnDeletedAsync(int id)
    {
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupDeleted, id);
    }

    // ===== Post-Create Hook =====

    protected override async Task<ClientGroup> PostCreateAsync(ClientGroup entity, CreateClientGroupRequest request, CancellationToken ct)
    {
        // Add initial IPs if provided
        if (request.InitialIps?.Count > 0)
        {
            foreach (var ip in request.InitialIps)
            {
                try
                {
                    await _clientGroupsRepository.AddMemberAsync(entity.Id, ip.Trim(), ct);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning("Could not add IP {Ip} to group: {Message}", ip, ex.Message);
                }
            }
            // Refresh to get updated members
            entity = await _clientGroupsRepository.GetGroupByIdAsync(entity.Id, ct) ?? entity;
        }
        return entity;
    }

    // ===== Override Create to return Created with location =====

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public override async Task<IActionResult> CreateAsync([FromBody] CreateClientGroupRequest request, CancellationToken ct = default)
    {
        await ValidateCreateRequestAsync(request, ct);

        var entity = FromCreateRequest(request);
        var created = await _repository.CreateAsync(entity, ct);
        created = await PostCreateAsync(created, request, ct);

        var dto = ToDto(created);
        await OnCreatedAsync(created, dto);

        _logger.LogInformation("Created {Resource}: {Id}", ResourceName, created.Id);
        return Created($"/api/client-groups/{created.Id}", dto);
    }

    // ===== Custom Endpoints (not part of standard CRUD) =====

    /// <summary>
    /// Add an IP to a client group
    /// </summary>
    /// <remarks>
    /// Validation is handled automatically by FluentValidation (see AddMemberRequestValidator)
    /// </remarks>
    [HttpPost("{id:int}/members")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> AddMemberAsync(int id, [FromBody] AddMemberRequest request, CancellationToken ct = default)
    {
        // Validation is handled automatically by FluentValidation
        var group = await _clientGroupsRepository.GetByIdOrThrowAsync(id, "Client group", ct);

        await _clientGroupsRepository.AddMemberAsync(id, request.ClientIp.Trim(), ct);

        // Get updated group
        var updated = await _clientGroupsRepository.GetGroupByIdAsync(id, ct);
        var dto = ToDto(updated!);

        // Notify clients via SignalR
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupMemberAdded, new ClientGroupMemberAdded(id, request.ClientIp.Trim()));

        return Ok(dto);
    }

    /// <summary>
    /// Remove an IP from a client group
    /// </summary>
    [HttpDelete("{id:int}/members/{ip}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RemoveMemberAsync(int id, string ip, CancellationToken ct = default)
    {
        var group = await _clientGroupsRepository.GetByIdOrThrowAsync(id, "Client group", ct);

        await _clientGroupsRepository.RemoveMemberAsync(id, ip, ct);

        // Notify clients via SignalR
        await _notifications.NotifyAllAsync(SignalREvents.ClientGroupMemberRemoved, new ClientGroupMemberRemoved(id, ip));

        return NoContent();
    }

    /// <summary>
    /// Get the IP to group mapping for efficient lookups
    /// </summary>
    [HttpGet("mapping")]
    public async Task<IActionResult> GetMappingAsync(CancellationToken ct = default)
    {
        var mapping = await _clientGroupsRepository.GetIpToGroupMappingAsync(ct);
        var result = mapping.ToDictionary(
            kvp => kvp.Key,
            kvp => new { groupId = kvp.Value.GroupId, nickname = kvp.Value.Nickname }
        );
        return Ok(result);
    }

    /// <summary>
    /// Update a client group (admin only)
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "AdminOnly")]
    public override Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateClientGroupRequest request, CancellationToken ct = default)
        => base.UpdateAsync(id, request, ct);

    /// <summary>
    /// Delete a client group (admin only)
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "AdminOnly")]
    public override Task<IActionResult> DeleteAsync(int id, CancellationToken ct = default)
        => base.DeleteAsync(id, ct);
}
