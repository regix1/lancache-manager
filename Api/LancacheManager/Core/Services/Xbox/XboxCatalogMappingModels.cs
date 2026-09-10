namespace LancacheManager.Core.Services.Xbox;

/// <summary>
/// Auth-status response for the REST <c>auth-status</c> endpoint. Mirrors <c>EpicMappingAuthStatus</c>.
/// Serialized camelCase over REST + SignalR -&gt; the frontend sees
/// <c>isAuthenticated/displayName/lastCollectionUtc/gamesDiscovered</c>.
/// </summary>
public class XboxMappingAuthStatus
{
    public bool IsAuthenticated { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? LastCollectionUtc { get; set; }
    public int GamesDiscovered { get; set; }
    /// <summary>
    /// True while a device-code login attempt is still alive - the approval wait AND the catalog
    /// harvest that follows approval, unlike <c>AwaitingSignIn</c> which covers only the wait. False
    /// together with <see cref="IsAuthenticated"/> false is the only pair that means the attempt is
    /// over and did not succeed, so a client that lost the completion event can tell a dead login from
    /// a busy one instead of waiting for a message that will never arrive.
    /// </summary>
    public bool LoginInProgress { get; set; }
    /// <summary>
    /// Approximate expiry of the MSA refresh token: last-auth time + ~90 days.
    /// Slides forward on each auto-renew (startup reconnect and the 12h schedule).
    /// Null when not authenticated.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>
/// Device-code challenge returned by <c>POST auth/login</c>. The frontend renders
/// <see cref="UserCode"/> + <see cref="VerificationUri"/> for the user; completion arrives via SignalR.
/// </summary>
public class XboxDeviceCodeChallenge
{
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public int Interval { get; set; }
    public Guid OperationId { get; set; }
}

/// <summary>
/// Result of a single Xbox catalog refresh pass: CDN patterns newly persisted + downloads re-tagged.
/// </summary>
public class XboxCatalogRefreshResult
{
    public int NewPatterns { get; set; }
    public int Resolved { get; set; }
}
