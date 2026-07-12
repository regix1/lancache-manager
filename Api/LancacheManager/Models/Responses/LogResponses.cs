namespace LancacheManager.Models;

/// <summary>
/// Response for log directory information
/// </summary>
public class LogInfoResponse
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
}

/// <summary>
/// Response for log position reset operation
/// </summary>
public class LogPositionResponse
{
    public string Message { get; set; } = string.Empty;
    public long Position { get; set; }
}

/// <summary>
/// Response for log service removal start
/// </summary>
public class LogRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }
}

/// <summary>Recovery response for the active log-removal operation.</summary>
public sealed class LogRemovalStatusResponse
{
    public bool IsProcessing { get; set; }
    public string? Service { get; set; }
    public string? Datasource { get; set; }
    public Guid? OperationId { get; set; }
    public int FilesProcessed { get; set; }
    public long LinesProcessed { get; set; }
    public long LinesRemoved { get; set; }
    public double? PercentComplete { get; set; }
    public OperationStatus? Status { get; set; }
    public string? StageKey { get; set; }
    public IReadOnlyDictionary<string, object?> Context { get; set; } = new Dictionary<string, object?>();
}

/// <summary>
/// Response for log entries count
/// </summary>
public class LogEntriesCountResponse
{
    public long Count { get; set; }
}
