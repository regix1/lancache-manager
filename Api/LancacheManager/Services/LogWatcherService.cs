using LancacheManager.Models;
using System.Text;

namespace LancacheManager.Services;

public class LogWatcherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LogParserService _parser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LogWatcherService> _logger;
    private long _lastPosition = 0;
    private readonly string _positionFile = "/data/logposition.txt";
    private int _consecutiveEmptyReads = 0;

    public LogWatcherService(
        IServiceProvider serviceProvider,
        LogParserService parser,
        IConfiguration configuration,
        ILogger<LogWatcherService> logger)
    {
        _serviceProvider = serviceProvider;
        _parser = parser;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logPath = _configuration["LanCache:LogPath"] ?? "/logs/access.log";
        
        // Wait for file to exist
        while (!File.Exists(logPath) && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation($"Waiting for log file: {logPath}");
            await Task.Delay(10000, stoppingToken);
        }

        _logger.LogInformation($"Starting log watcher for: {logPath}");

        // Load saved position
        await LoadPosition();

        // If no saved position, initialize based on configuration
        if (_lastPosition == 0)
        {
            await InitializePosition(logPath);
        }

        // Start processing
        await ProcessLogFile(logPath, stoppingToken);
    }

    private async Task InitializePosition(string logPath)
    {
        try
        {
            var fileInfo = new FileInfo(logPath);
            var startFromEnd = _configuration.GetValue<bool>("LanCache:StartFromEndOfLog", true);
            var processHistorical = _configuration.GetValue<bool>("LanCache:ProcessHistoricalLogs", false);
            
            if (!processHistorical || startFromEnd)
            {
                // Start from the end of file (only process new entries)
                _lastPosition = fileInfo.Length;
                _logger.LogInformation($"Starting from END of log file (position {_lastPosition:N0})");
                _logger.LogInformation($"Only NEW log entries will be processed");
            }
            else if (fileInfo.Length > 100_000_000) // If file is larger than 100MB
            {
                // Start from last 10MB for large files
                _lastPosition = Math.Max(0, fileInfo.Length - 10_000_000);
                _logger.LogInformation($"Large log file detected. Starting from position {_lastPosition:N0} (last 10MB)");
            }
            else
            {
                _lastPosition = 0;
                _logger.LogInformation("Starting from beginning of log file");
            }
            
            await SavePosition();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing position");
            _lastPosition = 0;
        }
    }

    private async Task ProcessLogFile(string path, CancellationToken stoppingToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096);

        // Seek to last position
        if (_lastPosition > 0 && _lastPosition <= stream.Length)
        {
            stream.Seek(_lastPosition, SeekOrigin.Begin);
            
            // If we're not at the end, skip the partial line
            if (_lastPosition < stream.Length)
            {
                await reader.ReadLineAsync();
            }
            
            _logger.LogInformation($"Resumed from position {_lastPosition:N0} of {stream.Length:N0} total bytes");
        }
        else if (_lastPosition > stream.Length)
        {
            // File was rotated or truncated, start from beginning
            _lastPosition = 0;
            _logger.LogWarning("Log file appears to have been rotated, starting from beginning");
        }

        var entries = new List<LogEntry>();
        var linesProcessed = 0;
        var lastReportTime = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                
                if (line != null)
                {
                    _consecutiveEmptyReads = 0;
                    linesProcessed++;

                    var entry = _parser.ParseLine(line);
                    if (entry != null && entry.BytesServed > 1024) // Only process entries > 1KB
                    {
                        entries.Add(entry);
                    }

                    // Process batch when we have enough entries or every 100 lines
                    if (entries.Count >= 50 || linesProcessed >= 100)
                    {
                        if (entries.Count > 0)
                        {
                            await ProcessBatch(entries);
                            entries.Clear();
                        }
                        
                        _lastPosition = stream.Position;
                        await SavePosition();
                        linesProcessed = 0;
                    }

                    // Report progress every 30 seconds
                    if (DateTime.UtcNow - lastReportTime > TimeSpan.FromSeconds(30))
                    {
                        _logger.LogDebug($"Processed {linesProcessed} lines, position: {_lastPosition:N0}");
                        lastReportTime = DateTime.UtcNow;
                    }
                }
                else
                {
                    // No new data
                    if (entries.Count > 0)
                    {
                        await ProcessBatch(entries);
                        entries.Clear();
                    }

                    _lastPosition = stream.Position;
                    await SavePosition();

                    _consecutiveEmptyReads++;
                    
                    // Only log "waiting" message every 30th empty read (every 30 seconds)
                    if (_consecutiveEmptyReads % 30 == 1)
                    {
                        _logger.LogDebug($"Waiting for new log entries... (position: {_lastPosition:N0})");
                    }

                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing log line");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessBatch(List<LogEntry> entries)
    {
        if (entries.Count == 0) return;

        _logger.LogDebug($"Processing batch of {entries.Count} log entries");

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
                _logger.LogError(ex, $"Error processing entry for {entry.ClientIp}");
            }
        }
    }

    private async Task LoadPosition()
    {
        try
        {
            if (File.Exists(_positionFile))
            {
                var content = await File.ReadAllTextAsync(_positionFile);
                if (long.TryParse(content, out var position))
                {
                    _lastPosition = position;
                    _logger.LogInformation($"Loaded saved position: {_lastPosition:N0}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading position");
        }
    }

    private async Task SavePosition()
    {
        try
        {
            var dir = Path.GetDirectoryName(_positionFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(_positionFile, _lastPosition.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving position");
        }
    }
}