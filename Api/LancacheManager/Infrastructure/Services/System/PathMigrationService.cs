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
            Path.Combine(dataDirectory, "log-rotation-settings.json"),
            _pathResolver.GetSettingsPath("log-rotation-settings.json"),
            result,
            "log rotation settings");

        MoveFileIfMissing(
            Path.Combine(dataDirectory, "pics_depot_mappings.json"),
            Path.Combine(_pathResolver.GetPicsDirectory(), "pics_depot_mappings.json"),
            result,
            "pics mappings");

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
            _pathResolver.GetImagesDirectory(),
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

        RemoveLegacySqliteDatabase(dataDirectory);
        RemoveOrphanedStructuralScanState();

        return result;
    }

    /// <summary>
    /// An install that never ran the old SQLite import keeps a database nothing will read again.
    /// Everything else about it looks healthy: setup is already marked complete in state.json and
    /// the log offsets are stored, so the app skips the wizard and does not re-read the old lines.
    /// The file and its write-ahead siblings go, along with the markers the old import wrote, so
    /// the data volume does not carry a copy of history that nothing can reach. This is one way:
    /// once removed there is no downgrading to a build that could still import it.
    /// </summary>
    private void RemoveLegacySqliteDatabase(string dataDirectory)
    {
        // Older builds kept it beside the data root before a later one moved it into db/.
        string[] databases =
        [
            Path.Combine(dataDirectory, "db", "LancacheManager.db"),
            Path.Combine(dataDirectory, "LancacheManager.db")
        ];

        foreach (var database in databases.Where(File.Exists))
        {
            _logger.LogWarning(
                "Removing the old SQLite database at {Path}. This release reads PostgreSQL only, so "
                + "downloads recorded before the switch are gone rather than hidden.",
                database);

            // The -wal and -shm siblings are meaningless without it and would otherwise be left
            // behind on their own.
            foreach (var path in new[] { database, $"{database}-wal", $"{database}-shm" })
            {
                DeleteIfPresent(path);
            }
        }

        // Bookkeeping the deleted import wrote. Nothing reads these now.
        DeleteIfPresent(Path.Combine(dataDirectory, "postgres-migration.complete"));
        DeleteIfPresent(Path.Combine(dataDirectory, "postgresql", ".migration_complete"));
    }

    private void DeleteIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reclaiming disk must never stop the app from starting; the next start tries again.
            _logger.LogWarning(ex, "Could not remove {Path}", path);
        }
    }

    /// <summary>
    /// The corruption scanner keeps its baselines in PostgreSQL now, and nothing in the app names
    /// this directory any more. Left alone it would sit on the data volume forever: one file per
    /// cache root, each carrying a row per cached file. The baselines are derived state, so the
    /// next scan rebuilds whatever is thrown away here.
    /// </summary>
    private void RemoveOrphanedStructuralScanState()
    {
        var legacyStateDirectory = Path.Combine(
            _pathResolver.GetStateDirectory(),
            "corruption-structural");
        if (!Directory.Exists(legacyStateDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(legacyStateDirectory, recursive: true);
            _logger.LogInformation(
                "Removed the old structural scan state at {Path}; those baselines are in PostgreSQL now",
                legacyStateDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reclaiming disk must never stop the app from starting; the next start tries again.
            _logger.LogWarning(
                ex,
                "Could not remove the old structural scan state at {Path}",
                legacyStateDirectory);
        }
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
