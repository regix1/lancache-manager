using System.Text.Json.Serialization;
using LancacheManager.Models;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// Centralized endpoints / scopes / client id for the manager-side daemon-free Xbox MSA device-code
/// login. Ported verbatim from the prefill daemon's <c>Settings/AppConfig.cs</c> (PROVEN live against a
/// real account). Kept in one consts class - NOT hardcoded deep in method bodies - because Microsoft can
/// revoke the legacy device-code client id, in which case only this file changes.
/// </summary>
internal static class XboxAuthConstants
{
    /// <summary>MSA client id used for the device-code flow (legacy, device-code capable - PROVEN).</summary>
    public const string ClientId = "0000000048183522";

    /// <summary>
    /// OAuth scope requested for the device-code grant (yields the MBI_SSL RpsTicket the XBL chain
    /// consumes). This legacy <c>login.live.com</c> flow already returns a refresh token with this scope
    /// alone (proven by the Xbox prefill daemon), which keeps the integration signed in after a restart.
    /// Do NOT append the modern <c>offline_access</c> scope here: the legacy endpoint rejects the combined
    /// scope for this client id and the device-code poll fails with <c>invalid_grant</c>.
    /// </summary>
    public const string AuthScope = "service::user.auth.xboxlive.com::MBI_SSL";

    public const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    public const string TokenUrl = "https://login.live.com/oauth20_token.srf";
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    public const string UserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    public const string DeviceAuthUrl = "https://device.auth.xboxlive.com/device/authenticate";
    public const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

    /// <summary>XSTS relying party for titlehub enumeration. User token only, unsigned.</summary>
    public const string TitleHubRelyingParty = "http://xboxlive.com";

    /// <summary>XSTS relying party for the package service. Device-bearing + signed (else GetBasePackage 403s).</summary>
    public const string UpdateRelyingParty = "http://update.xboxlive.com";

    public const string TitleHubBaseUrl = "https://titlehub.xboxlive.com";
    public const string DisplayCatalogBaseUrl = "https://displaycatalog.mp.microsoft.com";
    public const string PackageServiceBaseUrl = "https://packagespc.xboxlive.com/GetBasePackage/";
    public const string ProfileBaseUrl = "https://profile.xboxlive.com";

    public const string DefaultUserAgent = "Microsoft.Xbox.GameStreaming/10.0";
}

/// <summary>Result of one daemon-free Xbox catalog harvest (titlehub titles + packagespc CDN fragments).</summary>
internal sealed class XboxHarvestResult
{
    public List<CdnInfo> CdnInfos { get; init; } = new();
    public string? DisplayName { get; init; }
    public string? Xuid { get; init; }
}

// --- MSA device-code wire models (login.live.com) ---

/// <summary>Response from the MSA device-code request (<c>oauth20_connect.srf</c>).</summary>
internal sealed class XboxDeviceCodeResponse
{
    [JsonPropertyName("user_code")]
    public string? UserCode { get; set; }

