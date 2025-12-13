using System.Globalization;
using LancacheManager.Configuration;
using LancacheManager.Infrastructure.Services.Interfaces;

namespace LancacheManager.Application.Services;

/// <summary>
/// Service for managing multiple LANCache datasources (log/cache locations).
/// Provides access to configured datasources with resolved paths.
/// </summary>
public class DatasourceService
{
    private readonly IConfiguration _configuration;
    private readonly IPathResolver _pathResolver;
    private readonly ILogger<DatasourceService> _logger;
    private readonly List<ResolvedDatasource> _datasources;

    public DatasourceService(
        IConfiguration configuration,
        IPathResolver pathResolver,
        ILogger<DatasourceService> logger)
    {
        _configuration = configuration;
        _pathResolver = pathResolver;
        _logger = logger;
        _datasources = new List<ResolvedDatasource>();

        LoadDatasources();
    }

    /// <summary>
    /// Load and resolve all datasource configurations.
    /// Supports explicit config, auto-discovery, and legacy single-path modes.
    /// Priority: Explicit DataSources > Auto-Discovery > Legacy single-path
    /// </summary>
    private void LoadDatasources()
    {
        // Try to load new array-based configuration
        var datasourceConfigs = _configuration.GetSection("LanCache:DataSources").Get<List<DatasourceConfig>>();

        if (datasourceConfigs != null && datasourceConfigs.Count > 0)
        {
            // Explicit configuration takes highest priority
            _logger.LogInformation("Loading {Count} datasource(s) from explicit configuration", datasourceConfigs.Count);

            foreach (var config in datasourceConfigs.Where(c => c.Enabled))
            {
                var resolved = ResolveDatasource(config);
                if (resolved != null)
                {
                    _datasources.Add(resolved);
                    _logger.LogInformation("Loaded datasource '{Name}': Cache={CachePath}, Logs={LogPath}",
                        resolved.Name, resolved.CachePath, resolved.LogPath);
                }
            }
        }
        else
        {
            // Check if auto-discovery is enabled
            var autoDiscover = _configuration.GetValue<bool>("LanCache:AutoDiscoverDatasources");

            if (autoDiscover)
            {
                var discovered = DiscoverDatasources();
                if (discovered.Count > 0)
                {
                    _logger.LogInformation("Auto-discovered {Count} datasource(s)", discovered.Count);

                    foreach (var config in discovered)
                    {
                        var resolved = ResolveDatasource(config);
                        if (resolved != null)
                        {
                            _datasources.Add(resolved);
                            _logger.LogInformation("Auto-discovered datasource '{Name}': Cache={CachePath}, Logs={LogPath}",
                                resolved.Name, resolved.CachePath, resolved.LogPath);
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("Auto-discovery enabled but no matching subdirectories found");
                }
            }

            // Fall back to legacy single-path configuration if no datasources loaded
            if (_datasources.Count == 0)
            {
                _logger.LogInformation("Using legacy single-path configuration");

                var legacyConfig = new DatasourceConfig
                {
                    Name = "default",
                    Enabled = true
                };

                // Get legacy paths from configuration
                var configCachePath = _configuration["LanCache:CachePath"];
                var configLogPath = _configuration["LanCache:LogPath"];

                legacyConfig.CachePath = !string.IsNullOrEmpty(configCachePath) ? configCachePath : "cache";
                legacyConfig.LogPath = !string.IsNullOrEmpty(configLogPath) ? configLogPath : "logs";

                var resolved = ResolveDatasource(legacyConfig);
                if (resolved != null)
                {
                    _datasources.Add(resolved);
                    _logger.LogInformation("Loaded default datasource: Cache={CachePath}, Logs={LogPath}",
                        resolved.CachePath, resolved.LogPath);
                }
            }
        }

        if (_datasources.Count == 0)
        {
            _logger.LogWarning("No valid datasources configured. Some features may not work correctly.");
        }
    }

    /// <summary>
    /// Discover datasources by scanning for matching subdirectories in cache and logs folders.
    /// A subdirectory is only added if it exists in BOTH the cache and logs base paths.
    /// </summary>
    private List<DatasourceConfig> DiscoverDatasources()
    {
        var discovered = new List<DatasourceConfig>();

        var baseCachePath = _pathResolver.ResolvePath(
            _configuration["LanCache:CachePath"] ?? "cache");
        var baseLogsPath = _pathResolver.ResolvePath(
            _configuration["LanCache:LogPath"] ?? "logs");

        _logger.LogDebug("Auto-discovery scanning: Cache={CachePath}, Logs={LogsPath}", baseCachePath, baseLogsPath);

        if (!Directory.Exists(baseCachePath))
        {
            _logger.LogWarning("Auto-discovery: Cache directory does not exist: {Path}", baseCachePath);
            return discovered;
        }

        if (!Directory.Exists(baseLogsPath))
        {
            _logger.LogWarning("Auto-discovery: Logs directory does not exist: {Path}", baseLogsPath);
            return discovered;
        }

        foreach (var cacheSubdir in Directory.GetDirectories(baseCachePath))
        {
            var subdirName = Path.GetFileName(cacheSubdir);

            // Skip hidden directories and common non-datasource folders
            if (subdirName.StartsWith(".") || subdirName.StartsWith("_"))
                continue;

            var logsSubdir = Path.Combine(baseLogsPath, subdirName);

            // Only create datasource if BOTH cache and logs subdirectories exist
            if (Directory.Exists(logsSubdir))
            {
                var displayName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(subdirName.ToLower());

                discovered.Add(new DatasourceConfig
                {
                    Name = displayName,
                    CachePath = cacheSubdir,
                    LogPath = logsSubdir,
                    Enabled = true
                });

                _logger.LogDebug("Auto-discovery found matching pair: {Name} (cache: {Cache}, logs: {Logs})",
                    displayName, cacheSubdir, logsSubdir);
            }
        }

        // Sort by name for consistent ordering
        return discovered.OrderBy(d => d.Name).ToList();
    }

    /// <summary>
    /// Resolve paths and validate a datasource configuration.
    /// </summary>
    private ResolvedDatasource? ResolveDatasource(DatasourceConfig config)
    {
        try
        {
            var cachePath = _pathResolver.ResolvePath(config.CachePath);
            var logPath = _pathResolver.ResolvePath(config.LogPath);

            // For LogPath, if it points to a file (access.log), extract the directory
            var logDir = logPath;
            if (Path.HasExtension(logPath) && Path.GetFileName(logPath).Contains("access"))
            {
                logDir = Path.GetDirectoryName(logPath) ?? logPath;
            }

            return new ResolvedDatasource
            {
                Name = config.Name,
                CachePath = cachePath,
                LogPath = logDir,
                LogFilePath = Path.Combine(logDir, "access.log"),
                Enabled = config.Enabled,
                CacheWritable = _pathResolver.IsDirectoryWritable(cachePath),
                LogsWritable = _pathResolver.IsDirectoryWritable(logDir)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve datasource '{Name}'", config.Name);
            return null;
        }
    }

    /// <summary>
    /// Get all enabled datasources.
    /// </summary>
    public IReadOnlyList<ResolvedDatasource> GetDatasources()
    {
        return _datasources.AsReadOnly();
    }

    /// <summary>
    /// Get a specific datasource by name.
    /// </summary>
    public ResolvedDatasource? GetDatasource(string name)
    {
        return _datasources.FirstOrDefault(d =>
            d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get the default (first) datasource.
    /// </summary>
    public ResolvedDatasource? GetDefaultDatasource()
    {
        return _datasources.FirstOrDefault();
    }

    /// <summary>
    /// Check if a specific datasource's cache directory is writable.
    /// </summary>
    public bool IsCacheWritable(string datasourceName)
    {
        var datasource = GetDatasource(datasourceName);
        if (datasource == null) return false;

        return _pathResolver.IsDirectoryWritable(datasource.CachePath);
    }

    /// <summary>
    /// Check if a specific datasource's logs directory is writable.
    /// </summary>
    public bool IsLogsWritable(string datasourceName)
    {
        var datasource = GetDatasource(datasourceName);
        if (datasource == null) return false;

        return _pathResolver.IsDirectoryWritable(datasource.LogPath);
    }

    /// <summary>
    /// Get datasource info for all configured datasources (for API responses).
    /// </summary>
    public List<DatasourceInfo> GetDatasourceInfos()
    {
        return _datasources.Select(d => new DatasourceInfo
        {
            Name = d.Name,
            CachePath = d.CachePath,
            LogsPath = d.LogPath,
            CacheWritable = _pathResolver.IsDirectoryWritable(d.CachePath),
            LogsWritable = _pathResolver.IsDirectoryWritable(d.LogPath),
            Enabled = d.Enabled
        }).ToList();
    }

    /// <summary>
    /// Check if multiple datasources are configured.
    /// </summary>
    public bool HasMultipleDatasources => _datasources.Count > 1;

    /// <summary>
    /// Get the count of configured datasources.
    /// </summary>
    public int DatasourceCount => _datasources.Count;
}

/// <summary>
/// A datasource with resolved absolute paths.
/// </summary>
public class ResolvedDatasource
{
    /// <summary>
    /// Unique name/identifier for this datasource.
    /// </summary>
    public string Name { get; set; } = "default";

    /// <summary>
    /// Resolved absolute path to the cache directory.
    /// </summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>
    /// Resolved absolute path to the logs directory.
    /// </summary>
    public string LogPath { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the access.log file.
    /// </summary>
    public string LogFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether this datasource is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the cache directory is writable.
    /// </summary>
    public bool CacheWritable { get; set; }

    /// <summary>
    /// Whether the logs directory is writable.
    /// </summary>
    public bool LogsWritable { get; set; }
}

/// <summary>
/// Datasource information for API responses.
/// </summary>
public class DatasourceInfo
{
    public string Name { get; set; } = string.Empty;
    public string CachePath { get; set; } = string.Empty;
    public string LogsPath { get; set; } = string.Empty;
    public bool CacheWritable { get; set; }
    public bool LogsWritable { get; set; }
    public bool Enabled { get; set; }
}
