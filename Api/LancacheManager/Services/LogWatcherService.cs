using LancacheManager.Models;
using LancacheManager.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Services;

public class LogWatcherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LogParserService _logParser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LogWatcherService> _logger;
    private FileSystemWatcher? _watcher;
    private long _lastPosition = 0;
    private readonly string _positionFile = "logposition.txt";
    private string? _activeLogPath;

    public LogWatcherService(
        IServiceProvider serviceProvider, 
        LogParserService logParser, 
        IConfiguration configuration,
        ILogger<LogWatcherService> logger)
    {
        _serviceProvider = serviceProvider;
        _logParser = logParser;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Auto-detect log file
        _activeLogPath = await DetectLogFile();
        
        if (string.IsNullOrEmpty(_activeLogPath))
        {
            _logger.LogError("No log file found. Waiting for logs to appear...");
            
            // Wait for logs to appear
            while (string.IsNullOrEmpty(_activeLogPath) && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(10000, stoppingToken); // Check every 10 seconds
                _activeLogPath = await DetectLogFile();
            }
        }

        if (string.IsNullOrEmpty(_activeLogPath))
        {
            _logger.LogError("No log file found after waiting. Service stopping.");
            return;
        }

        _logger.LogInformation($"Using log file: {_activeLogPath}");

        // Load last position if exists
        if (File.Exists(_positionFile))
        {
            var positionText = await File.ReadAllTextAsync(_positionFile);
            long.TryParse(positionText, out _lastPosition);
        }

        // Start processing in background
        _ = Task.Run(async () => await ProcessLogContinuously(_activeLogPath, stoppingToken), stoppingToken);

        // Set up file watcher for changes
        var dir = Path.GetDirectoryName(_activeLogPath);
        var fileName = Path.GetFileName(_activeLogPath);

        if (!string.IsNullOrEmpty(dir))
        {
            _watcher = new FileSystemWatcher(dir)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.EnableRaisingEvents = true;
        }

        // Keep service running
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task<string?> DetectLogFile()
    {
        var searchPaths = new List<string>();

        // Priority 1: Check configured path
        var configuredPath = _configuration["LanCache:LogPath"];
        if (!string.IsNullOrEmpty(configuredPath))
        {
            searchPaths.Add(configuredPath);
        }

        // Priority 2: Check /logs directory (Docker mount)
        searchPaths.Add("/logs/access.log");
        searchPaths.Add("/logs/lancache/access.log");
        searchPaths.Add("/logs/nginx/access.log");
        
        // Priority 3: Check standard Linux paths
        searchPaths.Add("/var/lancache/logs/access.log");
        searchPaths.Add("/var/log/nginx/lancache-access.log");
        searchPaths.Add("/var/log/lancache/access.log");
        
        // Priority 4: Check Windows development paths
        if (OperatingSystem.IsWindows())
        {
            searchPaths.Add("C:/temp/lancache/access.log");
            searchPaths.Add("./logs/access.log");
        }

        foreach (var path in searchPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    _logger.LogInformation($"Found log file at: {path}");
                    return path;
                }

                // Check if directory exists and look for any .log files
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    var logFiles = Directory.GetFiles(dir, "*.log", SearchOption.TopDirectoryOnly)
                        .Where(f => f.Contains("access", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .ToList();

                    if (logFiles.Any())
                    {
                        _logger.LogInformation($"Found log file at: {logFiles.First()}");
                        return logFiles.First();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Error checking path {path}: {ex.Message}");
            }
        }

        // If in development and no logs found, create a test log file
        if (_configuration.GetValue<bool>("LanCache:GenerateSampleLogs", false))
        {
            var testLogPath = "/logs/access.log";
            if (OperatingSystem.IsWindows())
            {
                testLogPath = "C:/temp/lancache/access.log";
            }

            try
            {
                var dir = Path.GetDirectoryName(testLogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                if (!File.Exists(testLogPath))
                {
                    await File.WriteAllTextAsync(testLogPath, "");
                    _logger.LogInformation($"Created test log file at: {testLogPath}");
                }
                
                return testLogPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create test log file");
            }
        }

        return null;
    }

    private async Task ProcessLogContinuously(string path, CancellationToken stoppingToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        // Seek to last position
        if (_lastPosition > 0 && _lastPosition < stream.Length)
        {
            stream.Seek(_lastPosition, SeekOrigin.Begin);
        }

        var batchedEntries = new List<LogEntry>();
        var lastBatchTime = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                
                if (line != null)
                {
                    var entry = _logParser.ParseLogLine(line);
                    if (entry != null)
                    {
                        batchedEntries.Add(entry);
                    }

                    // Process batch if we have enough entries or enough time has passed
                    if (batchedEntries.Count >= 100 || 
                        (batchedEntries.Count > 0 && DateTime.UtcNow - lastBatchTime > TimeSpan.FromSeconds(5)))
                    {
                        await ProcessBatch(batchedEntries);
                        batchedEntries.Clear();
                        lastBatchTime = DateTime.UtcNow;
                        
                        // Save position
                        _lastPosition = stream.Position;
                        await File.WriteAllTextAsync(_positionFile, _lastPosition.ToString());
                    }
                }
                else
                {
                    // No new data, process any pending entries
                    if (batchedEntries.Count > 0)
                    {
                        await ProcessBatch(batchedEntries);
                        batchedEntries.Clear();
                        lastBatchTime = DateTime.UtcNow;
                        
                        // Save position
                        _lastPosition = stream.Position;
                        await File.WriteAllTextAsync(_positionFile, _lastPosition.ToString());
                    }

                    // Wait a bit before checking again
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log file");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessBatch(List<LogEntry> entries)
    {
        if (entries.Count == 0)
            return;

        _logger.LogInformation($"Processing batch of {entries.Count} log entries");

        using var scope = _serviceProvider.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();

        foreach (var entry in entries)
        {
            try
            {
                await dbService.ProcessLogEntry(entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing log entry for {entry.ClientIp}");
            }
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}