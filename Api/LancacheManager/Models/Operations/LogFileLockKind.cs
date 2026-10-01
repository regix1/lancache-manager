namespace LancacheManager.Models;

/// <summary>
/// What the holder of a <see cref="LogFileLock"/> does with the datasource logs.
/// </summary>
public enum LogFileLockKind
{
    /// <summary>A log import pass reading new lines and saving its positions.</summary>
    Ingest,

    /// <summary>A step that deletes Downloads or LogEntries rows or lowers stored log positions.</summary>
    Rows,

    /// <summary>A step that rewrites or deletes a log file.</summary>
    Rewrite
}
