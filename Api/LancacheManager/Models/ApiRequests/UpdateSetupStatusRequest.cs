using System.Text.Json.Serialization;

namespace LancacheManager.Models.ApiRequests;

/// <summary>
/// Request body for <c>PATCH /api/system/setup</c>.
/// Every property is optional and <c>null</c> means "not provided" — the controller only
/// writes a given state field when the corresponding property is non-null.
/// <see cref="CompletedPlatforms"/> is intentionally typed <c>string?</c> (not a list)
/// because the frontend persists a serialized JSON object
/// (<c>{"steam":"github"|"steam"|null,"epic":bool}</c>) and <c>AppState.CompletedPlatforms</c>
/// stores it verbatim — see <c>Web/src/hooks/useInitializationFlow.ts</c>
/// (<c>JSON.stringify(completedPlatforms)</c>) and <c>StateModels.cs</c>.
/// </summary>
public class UpdateSetupStatusRequest
{
    /// <summary>
    /// When set, marks the setup as complete (<c>true</c>) or incomplete (<c>false</c>)
    /// and clears all wizard state fields (<see cref="CurrentSetupStep"/>,
    /// <see cref="DataSourceChoice"/>, <see cref="CompletedPlatforms"/>).
    /// </summary>
    [JsonPropertyName("completed")]
    public bool? Completed { get; set; }

    /// <summary>
    /// Current step in the setup wizard. Uses the <see cref="SetupStep"/> enum with
    /// kebab-case wire values ("database-setup", "platform-setup", etc.).
    /// </summary>
    [JsonPropertyName("currentSetupStep")]
    public SetupStep? CurrentSetupStep { get; set; }

    /// <summary>
    /// The data source the user selected during the wizard. Uses the
    /// <see cref="Models.DataSourceChoice"/> enum with lowercase wire values
    /// ("github", "steam", "epic", "skip").
    /// </summary>
    [JsonPropertyName("dataSourceChoice")]
    public DataSourceChoice? DataSourceChoice { get; set; }

    /// <summary>
    /// Opaque JSON string describing per-platform completion state. The frontend
    /// serializes a <c>{ steam, epic }</c> object and this server persists it
    /// verbatim — no server-side interpretation beyond pass-through.
    /// </summary>
    [JsonPropertyName("completedPlatforms")]
    public string? CompletedPlatforms { get; set; }
}
