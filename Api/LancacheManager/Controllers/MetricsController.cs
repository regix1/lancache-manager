using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Services;
using LancacheManager.Security;
using LancacheManager.Constants;
using System.Text;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly StatsService _statsService;
    private readonly CacheManagementService _cacheService;
    private readonly ApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(
        StatsService statsService,
        CacheManagementService cacheService,
        ApiKeyService apiKeyService,
        IConfiguration configuration,
        ILogger<MetricsController> logger)
    {
        _statsService = statsService;
        _cacheService = cacheService;
        _apiKeyService = apiKeyService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet]
    [HttpGet("prometheus")]
    [Produces("text/plain")]
    [ConditionalAuth(LancacheConstants.CONFIG_KEY_REQUIRE_AUTH_METRICS, false)]
    public async Task<IActionResult> GetPrometheusMetrics()
    {
        try
        {
            var metrics = new StringBuilder();

            // Get current stats using StatsService
            var serviceStats = await _statsService.GetServiceStatsAsync();
            var clientStats = await _statsService.GetClientStatsAsync();
            var cacheInfo = _cacheService.GetCacheInfo();
            var activeDownloads = await _statsService.GetActiveDownloadCountAsync();
            
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
            metrics.AppendLine($"lancache_unique_clients {await _statsService.GetUniqueClientCountAsync()}");

            // Calculate total bandwidth saved (sum of all cache hits)
            var totalBandwidthSaved = await _statsService.GetTotalBandwidthSavedAsync();
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
    
    [HttpGet("status")]
    [Produces("application/json")]
    public IActionResult GetMetricsStatus()
    {
        var requireAuth = _configuration.GetValue<bool>(LancacheConstants.CONFIG_KEY_REQUIRE_AUTH_METRICS, false);
        return Ok(new 
        { 
            requiresAuthentication = requireAuth,
            status = requireAuth ? "Secured" : "Public"
        });
    }
    
    [HttpGet("json")]
    [Produces("application/json")]
    [ConditionalAuth(LancacheConstants.CONFIG_KEY_REQUIRE_AUTH_METRICS, false)]
    public async Task<IActionResult> GetJsonMetrics()
    {
        try
        {
            // Use StatsService for all database queries
            var serviceStats = await _statsService.GetServiceStatsAsync();
            var clientStats = await _statsService.GetClientStatsAsync();
            var cacheInfo = _cacheService.GetCacheInfo();
            var activeDownloads = await _statsService.GetActiveDownloadCountAsync();
                
            var recentDownloads = await _statsService.GetLatestDownloadsAsync(10);
            
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