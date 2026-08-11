namespace LancacheManager.Models;

/// <summary>
/// Body of the 429 a throttled route answers with once its window is full. The limiter otherwise
/// completes the request with no content at all, so a caller that reads the response as JSON gets a
/// parse error where it should be reading the reason.
///
/// Same two fields as <see cref="OperationConflictResponse"/> carries for a 409: <see cref="StageKey"/>
/// is the i18n key the client renders, <see cref="Error"/> is the English fallback for a client that
/// does not localize. Serialized camelCase like every other response body.
/// </summary>
public sealed class RateLimitExceededResponse
{
    /// <summary>i18n key for the localized reason.</summary>
    public string StageKey { get; init; } = string.Empty;

    /// <summary>English fallback message for clients that do not consume <see cref="StageKey"/>.</summary>
    public string Error { get; init; } = string.Empty;
}
