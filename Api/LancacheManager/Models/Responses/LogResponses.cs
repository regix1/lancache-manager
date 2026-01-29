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
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Response for cancellation operations
/// </summary>
public class CancellationResponse
{
    public string Message { get; set; } = string.Empty;
    public bool Cancelled { get; set; }
}

/// <summary>
/// Response for log entries count
/// </summary>
public class LogEntriesCountResponse
{
    public long Count { get; set; }
}
