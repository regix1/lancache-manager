using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Maps an Xbox / Microsoft Store CDN URL fragment to a game.
/// Xbox content is delivered through *.dl.delivery.mp.microsoft.com (lancache-tagged 'wsus')
/// as opaque /filestreamingservice/files/&lt;GUID&gt; objects with no product id in the URL.
/// This table lets the resolver match a cached 'wsus' download back to its Xbox title by the
/// per-file path fragment the authenticated daemon contributed.
/// </summary>
public class XboxCdnPattern
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// The Xbox / Microsoft Store ProductId (links to XboxGameMapping.ProductId).
    /// </summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>
    /// The game title at time of discovery.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The per-file CDN URL path fragment (e.g. "/filestreamingservice/files/&lt;GUID&gt;").
    /// The volatile query (P1..P4 / cacheHostOrigin) is stripped before storage.
    /// </summary>
    public string UrlFragment { get; set; } = string.Empty;

    /// <summary>
    /// The CDN hostname (e.g., "assets1.xboxlive.com", "*.dl.delivery.mp.microsoft.com").
    /// </summary>
    public string CdnHost { get; set; } = string.Empty;

    /// <summary>
    /// When this CDN fragment was first discovered.
    /// </summary>
    public DateTime DiscoveredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this fragment was last confirmed still valid.
    /// </summary>
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}
