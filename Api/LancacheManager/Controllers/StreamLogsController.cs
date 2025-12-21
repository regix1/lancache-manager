using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// Controller for stream-access.log processing and stream session management
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StreamLogsController : ControllerBase
{
    private readonly ILogger<StreamLogsController> _logger;
    private readonly RustStreamProcessorService _streamProcessor;
    private readonly DatasourceService _datasourceService;
    private readonly IPathResolver _pathResolver;
    private readonly StateRepository _stateRepository;
    private readonly AppDbContext _context;

    public StreamLogsController(
        ILogger<StreamLogsController> logger,
        RustStreamProcessorService streamProcessor,
        DatasourceService datasourceService,
        IPathResolver pathResolver,
        StateRepository stateRepository,
        AppDbContext context)
    {
        _logger = logger;
        _streamProcessor = streamProcessor;
        _datasourceService = datasourceService;
        _pathResolver = pathResolver;
        _stateRepository = stateRepository;
        _context = context;
    }

    /// <summary>
    /// Get stream log info for all datasources
    /// </summary>
    [HttpGet("info")]
    public async Task<IActionResult> GetStreamLogInfo()
    {
        var datasources = _datasourceService.GetDatasources();
        var result = new List<object>();

        foreach (var ds in datasources)
        {
            var streamLogPath = Path.Combine(ds.LogPath, "stream-access.log");
            var streamLogExists = System.IO.File.Exists(streamLogPath);
            long lineCount = 0;
            long fileSize = 0;

            if (streamLogExists)
            {
                var fileInfo = new FileInfo(streamLogPath);
                fileSize = fileInfo.Length;
                lineCount = CountLinesInStreamLogs(ds.LogPath);
            }

            var sessionCount = await _context.StreamSessions
                .Where(s => s.Datasource == ds.Name)
                .CountAsync();

            result.Add(new
            {
                datasource = ds.Name,
                logsPath = ds.LogPath,
                streamLogExists,
                lineCount,
                fileSize,
                fileSizeFormatted = FormatBytes(fileSize),
                sessionCount,
                logsWritable = _pathResolver.IsDirectoryWritable(ds.LogPath)
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Get stream session counts by datasource
    /// </summary>
    [HttpGet("session-counts")]
    public async Task<IActionResult> GetSessionCounts()
    {
        var counts = await _context.StreamSessions
            .GroupBy(s => s.Datasource)
            .Select(g => new
            {
                datasource = g.Key,
                sessionCount = g.Count(),
                totalBytesSent = g.Sum(s => s.BytesSent),
                totalBytesReceived = g.Sum(s => s.BytesReceived),
                correlatedCount = g.Count(s => s.DownloadId != null)
            })
            .ToListAsync();

        return Ok(counts);
    }

    /// <summary>
    /// Start stream log processing for all datasources
    /// </summary>
    [HttpPost("process")]
    [RequireAuth]
    public async Task<IActionResult> StartProcessing()
    {
        if (_streamProcessor.IsProcessing)
        {
            return Conflict(new { error = "Stream processing is already running" });
        }

        var result = await _streamProcessor.StartProcessing();

        return Ok(new
        {
            status = result ? "started" : "failed",
            message = result ? "Stream log processing started" : "Failed to start stream processing"
        });
    }

    /// <summary>
    /// Get stream processing status
    /// </summary>
    [HttpGet("processing-status")]
    public IActionResult GetProcessingStatus()
    {
        return Ok(_streamProcessor.GetStatus());
    }

    /// <summary>
    /// Clear all stream sessions from database
    /// </summary>
    [HttpDelete("sessions")]
    [RequireAuth]
    public async Task<IActionResult> ClearAllSessions()
    {
        try
        {
            var count = await _context.StreamSessions.CountAsync();
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM StreamSessions");

            // Also clear speed data from downloads
            await _context.Database.ExecuteSqlRawAsync(
                "UPDATE Downloads SET DownloadSpeedBps = NULL, UploadSpeedBps = NULL, SessionDurationSeconds = NULL, StreamSessionCount = NULL");

            // Reset stream log positions
            _streamProcessor.ResetStreamLogPosition();

            _logger.LogInformation("Cleared {Count} stream sessions", count);

            return Ok(new
            {
                success = true,
                message = $"Cleared {count} stream sessions",
                sessionsCleared = count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear stream sessions");
            return StatusCode(500, new { error = "Failed to clear stream sessions" });
        }
    }

    /// <summary>
    /// Clear stream sessions for a specific datasource
    /// </summary>
    [HttpDelete("sessions/{datasourceName}")]
    [RequireAuth]
    public async Task<IActionResult> ClearSessionsForDatasource(string datasourceName)
    {
        try
        {
            var sessions = await _context.StreamSessions
                .Where(s => s.Datasource == datasourceName)
                .ToListAsync();

            var count = sessions.Count;
            _context.StreamSessions.RemoveRange(sessions);
            await _context.SaveChangesAsync();

            // Reset stream log position for this datasource
            _streamProcessor.ResetStreamLogPosition(datasourceName);

            _logger.LogInformation("Cleared {Count} stream sessions for datasource '{Datasource}'", count, datasourceName);

            return Ok(new
            {
                success = true,
                message = $"Cleared {count} stream sessions for {datasourceName}",
                sessionsCleared = count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear stream sessions for datasource {Datasource}", datasourceName);
            return StatusCode(500, new { error = "Failed to clear stream sessions" });
        }
    }

    /// <summary>
    /// Get stream sessions with speed data (paginated)
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? datasource = null)
    {
        var query = _context.StreamSessions.AsQueryable();

        if (!string.IsNullOrEmpty(datasource))
        {
            query = query.Where(s => s.Datasource == datasource);
        }

        var totalCount = await query.CountAsync();
        var sessions = await query
            .OrderByDescending(s => s.SessionEndUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.ClientIp,
                s.SessionStartUtc,
                s.SessionEndUtc,
                s.Protocol,
                s.Status,
                s.BytesSent,
                s.BytesReceived,
                s.DurationSeconds,
                s.UpstreamHost,
                s.DownloadId,
                s.Datasource,
                downloadSpeedBps = s.DurationSeconds > 0 ? s.BytesSent / s.DurationSeconds : 0,
                uploadSpeedBps = s.DurationSeconds > 0 ? s.BytesReceived / s.DurationSeconds : 0,
                downloadSpeedFormatted = FormatSpeed(s.DurationSeconds > 0 ? s.BytesSent / s.DurationSeconds : 0),
                uploadSpeedFormatted = FormatSpeed(s.DurationSeconds > 0 ? s.BytesReceived / s.DurationSeconds : 0)
            })
            .ToListAsync();

        return Ok(new
        {
            sessions,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        });
    }

    /// <summary>
    /// Get speed statistics summary
    /// </summary>
    [HttpGet("speed-stats")]
    public async Task<IActionResult> GetSpeedStats()
    {
        var stats = await _context.StreamSessions
            .GroupBy(s => 1)
            .Select(g => new
            {
                totalSessions = g.Count(),
                totalBytesSent = g.Sum(s => s.BytesSent),
                totalBytesReceived = g.Sum(s => s.BytesReceived),
                totalDurationSeconds = g.Sum(s => s.DurationSeconds),
                avgDownloadSpeedBps = g.Sum(s => s.DurationSeconds) > 0
                    ? g.Sum(s => s.BytesSent) / g.Sum(s => s.DurationSeconds)
                    : 0,
                avgUploadSpeedBps = g.Sum(s => s.DurationSeconds) > 0
                    ? g.Sum(s => s.BytesReceived) / g.Sum(s => s.DurationSeconds)
                    : 0,
                correlatedCount = g.Count(s => s.DownloadId != null)
            })
            .FirstOrDefaultAsync();

        if (stats == null)
        {
            return Ok(new
            {
                totalSessions = 0,
                totalBytesSent = 0L,
                totalBytesReceived = 0L,
                avgDownloadSpeedBps = 0.0,
                avgUploadSpeedBps = 0.0,
                avgDownloadSpeedFormatted = "0 B/s",
                avgUploadSpeedFormatted = "0 B/s"
            });
        }

        return Ok(new
        {
            stats.totalSessions,
            stats.totalBytesSent,
            stats.totalBytesReceived,
            totalBytesSentFormatted = FormatBytes(stats.totalBytesSent),
            totalBytesReceivedFormatted = FormatBytes(stats.totalBytesReceived),
            stats.avgDownloadSpeedBps,
            stats.avgUploadSpeedBps,
            avgDownloadSpeedFormatted = FormatSpeed(stats.avgDownloadSpeedBps),
            avgUploadSpeedFormatted = FormatSpeed(stats.avgUploadSpeedBps),
            stats.correlatedCount,
            correlationPercent = stats.totalSessions > 0
                ? (stats.correlatedCount * 100.0 / stats.totalSessions)
                : 0
        });
    }

    /// <summary>
    /// Get stream log positions for all datasources
    /// </summary>
    [HttpGet("positions")]
    public IActionResult GetStreamLogPositions()
    {
        var datasources = _datasourceService.GetDatasources();
        var positions = new List<object>();

        foreach (var ds in datasources)
        {
            var position = _stateRepository.GetStreamLogPosition(ds.Name);
            var totalLines = CountLinesInStreamLogs(ds.LogPath);

            positions.Add(new
            {
                datasource = ds.Name,
                position = position,
                totalLines = totalLines,
                logPath = ds.LogPath,
                enabled = ds.Enabled
            });
        }

        return Ok(positions);
    }

    /// <summary>
    /// Reset stream log position for all datasources
    /// Request body: { "position": 0 } to reset to beginning, { "position": null } to reset to end
    /// </summary>
    [HttpPatch("position")]
    [RequireAuth]
    public IActionResult ResetStreamLogPosition([FromBody] UpdatePositionRequest? request)
    {
        var datasources = _datasourceService.GetDatasources();

        // If position is explicitly 0, reset to beginning
        if (request?.Position == 0)
        {
            _streamProcessor.ResetStreamLogPosition();
            _logger.LogInformation("Stream log position reset to beginning for all datasources");

            return Ok(new
            {
                message = "Stream log position reset to beginning",
                position = 0
            });
        }

        // Otherwise (position is null or not specified), reset to end of file
        long totalLines = 0;
        foreach (var ds in datasources)
        {
            var lineCount = CountLinesInStreamLogs(ds.LogPath);
            _stateRepository.SetStreamLogPosition(ds.Name, lineCount);
            totalLines += lineCount;

            if (lineCount > 0)
            {
                _logger.LogInformation("Datasource '{Name}': Stream log position set to end (line {LineCount})", ds.Name, lineCount);
            }
        }

        return Ok(new
        {
            message = "Stream log position reset to end of file",
            position = totalLines
        });
    }

    /// <summary>
    /// Reset stream log position for a specific datasource
    /// </summary>
    [HttpPatch("position/{datasourceName}")]
    [RequireAuth]
    public IActionResult ResetDatasourceStreamLogPosition(string datasourceName, [FromBody] UpdatePositionRequest? request)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(new { error = $"Datasource '{datasourceName}' not found" });
        }

        // If position is explicitly 0, reset to beginning
        if (request?.Position == 0)
        {
            _streamProcessor.ResetStreamLogPosition(datasourceName);
            _logger.LogInformation("Datasource '{Name}': Stream log position reset to beginning", datasourceName);

            return Ok(new
            {
                message = $"Stream log position reset to beginning for '{datasourceName}'",
                position = 0
            });
        }

        // Otherwise reset to end of file
        long lineCount = CountLinesInStreamLogs(datasource.LogPath);
        _stateRepository.SetStreamLogPosition(datasourceName, lineCount);
        _logger.LogInformation("Datasource '{Name}': Stream log position set to end (line {LineCount})", datasourceName, lineCount);

        return Ok(new
        {
            message = $"Stream log position reset to end of file for '{datasourceName}'",
            position = lineCount
        });
    }

    public class UpdatePositionRequest
    {
        public long? Position { get; set; }
    }

    private long CountLinesInStreamLogs(string logDir)
    {
        long total = 0;
        try
        {
            var streamLogs = Directory.GetFiles(logDir, "stream-access.log*");
            foreach (var file in streamLogs)
            {
                using var reader = new StreamReader(file);
                while (reader.ReadLine() != null)
                {
                    total++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to count lines in stream logs at {Path}", logDir);
        }
        return total;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000_000_000)
            return $"{bytes / 1_000_000_000_000.0:F2} TB";
        if (bytes >= 1_000_000_000)
            return $"{bytes / 1_000_000_000.0:F2} GB";
        if (bytes >= 1_000_000)
            return $"{bytes / 1_000_000.0:F2} MB";
        if (bytes >= 1_000)
            return $"{bytes / 1_000.0:F2} KB";
        return $"{bytes} B";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000_000)
            return $"{bytesPerSecond / 1_000_000_000:F2} GB/s";
        if (bytesPerSecond >= 1_000_000)
            return $"{bytesPerSecond / 1_000_000:F2} MB/s";
        if (bytesPerSecond >= 1_000)
            return $"{bytesPerSecond / 1_000:F2} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }
}
