namespace LancacheManager.Models;

/// <summary>
/// An event tag on a download, in the shape a batch/list endpoint needs (no timing fields).
/// </summary>
public class DownloadEventTag
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ColorIndex { get; set; }
    public bool AutoTagged { get; set; }
}

/// <summary>
/// An event tag on a download, including when it started/ended and when the tag itself was applied.
/// </summary>
public class DownloadEventAssociation
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ColorIndex { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public bool AutoTagged { get; set; }
    public DateTime TaggedAtUtc { get; set; }
}

/// <summary>
/// Response for a single download looked up by ID, with its event tags.
/// </summary>
public class DownloadWithEventsResponse
{
    public Download Download { get; set; } = null!;
    public List<DownloadEventAssociation> Events { get; set; } = new();
}

/// <summary>
/// The event tags for one download, keyed by download ID in a batch lookup.
/// </summary>
public class DownloadEventBatchEntry
{
    public List<DownloadEventTag> Events { get; set; } = new();
}

/// <summary>
/// One download and its event tags, as returned in a time-range listing.
/// </summary>
public class DownloadWithEventTags
{
    public Download Download { get; set; } = null!;
    public List<DownloadEventTag> Events { get; set; } = new();
}
