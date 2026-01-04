using LancacheManager.Application.DTOs;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for client group management
/// Handles CRUD operations for client groups and member management
/// </summary>
[ApiController]
[Route("api/client-groups")]
public class ClientGroupsController : ControllerBase
{
    private readonly IClientGroupsRepository _clientGroupsRepository;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<ClientGroupsController> _logger;

    public ClientGroupsController(
        IClientGroupsRepository clientGroupsRepository,
        IHubContext<DownloadHub> hubContext,
        ILogger<ClientGroupsController> logger)
    {
        _clientGroupsRepository = clientGroupsRepository;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Get all client groups
    /// </summary>
    [HttpGet]
    [RequireAuth]
    public async Task<IActionResult> GetAll()
    {
        try
        {
            var groups = await _clientGroupsRepository.GetAllGroupsAsync();
            var dtos = groups.Select(ToDto).ToList();
            return Ok(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all client groups");
            return Ok(new List<ClientGroupDto>());
        }
    }

    /// <summary>
    /// Get a single client group by ID
    /// </summary>
    [HttpGet("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> GetById(int id)
    {
        try
        {
            var group = await _clientGroupsRepository.GetGroupByIdAsync(id);
            if (group == null)
            {
                return NotFound(ApiResponse.NotFound("Client group"));
            }
            return Ok(ToDto(group));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting client group {Id}", id);
            return StatusCode(500, ApiResponse.InternalError("getting client group"));
        }
    }

    /// <summary>
    /// Create a new client group
    /// </summary>
    [HttpPost]
    [RequireAuth]
    public async Task<IActionResult> Create([FromBody] CreateClientGroupRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Nickname))
            {
                return BadRequest(ApiResponse.Required("Nickname"));
            }

            // Check for duplicate nickname
            var existing = await _clientGroupsRepository.GetGroupByNicknameAsync(request.Nickname);
            if (existing != null)
            {
                return BadRequest(ApiResponse.Duplicate("client group", "nickname"));
            }

            var group = new ClientGroup
            {
                Nickname = request.Nickname.Trim(),
                Description = request.Description?.Trim()
            };

            var created = await _clientGroupsRepository.CreateGroupAsync(group);

            // Add initial IPs if provided
            if (request.InitialIps?.Count > 0)
            {
                foreach (var ip in request.InitialIps)
                {
                    try
                    {
                        await _clientGroupsRepository.AddMemberAsync(created.Id, ip.Trim());
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogWarning("Could not add IP {Ip} to group: {Message}", ip, ex.Message);
                    }
                }
                // Refresh to get updated members
                created = await _clientGroupsRepository.GetGroupByIdAsync(created.Id) ?? created;
            }

            var dto = ToDto(created);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("ClientGroupCreated", dto);

            return Created($"/api/client-groups/{created.Id}", dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating client group");
            return StatusCode(500, ApiResponse.InternalError("creating client group"));
        }
    }

    /// <summary>
    /// Update an existing client group
    /// </summary>
    [HttpPut("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateClientGroupRequest request)
    {
        try
        {
            var existing = await _clientGroupsRepository.GetGroupByIdAsync(id);
            if (existing == null)
            {
                return NotFound(ApiResponse.NotFound("Client group"));
            }

            if (string.IsNullOrWhiteSpace(request.Nickname))
            {
                return BadRequest(ApiResponse.Required("Nickname"));
            }

            // Check for duplicate nickname (excluding self)
            var duplicate = await _clientGroupsRepository.GetGroupByNicknameAsync(request.Nickname);
            if (duplicate != null && duplicate.Id != id)
            {
                return BadRequest(ApiResponse.Duplicate("client group", "nickname"));
            }

            existing.Nickname = request.Nickname.Trim();
            existing.Description = request.Description?.Trim();

            var updated = await _clientGroupsRepository.UpdateGroupAsync(existing);
            var dto = ToDto(updated);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("ClientGroupUpdated", dto);

            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating client group {Id}", id);
            return StatusCode(500, ApiResponse.InternalError("updating client group"));
        }
    }

    /// <summary>
    /// Delete a client group
    /// </summary>
    [HttpDelete("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var existing = await _clientGroupsRepository.GetGroupByIdAsync(id);
            if (existing == null)
            {
                return NotFound(ApiResponse.NotFound("Client group"));
            }

            await _clientGroupsRepository.DeleteGroupAsync(id);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("ClientGroupDeleted", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting client group {Id}", id);
            return StatusCode(500, ApiResponse.InternalError("deleting client group"));
        }
    }

    /// <summary>
    /// Add an IP to a client group
    /// </summary>
    [HttpPost("{id:int}/members")]
    [RequireAuth]
    public async Task<IActionResult> AddMember(int id, [FromBody] AddMemberRequest request)
    {
        try
        {
            var group = await _clientGroupsRepository.GetGroupByIdAsync(id);
            if (group == null)
            {
                return NotFound(ApiResponse.NotFound("Client group"));
            }

            if (string.IsNullOrWhiteSpace(request.ClientIp))
            {
                return BadRequest(ApiResponse.Required("Client IP"));
            }

            await _clientGroupsRepository.AddMemberAsync(id, request.ClientIp.Trim());

            // Get updated group
            var updated = await _clientGroupsRepository.GetGroupByIdAsync(id);
            var dto = ToDto(updated!);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("ClientGroupMemberAdded", new ClientGroupMemberAdded(id, request.ClientIp.Trim()));

            return Ok(dto);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Error(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding member to client group {Id}", id);
            return StatusCode(500, ApiResponse.InternalError("adding member to client group"));
        }
    }

    /// <summary>
    /// Remove an IP from a client group
    /// </summary>
    [HttpDelete("{id:int}/members/{ip}")]
    [RequireAuth]
    public async Task<IActionResult> RemoveMember(int id, string ip)
    {
        try
        {
            var group = await _clientGroupsRepository.GetGroupByIdAsync(id);
            if (group == null)
            {
                return NotFound(ApiResponse.NotFound("Client group"));
            }

            await _clientGroupsRepository.RemoveMemberAsync(id, ip);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("ClientGroupMemberRemoved", new ClientGroupMemberRemoved(id, ip));

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing member from client group {Id}", id);
            return StatusCode(500, ApiResponse.InternalError("removing member from client group"));
        }
    }

    /// <summary>
    /// Get the IP to group mapping for efficient lookups
    /// </summary>
    [HttpGet("mapping")]
    [RequireAuth]
    public async Task<IActionResult> GetMapping()
    {
        try
        {
            var mapping = await _clientGroupsRepository.GetIpToGroupMappingAsync();
            var result = mapping.ToDictionary(
                kvp => kvp.Key,
                kvp => new { groupId = kvp.Value.GroupId, nickname = kvp.Value.Nickname }
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting IP to group mapping");
            return Ok(new Dictionary<string, object>());
        }
    }

    private static ClientGroupDto ToDto(ClientGroup group)
    {
        return new ClientGroupDto
        {
            Id = group.Id,
            Nickname = group.Nickname,
            Description = group.Description,
            CreatedAtUtc = group.CreatedAtUtc,
            UpdatedAtUtc = group.UpdatedAtUtc,
            MemberIps = group.Members.Select(m => m.ClientIp).OrderBy(ip => ip).ToList()
        };
    }
}
