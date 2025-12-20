using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for tag management
/// Handles CRUD operations for freeform tags and download tagging
/// </summary>
[ApiController]
[Route("api/tags")]
public class TagsController : ControllerBase
{
    private readonly ITagsRepository _tagsRepository;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<TagsController> _logger;

    public TagsController(
        ITagsRepository tagsRepository,
        IHubContext<DownloadHub> hubContext,
        ILogger<TagsController> logger)
    {
        _tagsRepository = tagsRepository;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Get all tags
    /// </summary>
    [HttpGet]
    [RequireAuth]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetAll()
    {
        try
        {
            var tags = await _tagsRepository.GetAllTagsAsync();
            return Ok(tags);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all tags");
            return Ok(new List<Tag>());
        }
    }

    /// <summary>
    /// Get a single tag by ID
    /// </summary>
    [HttpGet("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> GetById(int id)
    {
        try
        {
            var tag = await _tagsRepository.GetTagByIdAsync(id);
            if (tag == null)
            {
                return NotFound(new { error = "Tag not found" });
            }
            return Ok(tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tag {Id}", id);
            return StatusCode(500, new { error = "Failed to get tag" });
        }
    }

    /// <summary>
    /// Create a new tag
    /// </summary>
    [HttpPost]
    [RequireAuth]
    public async Task<IActionResult> Create([FromBody] CreateTagRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { error = "Tag name is required" });
            }

            // Check for duplicate name
            var existing = await _tagsRepository.GetTagByNameAsync(request.Name);
            if (existing != null)
            {
                return BadRequest(new { error = "A tag with this name already exists" });
            }

            var tag = new Tag
            {
                Name = request.Name.Trim(),
                Color = request.Color ?? "#6b7280",
                Description = request.Description
            };

            var created = await _tagsRepository.CreateTagAsync(tag);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("TagCreated", created);

            return Created($"/api/tags/{created.Id}", created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating tag");
            return StatusCode(500, new { error = "Failed to create tag" });
        }
    }

    /// <summary>
    /// Update an existing tag
    /// </summary>
    [HttpPut("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateTagRequest request)
    {
        try
        {
            var existing = await _tagsRepository.GetTagByIdAsync(id);
            if (existing == null)
            {
                return NotFound(new { error = "Tag not found" });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { error = "Tag name is required" });
            }

            // Check for duplicate name (excluding this tag)
            var duplicate = await _tagsRepository.GetTagByNameAsync(request.Name);
            if (duplicate != null && duplicate.Id != id)
            {
                return BadRequest(new { error = "A tag with this name already exists" });
            }

            existing.Name = request.Name.Trim();
            existing.Color = request.Color ?? existing.Color;
            existing.Description = request.Description;

            var updated = await _tagsRepository.UpdateTagAsync(existing);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("TagUpdated", updated);

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating tag {Id}", id);
            return StatusCode(500, new { error = "Failed to update tag" });
        }
    }

    /// <summary>
    /// Delete a tag
    /// </summary>
    [HttpDelete("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var existing = await _tagsRepository.GetTagByIdAsync(id);
            if (existing == null)
            {
                return NotFound(new { error = "Tag not found" });
            }

            await _tagsRepository.DeleteTagAsync(id);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("TagDeleted", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting tag {Id}", id);
            return StatusCode(500, new { error = "Failed to delete tag" });
        }
    }

    /// <summary>
    /// Get downloads with a specific tag
    /// </summary>
    [HttpGet("{id:int}/downloads")]
    [RequireAuth]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetDownloads(int id)
    {
        try
        {
            var tag = await _tagsRepository.GetTagByIdAsync(id);
            if (tag == null)
            {
                return NotFound(new { error = "Tag not found" });
            }

            var downloads = await _tagsRepository.GetDownloadsWithTagAsync(id);
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting downloads for tag {Id}", id);
            return Ok(new List<Download>());
        }
    }

    /// <summary>
    /// Add a tag to a download
    /// </summary>
    [HttpPost("{tagId:int}/downloads/{downloadId:int}")]
    [RequireAuth]
    public async Task<IActionResult> AddTagToDownload(int tagId, int downloadId)
    {
        try
        {
            var tag = await _tagsRepository.GetTagByIdAsync(tagId);
            if (tag == null)
            {
                return NotFound(new { error = "Tag not found" });
            }

            await _tagsRepository.AddTagToDownloadAsync(tagId, downloadId);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("DownloadTagAdded", new { tagId, downloadId, tag });

            return Ok(new { message = "Tag added to download" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding tag {TagId} to download {DownloadId}", tagId, downloadId);
            return StatusCode(500, new { error = "Failed to add tag to download" });
        }
    }

    /// <summary>
    /// Remove a tag from a download
    /// </summary>
    [HttpDelete("{tagId:int}/downloads/{downloadId:int}")]
    [RequireAuth]
    public async Task<IActionResult> RemoveTagFromDownload(int tagId, int downloadId)
    {
        try
        {
            await _tagsRepository.RemoveTagFromDownloadAsync(tagId, downloadId);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("DownloadTagRemoved", new { tagId, downloadId });

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing tag {TagId} from download {DownloadId}", tagId, downloadId);
            return StatusCode(500, new { error = "Failed to remove tag from download" });
        }
    }

    /// <summary>
    /// Get tags for a specific download
    /// </summary>
    [HttpGet("download/{downloadId:int}")]
    [RequireAuth]
    public async Task<IActionResult> GetTagsForDownload(int downloadId)
    {
        try
        {
            var tags = await _tagsRepository.GetTagsForDownloadAsync(downloadId);
            return Ok(tags);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tags for download {DownloadId}", downloadId);
            return Ok(new List<Tag>());
        }
    }

    /// <summary>
    /// Get usage count for a tag
    /// </summary>
    [HttpGet("{id:int}/usage")]
    [RequireAuth]
    public async Task<IActionResult> GetUsageCount(int id)
    {
        try
        {
            var tag = await _tagsRepository.GetTagByIdAsync(id);
            if (tag == null)
            {
                return NotFound(new { error = "Tag not found" });
            }

            var count = await _tagsRepository.GetTagUsageCountAsync(id);
            return Ok(new { tagId = id, usageCount = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage count for tag {Id}", id);
            return StatusCode(500, new { error = "Failed to get usage count" });
        }
    }
}

/// <summary>
/// Request model for creating a tag
/// </summary>
public class CreateTagRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Request model for updating a tag
/// </summary>
public class UpdateTagRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? Description { get; set; }
}
