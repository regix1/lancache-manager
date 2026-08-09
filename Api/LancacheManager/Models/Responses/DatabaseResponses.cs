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
/// <see cref="Status"/> is the canonical <see cref="OperationStatus"/> - <c>null</c>
/// means no reset is in flight (the previous <c>"idle"</c> sentinel). Granular per-phase
/// detail is carried by <see cref="Message"/> rather than bespoke status strings.
/// </summary>
public class DatabaseResetStatusResponse
{
    public bool IsProcessing { get; set; }
    public OperationStatus? Status { get; set; }

    /// <summary>Human-readable progress message. Null until the reset has produced its first progress snapshot.</summary>
    public string? Message { get; set; }

    /// <summary>Percent complete, 0-100. Null until the reset has produced its first progress snapshot.</summary>
    public double? PercentComplete { get; set; }

    /// <summary>The tracked operation's id. Null when <see cref="IsProcessing"/> is false.</summary>
    public Guid? OperationId { get; set; }

    /// <summary>Localization key for the current stage. Null until the reset has produced its first progress snapshot.</summary>
    public string? StageKey { get; set; }
    public IReadOnlyDictionary<string, object?> Context { get; set; } = new Dictionary<string, object?>();

    /// <summary>Tables cleared so far in a selected-tables reset. Null for a full-database reset or before the first progress snapshot.</summary>
    public int? TablesCleared { get; set; }

    /// <summary>Total tables targeted by a selected-tables reset. Null for a full-database reset or before the first progress snapshot.</summary>
    public int? TotalTables { get; set; }

    /// <summary>Files deleted so far. Null until the reset has produced its first progress snapshot.</summary>
    public int? FilesDeleted { get; set; }
}

/// <summary>
/// Response for data import status check
/// </summary>
public class DataImportStatusResponse
{
    public bool IsProcessing { get; set; }

    /// <summary>The tracked operation's status. Null when <see cref="IsProcessing"/> is false.</summary>
    public OperationStatus? Status { get; set; }

    /// <summary>Human-readable progress message. Null when <see cref="IsProcessing"/> is false.</summary>
    public string? Message { get; set; }

    /// <summary>Percent complete, 0-100. Null when <see cref="IsProcessing"/> is false.</summary>
    public double? PercentComplete { get; set; }

    /// <summary>The tracked operation's id. Null when <see cref="IsProcessing"/> is false.</summary>
    public Guid? OperationId { get; set; }

    /// <summary>Localization key for the current stage. Null when <see cref="IsProcessing"/> is false.</summary>
    public string? StageKey { get; set; }
    public IReadOnlyDictionary<string, object?> Context { get; set; } = new Dictionary<string, object?>();
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
}

/// <summary>
/// Response for connection validation
/// </summary>
public class ConnectionValidationResponse
{
    public bool Valid { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>Row count read from the source database's Downloads table. Null when <see cref="Valid"/> is false.</summary>
    public int? RecordCount { get; set; }
}
