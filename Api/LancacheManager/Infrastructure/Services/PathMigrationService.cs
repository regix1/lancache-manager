using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

public class PathMigrationService
{
    private readonly ILogger<PathMigrationService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IConfiguration _configuration;

    public PathMigrationService(
        ILogger<PathMigrationService> logger,
        IPathResolver pathResolver,
        IConfiguration configuration)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _configuration = configuration;
    }

    public PathMigrationResult MigrateLegacyDataLayout()
    {
        var result = new PathMigrationResult();
        var dataDirectory = _pathResolver.GetDataDirectory();

        MoveFileIfMissing(
            Path.Combine(dataDirectory, "state.json"),
            Path.Combine(_pathResolver.GetStateDirectory(), "state.json"),
            result,
            "state");

        MoveFileIfMissing(
            Path.Combine(dataDirectory, "gc-settings.json"),
            _pathResolver.GetSettingsPath("gc-settings.json"),
            result,
            "gc settings");

        MoveFileIfMissing(
            Path.Combine(dataDirectory, "log-rotation-settings.json"),
            _pathResolver.GetSettingsPath("log-rotation-settings.json"),
            result,
            "log rotation settings");

        MoveFileIfMissing(
            Path.Combine(dataDirectory, "pics_depot_mappings.json"),
            Path.Combine(_pathResolver.GetPicsDirectory(), "pics_depot_mappings.json"),
            result,
            "pics mappings");

        MoveFileIfMissing(
            Path.Combine(dataDirectory, "LancacheManager.db"),
            _pathResolver.GetDatabasePath(),
            result,
            "database");

        var apiKeyPathOverride = _configuration["Security:ApiKeyPath"];
        if (string.IsNullOrWhiteSpace(apiKeyPathOverride))
        {
            MoveFileIfMissing(
                Path.Combine(dataDirectory, "api_key.txt"),
                Path.Combine(_pathResolver.GetSecurityDirectory(), "api_key.txt"),
                result,
                "api key");
        }

        MoveDirectoryIfMissing(
            Path.Combine(dataDirectory, "cached-img"),
            _pathResolver.GetCachedImagesDirectory(),
            result,
            "cached images");

        MoveDirectoryIfMissing(
            Path.Combine(dataDirectory, "steam_auth"),
            Path.Combine(_pathResolver.GetSecurityDirectory(), "steam_auth"),
            result,
            "steam auth");

        MoveDirectoryIfMissing(
            Path.Combine(dataDirectory, "prefill-sessions"),
            _pathResolver.GetPrefillDirectory(),
            result,
            "prefill sessions");

        MoveMatchingFiles(
            dataDirectory,
            "rust_progress*.json",
            _pathResolver.GetOperationsDirectory(),
            result);

        return result;
    }

    private void MoveMatchingFiles(string sourceDirectory, string pattern, string destinationDirectory, PathMigrationResult result)
    {
        try
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            var files = Directory.GetFiles(sourceDirectory, pattern);
            if (files.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(destinationDirectory);

            foreach (var file in files)
            {
                var destFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
                MoveFileIfMissing(file, destFile, result, "rust progress");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy files from {Dir} with pattern {Pattern}", sourceDirectory, pattern);
        }
    }

    private void MoveFileIfMissing(string sourcePath, string destinationPath, PathMigrationResult result, string label)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return;
            }

            if (File.Exists(destinationPath))
            {
                _logger.LogDebug("Skipping legacy {Label} file migration; destination already exists: {Dest}", label, destinationPath);
                return;
            }

            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Move(sourcePath, destinationPath);
            result.FilesMoved++;
            _logger.LogInformation("Migrated {Label} file to {Dest}", label, destinationPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy {Label} file from {Source} to {Dest}", label, sourcePath, destinationPath);
        }
    }

    private void MoveDirectoryIfMissing(string sourcePath, string destinationPath, PathMigrationResult result, string label)
    {
        try
        {
            if (!Directory.Exists(sourcePath))
            {
                return;
            }

            if (!Directory.Exists(destinationPath))
            {
                var destParent = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }

                Directory.Move(sourcePath, destinationPath);
                result.DirectoriesMoved++;
                _logger.LogInformation("Migrated {Label} directory to {Dest}", label, destinationPath);
                return;
            }

            MoveDirectoryContents(sourcePath, destinationPath, result, label);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy {Label} directory from {Source} to {Dest}", label, sourcePath, destinationPath);
        }
    }

    private void MoveDirectoryContents(string sourcePath, string destinationPath, PathMigrationResult result, string label)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourcePath))
        {
            var name = Path.GetFileName(entry);
            var destEntry = Path.Combine(destinationPath, name);

            if (File.Exists(entry))
            {
                MoveFileIfMissing(entry, destEntry, result, label);
                continue;
            }

            if (Directory.Exists(entry))
            {
                MoveDirectoryIfMissing(entry, destEntry, result, label);
            }
        }

        if (Directory.GetFileSystemEntries(sourcePath).Length == 0)
        {
            Directory.Delete(sourcePath);
        }
    }
}

public class PathMigrationResult
{
    public int FilesMoved { get; set; }
    public int DirectoriesMoved { get; set; }
}
