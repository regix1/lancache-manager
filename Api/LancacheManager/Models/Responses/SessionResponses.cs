namespace LancacheManager.Models;

/// <summary>
/// Paginated active sessions plus the full (unpaginated) revoked/expired history, returned by
/// <c>GET /api/sessions</c>.
/// </summary>
public class SessionListResponse
{
    public List<SessionDto> Sessions { get; set; } = new();
    public int Count { get; set; }
    public int AdminCount { get; set; }
    public int UserCount { get; set; }
    public int GuestCount { get; set; }
    public required SessionListPage Pagination { get; set; }
    public List<SessionDto> HistorySessions { get; set; } = new();
}

/// <summary>Pagination block nested inside <see cref="SessionListResponse"/>.</summary>
public class SessionListPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>Result of the bulk guest-preferences reset (<c>POST /api/sessions/bulk/reset-to-defaults</c>).</summary>
public class SessionResetResponse
{
    public bool Success { get; set; }
    public int AffectedCount { get; set; }
}

/// <summary>Result of clearing every guest session (<c>DELETE /api/sessions/bulk/clear-guests</c>).</summary>
public class SessionClearGuestsResponse
{
    public bool Success { get; set; }
    public int ClearedCount { get; set; }
}

/// <summary>
/// Result of <c>POST /api/sessions/me/client-info</c>: the resolved public IP plus whatever the GeoIP
/// lookup found for it.
/// </summary>
public class SessionClientInfoResponse
{
    public bool Success { get; set; }

    /// <summary>The address the client info was resolved against. Null when neither the connection's remote address nor the server's own public IP could be determined.</summary>
    public string? PublicIp { get; set; }

    /// <summary>Null when <see cref="PublicIp"/> is null or the GeoIP lookup found nothing for it.</summary>
    public string? CountryCode { get; set; }

    /// <summary>Null when <see cref="PublicIp"/> is null or the GeoIP lookup found nothing for it.</summary>
    public string? Country { get; set; }

    /// <summary>Null when <see cref="PublicIp"/> is null or the GeoIP lookup found nothing for it.</summary>
    public string? Region { get; set; }

    /// <summary>Null when <see cref="PublicIp"/> is null or the GeoIP lookup found nothing for it.</summary>
    public string? City { get; set; }

    /// <summary>Browser-reported timezone when the client supplied one, otherwise the GeoIP timezone. Null when neither is available.</summary>
    public string? Timezone { get; set; }

    /// <summary>Null when <see cref="PublicIp"/> is null or the GeoIP lookup found nothing for it.</summary>
    public string? Isp { get; set; }
}
