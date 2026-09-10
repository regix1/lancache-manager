namespace LancacheManager.Core.Services;

public sealed record GeoIpLookup(
    string? CountryCode,
    string? CountryName,
    string? RegionName,
    string? City,
    string? Timezone,
    string? IspName);
