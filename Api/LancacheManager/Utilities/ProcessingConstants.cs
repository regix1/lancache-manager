namespace LancacheManager.Utilities;

/// <summary>
/// Centralized constants for log processing, batch operations, and session management
/// </summary>
public static class ProcessingConstants
{
    /// <summary>
    /// Maximum number of concurrent batch processing operations
    /// </summary>
    public const int MaxConcurrentBatches = 4;

    /// <summary>
    /// Batch size for bulk log processing operations
    /// </summary>
    public const int BulkProcessingBatchSize = 2000;

    /// <summary>
    /// Batch size for real-time log processing operations
    /// </summary>
    public const int RealtimeBatchSize = 50;

    /// <summary>
    /// Time gap that determines when a new download session should be created
    /// If downloads from the same client are more than this time apart, they're considered separate sessions
    /// </summary>
    public static readonly TimeSpan SessionGapTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often to save progress during bulk log processing
    /// </summary>
    public static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum number of log entries to queue before blocking
    /// </summary>
    public const int MaxQueueSize = 100000;

    /// <summary>
    /// Buffer size for reading log files
    /// </summary>
    public const int LogReadBufferSize = 131072; // 128KB

    /// <summary>
    /// Delay between file polling attempts when watching log files
    /// </summary>
    public static readonly TimeSpan FilePollDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Maximum time to wait for log file to become available
    /// </summary>
    public static readonly TimeSpan LogFileWaitTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Default timeout for database operations
    /// </summary>
    public static readonly TimeSpan DatabaseOperationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Batch size for database bulk inserts
    /// </summary>
    public const int DatabaseBatchSize = 5000;
}