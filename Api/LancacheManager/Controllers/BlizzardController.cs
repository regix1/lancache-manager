using LancacheManager.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BlizzardController : ControllerBase
{
    private readonly BlizzardService _blizzardService;
    private readonly ILogger<BlizzardController> _logger;

    public BlizzardController(BlizzardService blizzardService, ILogger<BlizzardController> logger)
    {
        _blizzardService = blizzardService;
        _logger = logger;
    }

    /// <summary>
    /// Build chunk mappings for a Blizzard product
    /// </summary>
    [HttpPost("build-mappings")]
    public async Task<IActionResult> BuildMappings([FromBody] BuildMappingsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Product))
        {
            return BadRequest(new { error = "Product code is required" });
        }

        _logger.LogInformation("Building mappings for {Product}", request.Product);

        var result = await _blizzardService.BuildMappingsAsync(
            request.Product,
            request.LanguageFilter,
            request.PlatformFilter,
            HttpContext.RequestAborted);

        if (result.Success)
        {
            return Ok(result);
        }
        else
        {
            return StatusCode(500, result);
        }
    }

    /// <summary>
    /// Get chunk info for a specific archive index and byte offset
    /// </summary>
    [HttpGet("chunk-info")]
    public IActionResult GetChunkInfo(
        [FromQuery] string product,
        [FromQuery] int archiveIndex,
        [FromQuery] uint byteOffset)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return BadRequest(new { error = "Product code is required" });
        }

        var info = _blizzardService.GetFileForChunk(product, archiveIndex, byteOffset);

        if (info != null)
        {
            return Ok(info);
        }
        else
        {
            return NotFound(new { error = "Chunk mapping not found" });
        }
    }

    /// <summary>
    /// Get statistics about stored chunk mappings
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var stats = await _blizzardService.GetStatsAsync();
        return Ok(stats);
    }

    /// <summary>
    /// Clear all mappings for a specific product
    /// </summary>
    [HttpDelete("clear-product/{product}")]
    public async Task<IActionResult> ClearProductMappings(string product)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return BadRequest(new { error = "Product code is required" });
        }

        var count = await _blizzardService.ClearProductMappingsAsync(product);
        return Ok(new { product, mappingsCleared = count });
    }

    /// <summary>
    /// Discover all available Blizzard products by querying the CDN
    /// </summary>
    [HttpPost("discover-products")]
    public async Task<IActionResult> DiscoverProducts([FromQuery] bool forceRefresh = false)
    {
        _logger.LogInformation("Discovering Blizzard products (forceRefresh={ForceRefresh})", forceRefresh);

        var products = await _blizzardService.DiscoverProductsAsync(forceRefresh);

        return Ok(new
        {
            count = products.Count,
            products = products,
            cached = !forceRefresh
        });
    }

    /// <summary>
    /// Get list of discovered products (from cache, no CDN queries)
    /// </summary>
    [HttpGet("products")]
    public IActionResult GetProducts()
    {
        var products = _blizzardService.GetDiscoveredProducts();

        return Ok(new
        {
            count = products.Count,
            products = products
        });
    }

    /// <summary>
    /// Validate a specific product code
    /// </summary>
    [HttpGet("validate-product/{productCode}")]
    public async Task<IActionResult> ValidateProduct(string productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return BadRequest(new { error = "Product code is required" });
        }

        var info = await _blizzardService.ValidateProductAsync(productCode);

        if (info != null)
        {
            return Ok(info);
        }
        else
        {
            return NotFound(new
            {
                error = $"Product '{productCode}' not found or inactive",
                productCode
            });
        }
    }
}

public class BuildMappingsRequest
{
    public string Product { get; set; } = string.Empty;
    /// <summary>
    /// Optional language filter (e.g., "enUS", "frFR"). Leave null/empty to include all languages.
    /// </summary>
    public string? LanguageFilter { get; set; } = null;
    /// <summary>
    /// Optional platform filter (e.g., "Windows", "Mac"). Leave null/empty to include all platforms.
    /// </summary>
    public string? PlatformFilter { get; set; } = null;
}
