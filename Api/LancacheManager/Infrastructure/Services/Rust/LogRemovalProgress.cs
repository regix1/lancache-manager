using System.Text.Json.Serialization;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Mirror of the Rust log remover's polled progress JSON.
/// </summary>
public class LogRemovalProgress : RustProgressBase
{
    [JsonPropertyName("files_processed")]
    public int FilesProcessed { get; set; }

    [JsonPropertyName("lines_processed")]
    public long LinesProcessed { get; set; }

    [JsonPropertyName("lines_removed")]
    public long LinesRemoved { get; set; }

    /// <summary>
    /// Removed-line counts per stem for the REWRITTEN (monolithic) sources. Deleted
    /// per-service series report nothing here; their stems are cleared outright.
    /// </summary>
    [JsonPropertyName("lines_removed_by_stem")]
    public Dictionary<string, long>? LinesRemovedByStem { get; set; }

    /// <summary>
    /// The already-read subset of <see cref="LinesRemovedByStem"/>; the amount each
    /// stem's saved position comes back by.
    /// </summary>
    [JsonPropertyName("lines_removed_before_position_by_stem")]
    public Dictionary<string, long>? LinesRemovedBeforePositionByStem { get; set; }
}
