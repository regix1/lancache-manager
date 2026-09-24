using LancacheManager.Models;

namespace LancacheManager.Core.Services.EpicMapping;

/// <summary>
/// Auth status response for the REST endpoint.
/// </summary>
public class EpicMappingAuthStatus
{
    public bool CanManage { get; set; }
    public bool CanSignIn { get; set; }
    public bool CanLogout { get; set; }
    public bool CanCancel { get; set; }
    public bool CanRecover { get; set; }
    public string? OwnershipReason { get; set; }
    public Guid? AttemptId { get; set; }
    public DateTime? LoginExpiresAtUtc { get; set; }
    public bool IsAuthenticated { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? LastCollectionUtc { get; set; }
    public int GamesDiscovered { get; set; }
}

/// <summary>
/// Schedule status response for the REST endpoint.
/// </summary>
public class EpicScheduleStatus
{
    public double RefreshIntervalHours { get; set; }
    public bool IsProcessing { get; set; }
    public DateTime? LastRefreshTime { get; set; }
    public double NextRefreshIn { get; set; }
    public bool IsAuthenticated { get; set; }
    public Guid? OperationId { get; set; }
    public EpicMappingStatus Status { get; set; } = EpicMappingStatus.Idle;
    public double ProgressPercent { get; set; }
    public string? StatusMessage { get; set; }
}

/// <summary>
/// Result of a merge operation.
/// </summary>
public class MergeResult
{
    public int NewGames { get; set; }
    public int UpdatedGames { get; set; }
    public int UnchangedGames { get; set; }
    public int TotalGames { get; set; }
}

/// <summary>
/// Statistics about the Epic game mapping database.
/// </summary>
public class EpicMappingStats
{
    public int TotalGames { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    public DateTime? OldestGameUtc { get; set; }
    public int DistinctSources { get; set; }
    public int CdnPatterns { get; set; }
}
