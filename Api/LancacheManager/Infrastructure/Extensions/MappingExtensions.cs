using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using static LancacheManager.Security.DeviceAuthService;
using static LancacheManager.Security.GuestSessionService;

namespace LancacheManager.Infrastructure.Extensions;

/// <summary>
/// Extension methods for mapping entities to DTOs
/// </summary>
public static class MappingExtensions
{
    // ===== Session Mappings =====

    /// <summary>
    /// Maps an authenticated device to a SessionDto
    /// </summary>
    public static SessionDto ToSessionDto(this DeviceInfo device) => new()
    {
        Id = device.DeviceId,
        DeviceId = device.DeviceId,
        DeviceName = device.DeviceName,
        IpAddress = device.IpAddress,
        LocalIp = device.LocalIp,
        Hostname = device.Hostname,
        OperatingSystem = device.OperatingSystem,
        Browser = device.Browser,
        CreatedAt = device.RegisteredAt,
        LastSeenAt = device.LastSeenAt,
        ExpiresAt = device.ExpiresAt,
        IsExpired = device.IsExpired,
        IsRevoked = false,
        RevokedAt = null,
        RevokedBy = null,
        Type = "authenticated",
        PrefillEnabled = true,
        PrefillExpiresAt = null,
        IsPrefillExpired = false
    };

    /// <summary>
    /// Maps a guest session to a SessionDto
    /// </summary>
    public static SessionDto ToSessionDto(this GuestSessionInfo session) => new()
    {
        Id = session.DeviceId,
        DeviceId = session.DeviceId,
        DeviceName = session.DeviceName,
        IpAddress = session.IpAddress,
        LocalIp = null,
        Hostname = null,
        OperatingSystem = session.OperatingSystem,
        Browser = session.Browser,
        CreatedAt = session.CreatedAt,
        LastSeenAt = session.LastSeenAt,
        ExpiresAt = session.ExpiresAt,
        IsExpired = session.IsExpired,
        IsRevoked = session.IsRevoked,
        RevokedAt = session.RevokedAt,
        RevokedBy = session.RevokedBy,
        Type = "guest",
        PrefillEnabled = session.PrefillEnabled,
        PrefillExpiresAt = session.PrefillExpiresAt,
        IsPrefillExpired = session.IsPrefillExpired
    };

    /// <summary>
    /// Maps a collection of authenticated devices to SessionDtos
    /// </summary>
    public static List<SessionDto> ToSessionDtos(this IEnumerable<DeviceInfo> devices) =>
        devices.Select(d => d.ToSessionDto()).ToList();

    /// <summary>
    /// Maps a collection of guest sessions to SessionDtos
    /// </summary>
    public static List<SessionDto> ToSessionDtos(this IEnumerable<GuestSessionInfo> sessions) =>
        sessions.Select(s => s.ToSessionDto()).ToList();

    // ===== ClientGroup Mappings =====

    /// <summary>
    /// Maps a ClientGroup entity to a ClientGroupDto
    /// </summary>
    public static ClientGroupDto ToDto(this ClientGroup group) => new()
    {
        Id = group.Id,
        Nickname = group.Nickname,
        Description = group.Description,
        CreatedAtUtc = group.CreatedAtUtc,
        UpdatedAtUtc = group.UpdatedAtUtc,
        MemberIps = group.Members.Select(m => m.ClientIp).OrderBy(ip => ip).ToList()
    };

    /// <summary>
    /// Maps a collection of ClientGroups to DTOs
    /// </summary>
    public static List<ClientGroupDto> ToDtos(this IEnumerable<ClientGroup> groups) =>
        groups.Select(g => g.ToDto()).ToList();
}
