using System.Runtime.InteropServices;

namespace LancacheManager.Services;

public class PathHelperService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PathHelperService> _logger;
    
    // These are not readonly so they can be set in InitializePaths
    private string _dataDirectory = string.Empty;
    private string _logPath = string.Empty;
    private string _cachePath = string.Empty;

    public PathHelperService(IConfiguration configuration, ILogger<PathHelperService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        
        InitializePaths();
    }

    private void InitializePaths()
    {
        // Determine paths based on platform
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows paths
            _dataDirectory = _configuration["DataDirectory"] ?? 
                _configuration["Windows:DataDirectory"] ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LancacheManager");
            
            _logPath = _configuration["LanCache:LogPath"] ?? 
                _configuration["Windows:LanCache:LogPath"] ??
                Path.Combine(_dataDirectory, "logs", "access.log");
            
            _cachePath = _configuration["LanCache:CachePath"] ?? 
                _configuration["Windows:LanCache:CachePath"] ??
                Path.Combine(_dataDirectory, "cache");
        }
        else
        {
            // Linux/Docker paths
            _dataDirectory = _configuration["DataDirectory"] ?? 
                _configuration["Linux:DataDirectory"] ??
                "/data";
            
            _logPath = _configuration["LanCache:LogPath"] ?? 
                _configuration["Linux:LanCache:LogPath"] ??
                "/logs/access.log";
            
            _cachePath = _configuration["LanCache:CachePath"] ?? 
                _configuration["Linux:LanCache:CachePath"] ??
                "/cache";
        }
        
        // Ensure directories exist
        EnsureDirectoryExists(_dataDirectory);
        
        var logDir = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(logDir))
        {
            EnsureDirectoryExists(logDir);
        }
        
        EnsureDirectoryExists(_cachePath);
        
        _logger.LogInformation($"PathHelper initialized - Platform: {RuntimeInformation.OSDescription}");
        _logger.LogInformation($"Data Directory: {_dataDirectory}");
        _logger.LogInformation($"Log Path: {_logPath}");
        _logger.LogInformation($"Cache Path: {_cachePath}");
    }

    private void EnsureDirectoryExists(string path)
    {
        if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(path);
                _logger.LogDebug($"Created directory: {path}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create directory: {path}");
            }
        }
    }

    public string DataDirectory => _dataDirectory;
    public string LogPath => _logPath;
    public string CachePath => _cachePath;
    
    // Helper methods for common file paths
    public string GetPositionFilePath() => Path.Combine(_dataDirectory, "logposition.txt");
    public string GetProcessingMarkerPath() => Path.Combine(_dataDirectory, "bulk_processing.marker");
    public string GetDatabasePath() => Path.Combine(_dataDirectory, "lancache.db");
    
    // Utility method to get a safe path for any file in the data directory
    public string GetDataFilePath(string filename) => Path.Combine(_dataDirectory, filename);
}