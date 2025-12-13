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
    /// Discover datasources by scanning cache and logs folders.
    /// - Detects "Default" datasource if access.log exists at root level
    /// - Detects subdirectory datasources if matching folders exist in BOTH cache and logs
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

        // Check for root-level "Default" datasource
        // If there's an access.log at the root AND cache has content, create Default datasource
        if (HasRootLevelLogFile(baseLogsPath) && HasCacheContent(baseCachePath))
        {
            discovered.Add(new DatasourceConfig
            {
                Name = "Default",
                CachePath = baseCachePath,
                LogPath = baseLogsPath,
                Enabled = true
            });

            _logger.LogDebug("Auto-discovery found root-level Default datasource: Cache={Cache}, Logs={Logs}",
                baseCachePath, baseLogsPath);
        }

        // Scan subdirectories for additional datasources
        foreach (var cacheSubdir in Directory.GetDirectories(baseCachePath))
        {
            var subdirName = Path.GetFileName(cacheSubdir);

            // Skip hidden directories and common non-datasource folders
            if (subdirName.StartsWith(".") || subdirName.StartsWith("_"))
                continue;

            // Skip LANCache hash directories (2 character hex names like 00, 01, a1, ff)
            // These are cache content, not datasource subdirectories
            if (IsLanCacheHashDirectory(subdirName))
                continue;

            // Try to find matching logs subdirectory (handles naming variations)
            var logsSubdir = FindMatchingLogsDirectory(baseLogsPath, subdirName);

            if (logsSubdir != null)
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

        // Sort by name for consistent ordering, but keep Default first
        return discovered
            .OrderBy(d => d.Name == "Default" ? 0 : 1)
            .ThenBy(d => d.Name)
            .ToList();
    }

    /// <summary>
    /// Check if there's a log file at the root level of the logs directory.
    /// Looks for common log file patterns: access.log, *.log, etc.
    /// </summary>
    private bool HasRootLevelLogFile(string logsPath)
    {
        try
        {
            // Common log file patterns
            var logPatterns = new[] { "access.log", "*.log" };

            foreach (var pattern in logPatterns)
            {
                var files = Directory.GetFiles(logsPath, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                {
                    _logger.LogDebug("Found root-level log file(s) in {Path}: {Files}",
                        logsPath, string.Join(", ", files.Select(Path.GetFileName)));
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking for root-level log files in {Path}", logsPath);
            return false;
        }
    }

    /// <summary>
    /// Check if the cache directory has actual cache content (not just subdirectories).
    /// LANCache creates hash-named directories (2 character hex names like 00, 01, a1, etc.)
    /// </summary>
    private bool HasCacheContent(string cachePath)
    {
        try
        {
            // Look for LANCache hash directories (2 character hex names)
            var subdirs = Directory.GetDirectories(cachePath);
            foreach (var subdir in subdirs)
            {
                var name = Path.GetFileName(subdir);
                if (IsLanCacheHashDirectory(name))
                {
                    _logger.LogDebug("Found LANCache hash directory in {Path}: {Dir}", cachePath, name);
                    return true;
                }
            }

            // Also check for any files directly in the cache directory
            var files = Directory.GetFiles(cachePath, "*", SearchOption.TopDirectoryOnly);
            if (files.Length > 0)
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking for cache content in {Path}", cachePath);
            return false;
        }
    }

    /// <summary>
    /// Check if a directory name is a LANCache hash directory.
    /// LANCache creates 2-character hex directories (00, 01, a1, ff, etc.)
    /// </summary>
    private static bool IsLanCacheHashDirectory(string name)
    {
        // LANCache creates 2-char hex directories like 00, 01, a1, ff, etc.
        return name.Length == 2 && name.All(c => "0123456789abcdefABCDEF".Contains(c));
    }

    /// <summary>
    /// Find a matching logs directory for a given cache subdirectory name.
    /// Uses case-insensitive matching and normalized name comparison.
    /// </summary>
    private string? FindMatchingLogsDirectory(string baseLogsPath, string cacheSubdirName)
    {
        // Try exact match first
        var exactMatch = Path.Combine(baseLogsPath, cacheSubdirName);
        if (Directory.Exists(exactMatch))
        {
            return exactMatch;
        }

        // Scan all directories and find case-insensitive or normalized matches
        try
        {
            var logsDirectories = Directory.GetDirectories(baseLogsPath);
            var normalizedCacheName = NormalizeName(cacheSubdirName);

            foreach (var logsDir in logsDirectories)
            {
                var logsDirName = Path.GetFileName(logsDir);

                // Skip hash directories
                if (IsLanCacheHashDirectory(logsDirName))
                    continue;

                // Case-insensitive exact match
                if (string.Equals(logsDirName, cacheSubdirName, StringComparison.OrdinalIgnoreCase))
                {
                    return logsDir;
                }

                // Normalized name match (removes hyphens, underscores, handles pluralization)
                var normalizedLogsName = NormalizeName(logsDirName);
                if (string.Equals(normalizedLogsName, normalizedCacheName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Found logs directory via normalized match: {Cache} -> {Logs}",
                        cacheSubdirName, logsDirName);
                    return logsDir;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning logs directories in {Path}", baseLogsPath);
        }

        return null;
    }

    /// <summary>
    /// Normalize a directory name for flexible matching.
    /// Removes common separators and trailing 's' for pluralization.
    /// </summary>
    private static string NormalizeName(string name)
    {
        var normalized = name.ToLowerInvariant();

        // Remove common separators
        normalized = normalized.Replace("-", "").Replace("_", "").Replace(" ", "");

        // Remove trailing 's' for pluralization (but not for short names like 'logs')
        if (normalized.Length > 4 && normalized.EndsWith("s"))
        {
            normalized = normalized[..^1];
        }

        return normalized;
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
