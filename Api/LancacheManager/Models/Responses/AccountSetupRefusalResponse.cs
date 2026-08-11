namespace LancacheManager.Models;

/// <summary>
/// Refusal body for creating the account that owns the installation. <see cref="StageKey"/> is the
/// i18n key the browser renders; <see cref="Error"/> is the English sentence a client that does not
/// localize falls back to, so both travel on every refusal. [88][88d]
/// </summary>
public class AccountSetupRefusalResponse
{
    /// <summary>No API key was sent, or the one that was sent does not match this installation.</summary>
    public const string ApiKeyRequired = "errors.accountSetup.apiKeyRequired";

    /// <summary>The window the application opens at startup has already passed.</summary>
    public const string ClaimWindowClosed = "errors.accountSetup.claimWindowClosed";

    /// <summary>
    /// Somebody already owns this installation. The same refusal whether the count read it or the
    /// insert ran into it, so both paths name it with this key rather than two that drift apart.
    /// </summary>
    public const string AccountExists = "errors.accountSetup.accountExists";

    public string StageKey { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}
