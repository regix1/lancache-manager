using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Services;
using LancacheManager.Security;
using System.Text;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly CacheManagementService _cacheService;
    private readonly ApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(
        AppDbContext context, 
        CacheManagementService cacheService, 
        ApiKeyService apiKeyService,
        IConfiguration configuration,
        ILogger<MetricsController> logger)
    {
        _context = context;
        _cacheService = cacheService;
        _apiKeyService = apiKeyService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    [HttpGet("prometheus")]
    [Produces("text/plain")]
    public async Task<IActionResult> GetPrometheusMetrics()
    {
        // Check if metrics require authentication (configurable)
        var requireAuth = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);
        
        if (requireAuth)
        {
            var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !_apiKeyService.ValidateApiKey(apiKey))
            {
                return Unauthorized(new { error = "API key required for metrics access" });
            }
        }
        
        try
        {
            var metrics = new StringBuilder();
            
            // Get current stats
            var serviceStats = await _context.ServiceStats
                .AsNoTracking()
                .ToListAsync();
                
            var clientStats = await _context.ClientStats
                .AsNoTracking()
                .ToListAsync();
                
            var cacheInfo = _cacheService.GetCacheInfo();
                
            var activeDownloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive)
                .CountAsync();
            
            // Service metrics
            metrics.AppendLine("# HELP lancache_service_cache_hit_bytes Total cache hit bytes per service");
            metrics.AppendLine("# TYPE lancache_service_cache_hit_bytes counter");
            foreach (var service in serviceStats)
            {
                metrics.AppendLine($"lancache_service_cache_hit_bytes{{service=\"{service.Service}\"}} {service.TotalCacheHitBytes}");
            }
            
            metrics.AppendLine("# HELP lancache_service_cache_miss_bytes Total cache miss bytes per service");
            metrics.AppendLine("# TYPE lancache_service_cache_miss_bytes counter");
            foreach (var service in serviceStats)
            {
                metrics.AppendLine($"lancache_service_cache_miss_bytes{{service=\"{service.Service}\"}} {service.TotalCacheMissBytes}");
            }
            
            metrics.AppendLine("# HELP lancache_service_hit_ratio Cache hit ratio per service");
            metrics.AppendLine("# TYPE lancache_service_hit_ratio gauge");
            foreach (var service in serviceStats)
            {
                metrics.AppendLine($"lancache_service_hit_ratio{{service=\"{service.Service}\"}} {service.CacheHitPercent / 100.0}");
            }
            
            metrics.AppendLine("# HELP lancache_service_download_count Total downloads per service");
            metrics.AppendLine("# TYPE lancache_service_download_count counter");
            foreach (var service in serviceStats)
            {
                metrics.AppendLine($"lancache_service_download_count{{service=\"{service.Service}\"}} {service.TotalDownloads}");
            }
            
            // Client metrics
            metrics.AppendLine("# HELP lancache_client_cache_hit_bytes Total cache hit bytes per client");
            metrics.AppendLine("# TYPE lancache_client_cache_hit_bytes counter");
            foreach (var client in clientStats)
            {
                metrics.AppendLine($"lancache_client_cache_hit_bytes{{client=\"{client.ClientIp}\"}} {client.TotalCacheHitBytes}");
            }
            
            metrics.AppendLine("# HELP lancache_client_cache_miss_bytes Total cache miss bytes per client");
            metrics.AppendLine("# TYPE lancache_client_cache_miss_bytes counter");
            foreach (var client in clientStats)
            {
                metrics.AppendLine($"lancache_client_cache_miss_bytes{{client=\"{client.ClientIp}\"}} {client.TotalCacheMissBytes}");
            }
            
            // Cache storage metrics
            if (cacheInfo != null)
            {
                metrics.AppendLine("# HELP lancache_cache_total_bytes Total cache storage capacity");
                metrics.AppendLine("# TYPE lancache_cache_total_bytes gauge");
                metrics.AppendLine($"lancache_cache_total_bytes {cacheInfo.TotalCacheSize}");
                
                metrics.AppendLine("# HELP lancache_cache_used_bytes Used cache storage");
                metrics.AppendLine("# TYPE lancache_cache_used_bytes gauge");
                metrics.AppendLine($"lancache_cache_used_bytes {cacheInfo.UsedCacheSize}");
                
                metrics.AppendLine("# HELP lancache_cache_free_bytes Free cache storage");
                metrics.AppendLine("# TYPE lancache_cache_free_bytes gauge");
                metrics.AppendLine($"lancache_cache_free_bytes {cacheInfo.FreeCacheSize}");
                
                metrics.AppendLine("# HELP lancache_cache_usage_ratio Cache usage ratio");
                metrics.AppendLine("# TYPE lancache_cache_usage_ratio gauge");
                metrics.AppendLine($"lancache_cache_usage_ratio {cacheInfo.UsagePercent / 100.0}");
                
                metrics.AppendLine("# HELP lancache_cache_file_count Total files in cache");
                metrics.AppendLine("# TYPE lancache_cache_file_count gauge");
                metrics.AppendLine($"lancache_cache_file_count {cacheInfo.TotalFiles}");
            }
            
            // Active downloads
            metrics.AppendLine("# HELP lancache_active_downloads Currently active downloads");
            metrics.AppendLine("# TYPE lancache_active_downloads gauge");
            metrics.AppendLine($"lancache_active_downloads {activeDownloads}");
            
            // Total unique clients
            metrics.AppendLine("# HELP lancache_unique_clients Total unique clients");
            metrics.AppendLine("# TYPE lancache_unique_clients gauge");
            metrics.AppendLine($"lancache_unique_clients {clientStats.Count}");
            
            // Calculate total bandwidth saved (sum of all cache hits)
            var totalBandwidthSaved = serviceStats.Sum(s => s.TotalCacheHitBytes);
            metrics.AppendLine("# HELP lancache_bandwidth_saved_bytes Total bandwidth saved");
            metrics.AppendLine("# TYPE lancache_bandwidth_saved_bytes counter");
            metrics.AppendLine($"lancache_bandwidth_saved_bytes {totalBandwidthSaved}");
            
            return Content(metrics.ToString(), "text/plain; version=0.0.4; charset=utf-8");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating Prometheus metrics");
            return StatusCode(500, "Error generating metrics");
        }
    }
    
    [HttpGet("json")]
    [Produces("application/json")]
    public async Task<IActionResult> GetJsonMetrics()
    {
        // Check if metrics require authentication (configurable)
        var requireAuth = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);
        
        if (requireAuth)
        {
            var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey) || !_apiKeyService.ValidateApiKey(apiKey))
            {
                return Unauthorized(new { error = "API key required for metrics access" });
            }
        }
        
        try
        {
            var serviceStats = await _context.ServiceStats
                .AsNoTracking()
                .ToListAsync();
                
            var clientStats = await _context.ClientStats
                .AsNoTracking()
                .ToListAsync();
                
            var cacheInfo = _cacheService.GetCacheInfo();
                
            var activeDownloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.IsActive)
                .CountAsync();
                
            var recentDownloads = await _context.Downloads
                .AsNoTracking()
                .OrderByDescending(d => d.StartTime)
                .Take(10)
                .ToListAsync();
            
            return Ok(new
            {
                timestamp = DateTime.UtcNow,
                services = serviceStats,
                clients = clientStats,
                cache = cacheInfo,
                activeDownloads,
                uniqueClients = clientStats.Count,
                totalBandwidthSaved = serviceStats.Sum(s => s.TotalCacheHitBytes),
                totalServed = serviceStats.Sum(s => s.TotalCacheHitBytes + s.TotalCacheMissBytes),
                recentDownloads
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating JSON metrics");
            return StatusCode(500, new { error = "Error generating metrics" });
        }
    }
}