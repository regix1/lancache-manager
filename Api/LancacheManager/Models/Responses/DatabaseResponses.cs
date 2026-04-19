namespace LancacheManager.Models;

/// <summary>
/// Response for database reset operation start
/// </summary>
public class DatabaseResetStartResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}

/// <summary>
/// Response for selected tables reset operation start
/// </summary>
public class SelectedTablesResetResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public List<string> Tables { get; set; } = new();
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}

/// <summary>
/// Response for database reset status.
/// <see cref="Status"/> is the canonical <see cref="OperationStatus"/> — <c>null</c>
/// means no reset is in flight (the previous <c>"idle"</c> sentinel). Granular per-phase
/// detail is carried by <see cref="Message"/> rather than bespoke status strings.
/// </summary>
public class DatabaseResetStatusResponse
{
    public bool IsProcessing { get; set; }
    public OperationStatus? Status { get; set; }
    public string? Message { get; set; }
    public int? PercentComplete { get; set; }
}

/// <summary>
/// Response for data import status check
/// </summary>
public class DataImportStatusResponse
{
    public bool IsProcessing { get; set; }
    public OperationStatus? Status { get; set; }
    public string? Message { get; set; }
    public double? PercentComplete { get; set; }
    public Guid? OperationId { get; set; }
}

/// <summary>
/// Response for data migration import result
/// </summary>
public class MigrationImportResponse
{
    public string Message { get; set; } = string.Empty;
    public ulong TotalRecords { get; set; }
    public ulong Imported { get; set; }
    public ulong Skipped { get; set; }
    public ulong Errors { get; set; }
    public string? BackupPath { get; set; }
}

/// <summary>
/// Response for connection validation
/// </summary>
public class ConnectionValidationResponse
{
    public bool Valid { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? RecordCount { get; set; }
}

/// <summary>
/// Progress tracking for data migration operations
/// </summary>
public class MigrationProgress
{
    [System.Text.Json.Serialization.JsonPropertyName("is_processing")]
    public bool IsProcessing { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("percent_complete")]
    public double PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("records_processed")]
    public ulong RecordsProcessed { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("records_imported")]
    public ulong RecordsImported { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("records_skipped")]
    public ulong RecordsSkipped { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("records_errors")]
    public ulong RecordsErrors { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("backup_path")]
    public string? BackupPath { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}
