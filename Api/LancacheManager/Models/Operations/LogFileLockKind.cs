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
    Rewrite,

    /// <summary>
    /// A signal asking nginx to reopen its logs. It changes no file, row or position, so the import and
    /// the speed tracker keep running; it waits for a step that holds the logs and keeps new steps out
    /// while it signals, because a reopen inside a delete step makes nginx recreate the deleted file.
    /// </summary>
    Reopen
}