    [JsonPropertyName("device_code")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("verification_uri")]
    public string? VerificationUri { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

/// <summary>
/// Response from the MSA token endpoint (<c>oauth20_token.srf</c>) while polling the device-code grant
/// or refreshing. Carries the <c>authorization_pending</c> / <c>slow_down</c> error codes during polling.
/// </summary>
internal sealed class XboxMsaTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

// --- XBL / XSTS wire models (PascalCase) ---

/// <summary>
/// Response from <c>user.auth.xboxlive.com/user/authenticate</c> and
/// <c>device.auth.xboxlive.com/device/authenticate</c>. Both return a JWT in <c>Token</c>.
/// </summary>
internal sealed class XboxXblAuthResponse
{
    [JsonPropertyName("Token")]
    public string? Token { get; set; }

    [JsonPropertyName("IssueInstant")]
    public DateTime IssueInstant { get; set; }

    [JsonPropertyName("NotAfter")]
    public DateTime NotAfter { get; set; }
}

/// <summary>Response from <c>xsts.auth.xboxlive.com/xsts/authorize</c>.</summary>
internal sealed class XboxXstsTokenResponse
{
    [JsonPropertyName("Token")]
    public string? Token { get; set; }

    [JsonPropertyName("IssueInstant")]
    public DateTime IssueInstant { get; set; }

    [JsonPropertyName("NotAfter")]
    public DateTime NotAfter { get; set; }

    [JsonPropertyName("DisplayClaims")]
    public XboxXstsDisplayClaims? DisplayClaims { get; set; }
}

internal sealed class XboxXstsDisplayClaims
{
    [JsonPropertyName("xui")]
    public List<XboxXstsUserInfo>? Xui { get; set; }
}

internal sealed class XboxXstsUserInfo
{
    /// <summary>User hash, used in the <c>XBL3.0 x={uhs};{token}</c> authorization header.</summary>
    [JsonPropertyName("uhs")]
    public string? Uhs { get; set; }

    /// <summary>Xbox user id (xuid), used for titlehub enumeration.</summary>
    [JsonPropertyName("xid")]
    public string? Xid { get; set; }
}

/// <summary>Request body for <c>user.auth.xboxlive.com/user/authenticate</c>.</summary>
internal sealed class XboxXblUserAuthRequest
{
    [JsonPropertyName("Properties")]
    public XboxXblUserAuthProperties? Properties { get; set; }

    [JsonPropertyName("RelyingParty")]
    public string RelyingParty { get; set; } = "http://auth.xboxlive.com";

    [JsonPropertyName("TokenType")]
    public string TokenType { get; set; } = "JWT";
}

internal sealed class XboxXblUserAuthProperties
{
    [JsonPropertyName("AuthMethod")]
    public string AuthMethod { get; set; } = "RPS";

    [JsonPropertyName("SiteName")]
    public string SiteName { get; set; } = "user.auth.xboxlive.com";

    [JsonPropertyName("RpsTicket")]
    public string? RpsTicket { get; set; }
}

/// <summary>Request body for <c>device.auth.xboxlive.com/device/authenticate</c> (signed, ProofOfPossession).</summary>
internal sealed class XboxXblDeviceAuthRequest
{
    [JsonPropertyName("RelyingParty")]
    public string RelyingParty { get; set; } = "http://auth.xboxlive.com";

    [JsonPropertyName("TokenType")]
    public string TokenType { get; set; } = "JWT";

    [JsonPropertyName("Properties")]
    public XboxXblDeviceAuthProperties? Properties { get; set; }
}

internal sealed class XboxXblDeviceAuthProperties
{
    [JsonPropertyName("AuthMethod")]
    public string AuthMethod { get; set; } = "ProofOfPossession";

    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("DeviceType")]
    public string DeviceType { get; set; } = "Win32";

    [JsonPropertyName("Version")]
    public string Version { get; set; } = "10.0.19041.0";

    [JsonPropertyName("ProofKey")]
    public XboxProofKeyJwk? ProofKey { get; set; }
}

/// <summary>The P-256 public key (JWK) embedded as the device's proof key.</summary>
internal sealed class XboxProofKeyJwk
{
    [JsonPropertyName("crv")]
    public string Crv { get; set; } = "P-256";

    [JsonPropertyName("alg")]
    public string Alg { get; set; } = "ES256";

    [JsonPropertyName("use")]
    public string Use { get; set; } = "sig";

    [JsonPropertyName("kty")]
    public string Kty { get; set; } = "EC";

    [JsonPropertyName("x")]
    public string? X { get; set; }

    [JsonPropertyName("y")]
    public string? Y { get; set; }
}

/// <summary>Request body for <c>xsts.auth.xboxlive.com/xsts/authorize</c>.</summary>
internal sealed class XboxXstsAuthRequest
{
    [JsonPropertyName("Properties")]
    public XboxXstsAuthProperties? Properties { get; set; }

