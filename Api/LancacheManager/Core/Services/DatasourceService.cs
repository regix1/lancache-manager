using System.Globalization;
using System.Text.RegularExpressions;
using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Core.Services;

/// <summary>
/// Service for managing multiple LANCache datasources (log/cache locations).
/// Provides access to configured datasources with resolved paths.
/// </summary>
public class DatasourceService
{
    private static readonly Regex _datasourceNameRegex = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Maximum descendant depth auto-discovery will search below the configured root.
    /// Root is depth 0; depths 1-3 are eligible; children are never enumerated from depth 3.
    /// </summary>
    private const int MaxDiscoveryDepth = 3;

    private static bool IsValidDatasourceName(string name) =>
        !string.IsNullOrWhiteSpace(name) && _datasourceNameRegex.IsMatch(name);

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
                if (!IsValidDatasourceName(config.Name))
                    throw new ArgumentException(
                        "Datasource name must contain only letters, digits, dots, hyphens, and underscores.",
                        nameof(config.Name));

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
    /// Discover datasources by walking paired cache/log directories from the configured root
    /// through up to <see cref="MaxDiscoveryDepth"/> descendant levels.
    /// - Root (depth 0) becomes "Default" when it has valid top-level log/cache content.
    /// - Each valid nested pair (depths 1-3) is named from its cache leaf, as today.
    /// - A valid pair does not stop descent; eligible children are still explored below it.
    /// </summary>
    private List<DatasourceConfig> DiscoverDatasources()
    {
        var baseCachePath = _pathResolver.ResolvePath(
            _configuration["LanCache:CachePath"] ?? "cache");
        var baseLogsPath = _pathResolver.ResolvePath(
            _configuration["LanCache:LogPath"] ?? "logs");

        _logger.LogDebug("Auto-discovery scanning: Cache={CachePath}, Logs={LogsPath}", baseCachePath, baseLogsPath);

        if (!Directory.Exists(baseCachePath))
        {
            _logger.LogWarning("Auto-discovery: Cache directory does not exist: {Path}", baseCachePath);
            return new List<DatasourceConfig>();
        }

        if (!Directory.Exists(baseLogsPath))
        {
            _logger.LogWarning("Auto-discovery: Logs directory does not exist: {Path}", baseLogsPath);
            return new List<DatasourceConfig>();
        }

        var candidates = new List<DiscoveryCandidate>();
        var queue = new Queue<(string CachePath, string LogPath, int Depth, string RelativePath)>();
        queue.Enqueue((baseCachePath, baseLogsPath, 0, string.Empty));

        while (queue.Count > 0)
        {
            var (cachePath, logPath, depth, relativePath) = queue.Dequeue();
            var isRoot = depth == 0;

            if (HasRootLevelLogFile(logPath) && HasCacheContent(cachePath))
            {
                var name = isRoot ? "Default" : BuildNestedDatasourceName(cachePath);
                if (name != null)
                {
                    candidates.Add(new DiscoveryCandidate(name, cachePath, logPath, depth, relativePath));
                    _logger.LogDebug("Auto-discovery found valid pair: {Name} (cache: {Cache}, logs: {Logs}, depth: {Depth})",
                        name, cachePath, logPath, depth);
                }
            }

            if (depth < MaxDiscoveryDepth)
            {
                foreach (var (childCache, childLog) in GetChildPairs(cachePath, logPath))
                {
                    var childName = Path.GetFileName(childCache);
                    var childRelativePath = relativePath.Length == 0 ? childName : $"{relativePath}/{childName}";
                    queue.Enqueue((childCache, childLog, depth + 1, childRelativePath));
                }
            }
        }

        // Deduplicate by name (case-insensitive): shallower depth wins, then lexically earlier
        // normalized relative path. Later duplicates warn and are skipped.
        var deduped = new Dictionary<string, DiscoveryCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates
            .OrderBy(c => c.Depth)
            .ThenBy(c => NormalizeRelativePath(c.RelativePath), StringComparer.Ordinal))
        {
            if (deduped.TryGetValue(candidate.Name, out var existing))
            {
                _logger.LogWarning(
                    "Auto-discovery skipping duplicate datasource name '{Name}' at {Path} (already using {ExistingPath})",
                    candidate.Name, candidate.CachePath, existing.CachePath);
                continue;
            }

            deduped[candidate.Name] = candidate;
        }

