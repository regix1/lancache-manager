using System.Text.Json.Serialization;

namespace LancacheManager.Core.Services.EpicMapping;

#region Epic API Response Models

/// <summary>
/// OAuth token response from Epic's account service.
/// </summary>
public class EpicOAuthTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime RefreshExpiresAt { get; set; }
}

/// <summary>
/// Raw OAuth token response from Epic's API.
/// </summary>
internal class EpicTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAtStr { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("refresh_expires")]
    public int RefreshExpiresIn { get; set; }

    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("app")]
    public string? App { get; set; }
}

internal sealed class EpicExchangeCodeResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

/// <summary>
/// Asset entry from the Epic launcher assets API.
/// </summary>
internal class EpicAsset
{
    [JsonPropertyName("appName")]
    public string AppName { get; set; } = string.Empty;

    [JsonPropertyName("buildVersion")]
    public string? BuildVersion { get; set; }

    [JsonPropertyName("catalogItemId")]
    public string CatalogItemId { get; set; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;
}

/// <summary>
/// Catalog item from Epic's catalog metadata API.
/// </summary>
internal class EpicCatalogItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("keyImages")]
    public List<EpicKeyImage>? KeyImages { get; set; }
}

/// <summary>
/// Key image entry from Epic's catalog metadata. Public because it also backs
/// <see cref="LancacheManager.Models.OwnedGame.KeyImages"/>, which crosses the socket boundary.
/// </summary>
public class EpicKeyImage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

/// <summary>
/// Response from Epic's manifest API (assets v2 endpoint).
/// </summary>
internal class EpicManifestResponse
{
    [JsonPropertyName("elements")]
    public List<EpicManifestElement>? Elements { get; set; }
}

/// <summary>
/// Element entry from the manifest response containing manifest URIs.
/// </summary>
internal class EpicManifestElement
{
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("manifests")]
    public List<EpicManifestEntry>? Manifests { get; set; }
}

/// <summary>
/// Manifest entry containing the URI to the .manifest file on the CDN.
/// </summary>
internal class EpicManifestEntry
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

/// <summary>
/// Top-level response from Epic's free games promotions API.
/// </summary>
internal class EpicFreeGamesResponse
{
    [JsonPropertyName("data")]
    public EpicFreeGamesData? Data { get; set; }
}

/// <summary>
/// Data wrapper in the free games promotions response.
/// </summary>
internal class EpicFreeGamesData
{
    [JsonPropertyName("Catalog")]
    public EpicFreeGamesCatalog? Catalog { get; set; }
}

/// <summary>
/// Catalog wrapper in the free games promotions response.
/// </summary>
internal class EpicFreeGamesCatalog
{
    [JsonPropertyName("searchStore")]
    public EpicFreeGamesSearchStore? SearchStore { get; set; }
}

/// <summary>
/// Search store wrapper containing the elements array.
/// </summary>
internal class EpicFreeGamesSearchStore
{
    [JsonPropertyName("elements")]
    public List<EpicFreeGamesElement>? Elements { get; set; }
}

/// <summary>
/// Individual game element from the free games promotions API.
/// </summary>
internal class EpicFreeGamesElement
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("keyImages")]
    public List<EpicKeyImage>? KeyImages { get; set; }

    [JsonPropertyName("promotions")]
    public EpicFreeGamesPromotions? Promotions { get; set; }
}

/// <summary>
/// Promotions data for a free games element.
/// </summary>
internal class EpicFreeGamesPromotions
{
    [JsonPropertyName("promotionalOffers")]
    public List<EpicPromotionalOfferGroup>? PromotionalOffers { get; set; }
}

/// <summary>
/// Group of promotional offers containing individual offer entries.
/// </summary>
internal class EpicPromotionalOfferGroup
{
    [JsonPropertyName("promotionalOffers")]
    public List<EpicPromotionalOffer>? PromotionalOffers { get; set; }
}

/// <summary>
/// Individual promotional offer with start and end dates.
/// </summary>
internal class EpicPromotionalOffer
{
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public string? EndDate { get; set; }
}

#endregion