    [JsonPropertyName("RelyingParty")]
    public string? RelyingParty { get; set; }

    [JsonPropertyName("TokenType")]
    public string TokenType { get; set; } = "JWT";
}

internal sealed class XboxXstsAuthProperties
{
    [JsonPropertyName("SandboxId")]
    public string SandboxId { get; set; } = "RETAIL";

    [JsonPropertyName("UserTokens")]
    public string[]? UserTokens { get; set; }

    /// <summary>Device token. OMITTED for the titlehub audience (when null); mandatory for the update audience.</summary>
    [JsonPropertyName("DeviceToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceToken { get; set; }
}

// --- titlehub wire models ---

internal sealed class XboxTitleHubResponse
{
    [JsonPropertyName("titles")]
    public List<XboxTitleHubTitle>? Titles { get; set; }
}

internal sealed class XboxTitleHubTitle
{
    [JsonPropertyName("titleId")]
    public string? TitleId { get; set; }

    /// <summary>Package family name. Null for non-Store titles (e.g. Steam/Epic games played on PC).</summary>
    [JsonPropertyName("pfn")]
    public string? Pfn { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Store product id (big id). The cache key used to resolve a downloadable package.</summary>
    [JsonPropertyName("productId")]
    public string? ProductId { get; set; }
}

// --- DisplayCatalog wire models (anonymous; ProductId -> ContentId) ---

internal sealed class XboxDisplayCatalogResponse
{
    [JsonPropertyName("Products")]
    public List<XboxDisplayCatalogProduct>? Products { get; set; }
}

internal sealed class XboxDisplayCatalogProduct
{
    [JsonPropertyName("ProductId")]
    public string? ProductId { get; set; }

    [JsonPropertyName("DisplaySkuAvailabilities")]
    public List<XboxDisplaySkuAvailability>? DisplaySkuAvailabilities { get; set; }
}

internal sealed class XboxDisplaySkuAvailability
{
    [JsonPropertyName("Sku")]
    public XboxDisplayCatalogSku? Sku { get; set; }
}

internal sealed class XboxDisplayCatalogSku
{
    [JsonPropertyName("Properties")]
    public XboxDisplayCatalogSkuProperties? Properties { get; set; }
}

internal sealed class XboxDisplayCatalogSkuProperties
{
    [JsonPropertyName("Packages")]
    public List<XboxDisplayCatalogPackage>? Packages { get; set; }
}

internal sealed class XboxDisplayCatalogPackage
{
    [JsonPropertyName("ContentId")]
    public string? ContentId { get; set; }

    [JsonPropertyName("PackageFormat")]
    public string? PackageFormat { get; set; }
}

// --- packagespc GetBasePackage wire models (signed; ContentId -> PackageFiles) ---

internal sealed class XboxGetBasePackageResponse
{
    [JsonPropertyName("PackageFound")]
    public bool PackageFound { get; set; }

    [JsonPropertyName("ContentId")]
    public string? ContentId { get; set; }

    [JsonPropertyName("Version")]
    public string? Version { get; set; }

    [JsonPropertyName("PackageFiles")]
    public List<XboxPackageFile>? PackageFiles { get; set; }
}

internal sealed class XboxPackageFile
{
    [JsonPropertyName("FileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("FileSize")]
    public ulong FileSize { get; set; }

    [JsonPropertyName("RelativeUrl")]
    public string? RelativeUrl { get; set; }

    [JsonPropertyName("CdnRootPaths")]
    public string[]? CdnRootPaths { get; set; }
}

// --- profile (gamertag) wire models ---

internal sealed class XboxProfileResponse
{
    [JsonPropertyName("profileUsers")]
    public List<XboxProfileUser>? ProfileUsers { get; set; }
}

internal sealed class XboxProfileUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("settings")]
    public List<XboxProfileSetting>? Settings { get; set; }
}

internal sealed class XboxProfileSetting
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