        // Sort by name for consistent ordering, but keep Default first
        return deduped.Values
            .OrderBy(c => c.Name == "Default" ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new DatasourceConfig
            {
                Name = c.Name,
                CachePath = c.CachePath,
                LogPath = c.LogPath,
                Enabled = true
            })
            .ToList();
    }

    /// <summary>
    /// Build the display name for a valid nested (non-root) datasource pair from its cache leaf.
    /// Returns null (and logs a warning) when the name is reserved for the root ("default") or
    /// contains disallowed characters, so the caller skips recording without aborting traversal.
    /// </summary>
    private string? BuildNestedDatasourceName(string cachePath)
    {
        var leaf = Path.GetFileName(cachePath);

        // "Default" is reserved for the root pair; a nested leaf named "default" would
        // otherwise shadow it. Its content is already reachable via the root.
        if (string.Equals(leaf, "default", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Auto-discovery skipping nested datasource at '{Path}': the name 'default' is reserved for the root datasource",
                cachePath);
            return null;
        }

        var displayName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(leaf.ToLowerInvariant());

        if (!IsValidDatasourceName(displayName))
        {
            _logger.LogWarning(
                "Auto-discovery skipping subdirectory '{Name}': name contains disallowed characters (only letters, digits, dots, hyphens, and underscores are permitted)",
                displayName);
            return null;
        }

        return displayName;
    }

    /// <summary>
    /// Find eligible child cache/log directory pairs directly under a paired parent, for
    /// enqueueing at the next depth. Matches exact, then case-insensitive, then normalized
    /// names via <see cref="FindMatchingLogsDirectory"/>. When both parents have exactly one
    /// eligible child that did not match by name, the two are paired as an unnamed wrapper
    /// level, unless both are already independently content-valid leaves (which would be an
    /// unrelated cross-pair, e.g. a valid "steam" cache leaf next to a valid "epic" log leaf).
    /// </summary>
    private List<(string CachePath, string LogPath)> GetChildPairs(string parentCachePath, string parentLogPath)
    {
        var cacheChildren = GetEligibleChildDirectories(parentCachePath);
        var logChildren = GetEligibleChildDirectories(parentLogPath);
        var pairs = new List<(string CachePath, string LogPath)>();

        foreach (var cacheChild in cacheChildren)
        {
            var logMatch = FindMatchingLogsDirectory(parentLogPath, Path.GetFileName(cacheChild));
            if (logMatch != null)
            {
                pairs.Add((cacheChild, logMatch));
            }
        }

        if (pairs.Count == 0 && cacheChildren.Count == 1 && logChildren.Count == 1)
        {
            var singleCache = cacheChildren[0];
            var singleLog = logChildren[0];
            var bothAlreadyValidLeaves = HasCacheContent(singleCache) && HasRootLevelLogFile(singleLog);

            if (!bothAlreadyValidLeaves)
            {
                pairs.Add((singleCache, singleLog));
            }
        }

        return pairs;
    }

    /// <summary>
    /// List immediate subdirectories eligible for traversal: not hidden/underscore-prefixed,
    /// not a LANCache hash bucket, and not a reparse point/symlink. Returns an empty list both
    /// when there are no eligible children AND when enumeration fails (logged as a warning) -
    /// the two cases are indistinguishable to the caller by design, matching the sibling helpers.
    /// </summary>
    private List<string> GetEligibleChildDirectories(string parentPath)
    {
        try
        {
            return Directory.GetDirectories(parentPath)
                .Where(dir =>
                {
                    var name = Path.GetFileName(dir);
                    return !IsHiddenOrUnderscoreName(name)
                        && !IsLanCacheHashDirectory(name)
                        && !IsReparsePoint(dir);
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error enumerating child directories in {Path}", parentPath);
            return new List<string>();
        }
    }

    /// <summary>
    /// Check whether the logs directory actually holds access-log SOURCES: the monolithic
    /// access.log series, bare-metal per-service *-access.log series, or a bare-metal
    /// parent whose http/ child carries the per-service topology. A bare nginx-error.log
    /// or stream log is never evidence — the old any-*.log rule accepted the bare-metal
    /// parent dir and then reported zero-work success forever.
    /// Returns false both when no source exists AND when the directory scan fails.
    /// </summary>
    private bool HasRootLevelLogFile(string logsPath)
    {
        try
        {
            var resolved = LogSourceLayout.ResolveAccessLogDirectory(logsPath);
            var stems = LogSourceLayout.EnumerateStems(resolved);
            if (stems.Count > 0)
            {
                _logger.LogDebug("Found access-log source(s) in {Path}: {Stems}",
                    resolved, string.Join(", ", stems));
                return true;
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
    /// Returns false both when no cache content exists AND when the directory scan fails
    /// (logged as a warning) - the two cases are indistinguishable to the caller by design.
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
    /// Check if a directory name is hidden (dot-prefixed) or underscore-prefixed, and therefore
    /// excluded from traversal at every depth.
    /// </summary>
    private static bool IsHiddenOrUnderscoreName(string name) =>
        name.StartsWith(".") || name.StartsWith("_");

    /// <summary>
    /// Check if a filesystem entry is a reparse point (symlink/junction), excluded from
    /// traversal at every depth to avoid cycles. Inaccessible entries are treated as not a
    /// reparse point; enumeration failures at the parent level are handled by the caller.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Find a matching logs directory for a given cache subdirectory name.
    /// Uses case-insensitive matching and normalized name comparison.
    /// Returns null both when no match is found AND when the directory scan fails (logged as a
    /// warning) - the two cases are indistinguishable to the caller by design.
    /// </summary>
    private string? FindMatchingLogsDirectory(string baseLogsPath, string cacheSubdirName)
    {
        // Try exact match first
        var exactMatch = Path.Combine(baseLogsPath, cacheSubdirName);
        if (Directory.Exists(exactMatch) && !IsReparsePoint(exactMatch))
        {
            return exactMatch;
        }

        // Scan all directories and find case-insensitive or normalized matches. Order the
        // eligible directories by ordinal name so an ambiguous layout (two normalized-equal
        // log dirs) always resolves to the same winner regardless of filesystem enumeration
        // order. Exact and case-insensitive matches are still preferred over normalized ones.
        try
        {
            var logsDirectories = Directory.GetDirectories(baseLogsPath)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal);
            var normalizedCacheName = NormalizeName(cacheSubdirName);

            foreach (var logsDir in logsDirectories)
            {
                var logsDirName = Path.GetFileName(logsDir);

                // Skip hash directories, hidden/underscore directories, and reparse points/symlinks
                if (IsLanCacheHashDirectory(logsDirName) || IsHiddenOrUnderscoreName(logsDirName) || IsReparsePoint(logsDir))
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
    /// Normalize a relative discovery path (segments joined by '/') for deterministic duplicate
    /// tie-breaking, by normalizing each segment the same way single directory names are
    /// normalized for matching.
    /// </summary>
    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return string.Empty;

        return string.Join('/', relativePath.Split('/').Select(NormalizeName));
    }

    /// <summary>
    /// Resolve paths and validate a datasource configuration.
    /// Returns null when resolution fails; the failure is always logged as an error before
    /// returning, so callers that skip a null result are not silently swallowing it.
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

            var datasource = new ResolvedDatasource
            {
                Name = config.Name,
                CachePath = cachePath,
                ConfiguredLogPath = logDir,
                LogPath = logDir,
                LogFilePath = Path.Combine(logDir, "access.log"),
                Enabled = config.Enabled,
                CacheWritable = _pathResolver.IsDirectoryWritable(cachePath)
            };
            // RefreshLogSources performs the bare-metal <logs> -> <logs>/http descent from
            // the configured root; doing it per refresh (not once here) means an http/
            // folder that appears after startup is still discovered. Probe writability only
            // after the descent so LogsWritable reflects the directory the record processor
            // actually writes positions against, not a parent mount whose ownership or mode
            // differs from the resolved http/ child.
            datasource.RefreshLogSources();
            datasource.LogsWritable = _pathResolver.IsDirectoryWritable(datasource.LogPath);
            return datasource;
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
    /// Convenience method: returns the cache path of the default datasource, or null if none configured.
    /// </summary>
    public string? ResolvePrimaryCachePath()
    {
        return GetDefaultDatasource()?.CachePath;
    }

    /// <summary>
    /// Check if a specific datasource's cache directory is writable.
    /// </summary>
    public bool IsCacheWritable(string datasourceName)
    {
        var datasource = GetDatasource(datasourceName);
        return datasource?.CacheWritable ?? false;
    }

    /// <summary>
    /// Check if a specific datasource's logs directory is writable.
    /// </summary>
    public bool IsLogsWritable(string datasourceName)
    {
        var datasource = GetDatasource(datasourceName);
        return datasource?.LogsWritable ?? false;
    }

    /// <summary>
    /// Get datasource info for all configured datasources (for API responses).
    /// </summary>
    public List<DatasourceInfo> GetDatasourceInfos()
    {
        return _datasources.Select(d =>
        {
            d.RefreshLogSources();
            return new DatasourceInfo
            {
                Name = d.Name,
                CachePath = d.CachePath,
                LogsPath = d.LogPath,
                CacheWritable = d.CacheWritable,
                LogsWritable = d.LogsWritable,
                Enabled = d.Enabled,
                Layout = d.Layout,
                SourceCount = d.LogSourceStems.Count
            };
        }).ToList();
    }

    /// <summary>
    /// Re-check directory permissions for all datasources and update cached writable flags.
    /// Call this when permissions may have changed (e.g., Docker volume remount, PUID/PGID change).
    /// </summary>
    public void RefreshPermissions()
    {
        foreach (var ds in _datasources)
        {
            ds.CacheWritable = _pathResolver.IsDirectoryWritable(ds.CachePath);
            // Re-run the <logs> -> <logs>/http descent before probing writability so
            // LogsWritable is measured against the resolved LogPath rather than a parent
            // mount whose permissions differ from the http/ child that holds the sources.
            ds.RefreshLogSources();
            ds.LogsWritable = _pathResolver.IsDirectoryWritable(ds.LogPath);
        }
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

    /// <summary>
    /// Presentation-only source layout: monolithic | bare_metal | mixed. Derived from the
    /// stems on disk at the last refresh; never drives capability decisions by itself.
    /// </summary>
    public string Layout { get; set; } = LogSourceLayout.LayoutMonolithic;

    /// <summary>
    /// Logical source stems present at the last refresh (access.log, steam-access.log, ...).
    /// </summary>
    public IReadOnlyList<string> LogSourceStems { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Current (non-rotated) file paths for every source stem at the last refresh.
    /// LogFilePath above stays the legacy access.log path.
    /// </summary>
    public IReadOnlyList<string> LogFilePaths { get; set; } = Array.Empty<string>();

    private readonly object _refreshLock = new();

    /// <summary>
    /// The log directory exactly as configured, BEFORE any bare-metal http/ descent.
    /// Descent re-resolves from here on every refresh so the chosen directory never
    /// freezes on a stale answer.
    /// </summary>
    public string ConfiguredLogPath { get; set; } = string.Empty;

    /// <summary>
    /// Re-enumerates the source stems on disk, re-running the http/ descent from the
    /// configured root first. Cheap (one or two directory listings); called at resolve
    /// time and by consumers that need current sources (live monitor, API infos).
    /// Serialized so concurrent refreshes from different singleton services cannot
    /// interleave their writes; all properties are always derived from ONE listing.
    /// </summary>
    public void RefreshLogSources()
    {
        lock (_refreshLock)
        {
            var root = string.IsNullOrEmpty(ConfiguredLogPath) ? LogPath : ConfiguredLogPath;
            var resolvedDir = LogSourceLayout.ResolveAccessLogDirectory(root);
            if (!string.Equals(resolvedDir, LogPath, StringComparison.Ordinal))
            {
                LogPath = resolvedDir;
                LogFilePath = Path.Combine(resolvedDir, "access.log");
            }

            var stems = LogSourceLayout.EnumerateStems(LogPath)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            LogSourceStems = stems;
            Layout = LogSourceLayout.DeriveLayout(stems);
            LogFilePaths = stems
                .Select(stem => Path.Combine(LogPath, stem))
                .Where(File.Exists)
                .ToList();
        }
    }
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
    /// <summary>Presentation-only source layout: monolithic | bare_metal | mixed.</summary>
    public string Layout { get; set; } = LogSourceLayout.LayoutMonolithic;
    /// <summary>Number of logical access-log sources currently on disk.</summary>
    public int SourceCount { get; set; }
}

/// <summary>
/// A candidate datasource pair discovered during bounded auto-discovery traversal, before
/// deduplication. Carries the depth and relative path used to resolve duplicate-name priority.
/// </summary>
internal sealed record DiscoveryCandidate(string Name, string CachePath, string LogPath, int Depth, string RelativePath);
