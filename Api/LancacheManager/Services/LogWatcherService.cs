using LancacheManager.Models;

namespace LancacheManager.Services;

public class LogWatcherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LogParserService _parser;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LogWatcherService> _logger;
    private long _lastPosition = 0;

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
        _logger.LogInformation($"Starting log watcher for: {logPath}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(logPath))
                {
                    _logger.LogWarning($"Log file not found: {logPath}");
                    await Task.Delay(10000, stoppingToken);
                    continue;
                }

                await ProcessLogFile(logPath, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in log watcher");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessLogFile(string path, CancellationToken stoppingToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        // Seek to last position
        if (_lastPosition > 0 && _lastPosition < stream.Length)
        {
            stream.Seek(_lastPosition, SeekOrigin.Begin);
        }

        var entries = new List<LogEntry>();

        while (!stoppingToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            
            if (line == null)
            {
                // Process batch if we have entries
                if (entries.Count > 0)
                {
                    await ProcessBatch(entries);
                    entries.Clear();
                }
                
                _lastPosition = stream.Position;
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            var entry = _parser.ParseLine(line);
            if (entry != null)
            {
                entries.Add(entry);
            }

            // Process batch when we have enough
            if (entries.Count >= 50)
            {
                await ProcessBatch(entries);
                entries.Clear();
                _lastPosition = stream.Position;
            }
        }
    }

    private async Task ProcessBatch(List<LogEntry> entries)
    {
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
                _logger.LogError(ex, "Error processing log entry");
            }
        }
    }
}