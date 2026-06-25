using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// Lightweight HTTP client that calls the public Microsoft Store DisplayCatalog API directly
/// from lancache-manager (no auth, no Docker). Used by the Xbox mapping path to fetch a game's
/// banner art by its ProductId ("bigId", e.g. "9NBLGGH537BL") at mapping time.
///
/// The art is stored keyed by the GameName slug via the name-keyed banner path (Xbox rides the
/// named-game model - <c>Service='xbox'</c>, <c>GameAppId=NULL</c>), so this client only needs to
/// resolve a single image URL per ProductId. Empty <c>Products[]</c> returns null so the caller
/// can fall back to the service-icon placeholder rather than showing a wrong image.
/// </summary>
public class XboxApiDirectClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<XboxApiDirectClient> _logger;

    // Public, unauthenticated DisplayCatalog endpoint. fieldsTemplate=Details returns the
    // LocalizedProperties[].Images[] array that carries the banner art.
    private const string DisplayCatalogUrlFormat =
        "https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds={0}&market=US&languages=en-us&fieldsTemplate=Details";

    // Resize the protocol-relative DisplayCatalog image to a Steam-quality banner width. GET only.
    private const string ImageResizeQuery = "?w=460&q=90&format=jpg";

    // ImagePurpose values that carry a wide, banner-shaped hero image. The live catalog has used
    // both spellings across products, so we accept either and prefer them in this order. We fall
    // back to any image rather than returning nothing when none of the preferred purposes exist.
    private static readonly string[] _preferredImagePurposes =
    {
        "SuperHeroArt",
        "Hero",
        "BrandedKeyArt",
        "TitledHeroArt",
        "FeaturePromotionalSquareArt",
        "Poster",
        "BoxArt",
        "Logo"
    };

    public XboxApiDirectClient(HttpClient httpClient, ILogger<XboxApiDirectClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Resolves a banner art URL for the given Microsoft Store ProductId via DisplayCatalog.
    /// Returns an absolute, resized https URL, or null when the catalog has no product / no image
    /// (caller should then use the service-icon placeholder, never a wrong image).
    /// </summary>
    public async Task<string?> GetBannerImageUrlAsync(string productId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
        {
            return null;
        }

        DisplayCatalogResponse? catalog;
        try
        {
            var url = string.Format(DisplayCatalogUrlFormat, Uri.EscapeDataString(productId));
            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "DisplayCatalog lookup for Xbox ProductId {ProductId} returned {StatusCode}",
                    productId, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            catalog = JsonSerializer.Deserialize<DisplayCatalogResponse>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query DisplayCatalog for Xbox ProductId {ProductId}", productId);
            return null;
        }

        var product = catalog?.Products?.FirstOrDefault();
        if (product == null)
        {
            // Empty Products[] - graceful skip; caller shows the service-icon placeholder.
            _logger.LogInformation("DisplayCatalog returned no products for Xbox ProductId {ProductId}", productId);
            return null;
        }

        var rawImageUri = SelectBestImageUri(product);
        if (string.IsNullOrWhiteSpace(rawImageUri))
        {
            _logger.LogInformation("No usable image in DisplayCatalog for Xbox ProductId {ProductId}", productId);
            return null;
        }

        // DisplayCatalog image URIs are protocol-relative ("//store-images..."). Prefix https and
        // append the resize query for Steam-quality banners.
        var absolute = rawImageUri.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + rawImageUri
            : rawImageUri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? rawImageUri
                : "https://" + rawImageUri.TrimStart('/');

        return absolute + ImageResizeQuery;
    }

    /// <summary>
    /// Picks the most banner-shaped image from a product, accepting either ImagePurpose spelling
    /// the live catalog uses and falling back to any image before giving up.
    /// </summary>
    private static string? SelectBestImageUri(CatalogProduct product)
    {
        var images = product.LocalizedProperties?
            .SelectMany(lp => lp.Images ?? Enumerable.Empty<CatalogImage>())
            .Where(img => !string.IsNullOrWhiteSpace(img.Uri))
            .ToList();

        if (images == null || images.Count == 0)
        {
            return null;
        }

        foreach (var purpose in _preferredImagePurposes)
        {
            var match = images.FirstOrDefault(img =>
                string.Equals(img.ImagePurpose, purpose, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match.Uri;
            }
        }

        // No preferred purpose matched - return the widest image as a last resort.
        return images
            .OrderByDescending(img => img.Width ?? 0)
            .First()
            .Uri;
    }

    // --- DisplayCatalog response DTOs (only the fields we read) ---

    private sealed class DisplayCatalogResponse
    {
        [JsonPropertyName("Products")]
        public List<CatalogProduct>? Products { get; set; }
    }

    private sealed class CatalogProduct
    {
        [JsonPropertyName("ProductId")]
        public string? ProductId { get; set; }

        [JsonPropertyName("LocalizedProperties")]
        public List<CatalogLocalizedProperties>? LocalizedProperties { get; set; }
    }

    private sealed class CatalogLocalizedProperties
    {
        [JsonPropertyName("ProductTitle")]
        public string? ProductTitle { get; set; }

        [JsonPropertyName("Images")]
        public List<CatalogImage>? Images { get; set; }
    }

    private sealed class CatalogImage
    {
        [JsonPropertyName("Uri")]
        public string? Uri { get; set; }

        [JsonPropertyName("ImagePurpose")]
        public string? ImagePurpose { get; set; }

        [JsonPropertyName("Height")]
        public int? Height { get; set; }

        [JsonPropertyName("Width")]
        public int? Width { get; set; }
    }
}
