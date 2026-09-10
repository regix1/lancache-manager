namespace LancacheManager.Infrastructure.Platform;

/// <summary>
/// Outcome of a directory write-access probe. Distinguishes a deliberately read-only mount
/// from an ownership/mode denial so callers can log an accurate, low-noise reason.
/// </summary>
public enum DirectoryWriteAccess
{
    /// <summary>The directory accepts writes.</summary>
    Writable,

    /// <summary>The directory does not exist.</summary>
    DirectoryMissing,

    /// <summary>The path is mounted read-only, so writes are disabled by design.</summary>
    ReadOnlyMount,

    /// <summary>Writes are denied by ownership or file mode (commonly a PUID/PGID mismatch).</summary>
    OwnershipOrModeDenied,

    /// <summary>Write access could not be determined.</summary>
    Indeterminate
}
