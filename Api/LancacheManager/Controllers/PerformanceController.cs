using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PerformanceController : ControllerBase
{
    private readonly LogProcessingService _processingService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PerformanceController> _logger;

    public PerformanceController(
        LogProcessingService processingService,
        IConfiguration configuration,
        ILogger<PerformanceController> logger)
    {
        _processingService = processingService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get current performance configuration
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetPerformanceConfig()
    {
        return Ok(new
        {
            ChannelCapacity = _configuration.GetValue<int>("LanCache:ChannelCapacity", 100000),
            BatchSize = _configuration.GetValue<int>("LanCache:BatchSize", 5000),
            BatchTimeoutMs = _configuration.GetValue<int>("LanCache:BatchTimeoutMs", 500),
            ConsumerCount = _configuration.GetValue<int>("LanCache:ConsumerCount", 4),
            ParserParallelism = _configuration.GetValue<int>("LanCache:ParserParallelism", 8),
            UseHighThroughputMode = _configuration.GetValue<bool>("LanCache:UseHighThroughputMode", false)
        });
    }

    /// <summary>
    /// Get current queue statistics
    /// </summary>
    [HttpGet("stats")]
    public IActionResult GetQueueStats()
    {
        try
        {
            var stats = _processingService.GetQueueStats();
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting queue stats");
            return StatusCode(500, new { error = "Failed to get queue statistics" });
        }
    }

    /// <summary>
    /// Get system performance metrics
    /// </summary>
    [HttpGet("metrics")]
    public IActionResult GetPerformanceMetrics()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();

        return Ok(new
        {
            Memory = new
            {
                WorkingSetMB = process.WorkingSet64 / (1024 * 1024),
                PrivateMemoryMB = process.PrivateMemorySize64 / (1024 * 1024),
                GCTotalMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024)
            },
            CPU = new
            {
                ProcessorTime = process.TotalProcessorTime.TotalSeconds,
                ThreadCount = process.Threads.Count
            },
            Queues = _processingService.GetQueueStats(),
            Config = new
            {
                ChannelCapacity = _configuration.GetValue<int>("LanCache:ChannelCapacity", 100000),
                BatchSize = _configuration.GetValue<int>("LanCache:BatchSize", 5000),
                BatchTimeoutMs = _configuration.GetValue<int>("LanCache:BatchTimeoutMs", 500),
                ConsumerCount = _configuration.GetValue<int>("LanCache:ConsumerCount", 4),
                ParserParallelism = _configuration.GetValue<int>("LanCache:ParserParallelism", 8),
                UseHighThroughputMode = _configuration.GetValue<bool>("LanCache:UseHighThroughputMode", false)
            }
        });
    }
}