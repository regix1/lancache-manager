using System.Text;

namespace LancacheManager.Services;

public class SampleLogGeneratorService : BackgroundService
{
    private readonly ILogger<SampleLogGeneratorService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Random _random = new();

    private readonly string[] _services = { "steam", "epic", "origin", "blizzard", "uplay", "xboxlive", "wsus" };
    private readonly string[] _cacheStatuses = { "HIT", "MISS", "HIT", "HIT", "HIT" }; // More HITs for realistic ratio
    private readonly string[] _clientIps = { "192.168.1.100", "192.168.1.101", "192.168.1.102", "192.168.1.103", "192.168.1.104" };

    public SampleLogGeneratorService(ILogger<SampleLogGeneratorService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logPath = _configuration["LanCache:LogPath"] ?? "/logs/access.log";
        
        // Ensure directory exists
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _logger.LogInformation($"Sample log generator started, writing to: {logPath}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Generate a sample log entry
                var logEntry = GenerateSampleLogEntry();
                
                // Append to log file
                await File.AppendAllTextAsync(logPath, logEntry + Environment.NewLine, stoppingToken);
                
                // Random delay between log entries (100ms to 2 seconds)
                await Task.Delay(_random.Next(100, 2000), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating sample log");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private string GenerateSampleLogEntry()
    {
        var service = _services[_random.Next(_services.Length)];
        var clientIp = _clientIps[_random.Next(_clientIps.Length)];
        var cacheStatus = _cacheStatuses[_random.Next(_cacheStatuses.Length)];
        var bytes = _random.Next(1024, 104857600); // 1KB to 100MB
        var timestamp = DateTime.UtcNow.ToString("dd/MMM/yyyy:HH:mm:ss +0000");
        
        // Generate appropriate URL based on service
        var url = service switch
        {
            "steam" => $"/depot/{_random.Next(100000, 999999)}/chunk/{Guid.NewGuid():N}",
            "epic" => $"/epic-games/launcher/chunk_{Guid.NewGuid():N}",
            "blizzard" => $"/blizzard/wow/data_{_random.Next(1000, 9999)}.bin",
            "origin" => $"/origin/game{_random.Next(100, 999)}/asset_{Guid.NewGuid():N}",
            _ => $"/{service}/file_{Guid.NewGuid():N}"
        };

        // Nginx access log format
        return $"{service} {clientIp} - - [{timestamp}] \"GET {url} HTTP/1.1\" 200 {bytes} \"-\" \"LancacheManager/1.0\" \"{cacheStatus}\" \"{service}.cache.local\" \"-\"";
    }
}