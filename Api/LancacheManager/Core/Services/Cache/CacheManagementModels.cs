using System.Text.Json.Serialization;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    private class LogCountProgress
    {
        [JsonPropertyName("is_processing")]
        public bool IsProcessing { get; set; }

        [JsonPropertyName("percent_complete")]
        public double PercentComplete { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("lines_processed")]
        public ulong LinesProcessed { get; set; }

        [JsonPropertyName("service_counts")]
        public Dictionary<string, ulong>? ServiceCounts { get; set; }
    }

    private class GameRemovalProgress
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("stageKey")]
        public string StageKey { get; set; } = string.Empty;

        [JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

        [JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [JsonPropertyName("filesProcessed")]
        public int FilesProcessed { get; set; }

        [JsonPropertyName("totalFiles")]
        public int TotalFiles { get; set; }
    }

    private class ServiceRemovalProgress
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("stageKey")]
        public string StageKey { get; set; } = string.Empty;

        [JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

        [JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [JsonPropertyName("filesProcessed")]
        public int FilesProcessed { get; set; }

        [JsonPropertyName("totalFiles")]
        public int TotalFiles { get; set; }
    }

    public class GameCacheRemovalReport
    {
        [JsonPropertyName("game_app_id")]
        public long GameAppId { get; set; }

        [JsonPropertyName("game_name")]
        public string GameName { get; set; } = string.Empty;

        [JsonPropertyName("cache_files_deleted")]
        public int CacheFilesDeleted { get; set; }

        [JsonPropertyName("total_bytes_freed")]
        public ulong TotalBytesFreed { get; set; }

        [JsonPropertyName("empty_dirs_removed")]
        public int EmptyDirsRemoved { get; set; }

        [JsonPropertyName("log_entries_removed")]
        public ulong LogEntriesRemoved { get; set; }

        [JsonPropertyName("log_lines_removed_by_source")]
        public Dictionary<string, long> LogLinesRemovedBySource { get; set; } = new();

        [JsonPropertyName("log_lines_removed_before_position_by_source")]
        public Dictionary<string, long> LogLinesRemovedBeforePositionBySource { get; set; } = new();

        [JsonPropertyName("depot_ids")]
        public List<long> DepotIds { get; set; } = new();
    }

    public class ServiceCacheRemovalReport
    {
        [JsonPropertyName("service_name")]
        public string ServiceName { get; set; } = string.Empty;

        [JsonPropertyName("cache_files_deleted")]
        public int CacheFilesDeleted { get; set; }

        [JsonPropertyName("total_bytes_freed")]
        public ulong TotalBytesFreed { get; set; }

        [JsonPropertyName("log_entries_removed")]
        public ulong LogEntriesRemoved { get; set; }

        [JsonPropertyName("log_lines_removed_by_source")]
        public Dictionary<string, long> LogLinesRemovedBySource { get; set; } = new();

        [JsonPropertyName("log_lines_removed_before_position_by_source")]
        public Dictionary<string, long> LogLinesRemovedBeforePositionBySource { get; set; } = new();

        [JsonPropertyName("database_entries_deleted")]
        public int DatabaseEntriesDeleted { get; set; }
    }

    private sealed record RemovalDatasourceContext(
        ResolvedDatasource Datasource,
        int ExecutionIndex,
        int TotalConfiguredDatasources,
        string OutputJsonPath,
        string ProgressJsonPath);

    private sealed record RemovalExecutionPlan(
        IReadOnlyList<RemovalDatasourceContext> RunnableDatasources,
        int DatasourcesSkipped);

    private sealed record RustRemovalProcessResult(
        ResolvedDatasource Datasource,
        string OutputJsonPath,
        string ProgressJsonPath,
        string StdOut,
        string StdErr);

    private class CacheScanProgress
    {
        [JsonPropertyName("stageKey")]
        public string? StageKey { get; set; }

        [JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [JsonPropertyName("directoriesScanned")]
        public long DirectoriesScanned { get; set; }

        [JsonPropertyName("totalDirectories")]
        public long TotalDirectories { get; set; }

        [JsonPropertyName("totalBytes")]
        public long TotalBytes { get; set; }

        [JsonPropertyName("totalFiles")]
        public long TotalFiles { get; set; }

        [JsonPropertyName("calibrationStep")]
        public int CalibrationStep { get; set; }

        [JsonPropertyName("calibrationTotalSteps")]
        public int CalibrationTotalSteps { get; set; }
    }

    private class CacheSizeResult
    {
        [JsonPropertyName("totalBytes")]
        public ulong TotalBytes { get; set; }

        [JsonPropertyName("totalFiles")]
        public ulong TotalFiles { get; set; }

        [JsonPropertyName("totalDirectories")]
        public ulong TotalDirectories { get; set; }

        [JsonPropertyName("hexDirectories")]
        public int HexDirectories { get; set; }

        [JsonPropertyName("scanDurationMs")]
        public ulong ScanDurationMs { get; set; }

        [JsonPropertyName("estimatedDeletionTimes")]
        public CacheSizeEstimates EstimatedDeletionTimes { get; set; } = new();

        [JsonPropertyName("formattedSize")]
        public string FormattedSize { get; set; } = string.Empty;
    }

    private class CacheSizeEstimates
    {
        [JsonPropertyName("preserveSeconds")]
        public double PreserveSeconds { get; set; }

        [JsonPropertyName("fullSeconds")]
        public double FullSeconds { get; set; }

        [JsonPropertyName("rsyncSeconds")]
        public double RsyncSeconds { get; set; }

        [JsonPropertyName("preserveFormatted")]
        public string PreserveFormatted { get; set; } = string.Empty;

        [JsonPropertyName("fullFormatted")]
        public string FullFormatted { get; set; } = string.Empty;

        [JsonPropertyName("rsyncFormatted")]
        public string RsyncFormatted { get; set; } = string.Empty;
    }

    public class CachedCacheScan
    {
        public CacheSizeResponse ScanResult { get; set; } = new();
        public long UsedCacheSizeAtScan { get; set; }
        public Dictionary<string, long> UsedCacheSizeByMountAtScan { get; set; } = new();
        public Dictionary<string, CacheSizeResponse> RootResults { get; set; } = new();
        public DateTime ScannedAtUtc { get; set; }
    }
}
