using System.Security.Cryptography;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

public class SessionService
{
    private readonly AppDbContext _dbContext;
    private readonly ApiKeyService _apiKeyService;
    private readonly ILogger<SessionService> _logger;
    private readonly IConfiguration _configuration;

    private const string CookieName = "LancacheManager.Session";
    private const int AdminSessionDurationHours = 720; // 30 days
    private const int DefaultGuestDurationHours = 6;

    public SessionService(
        AppDbContext dbContext,
        ApiKeyService apiKeyService,
        ILogger<SessionService> logger,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _apiKeyService = apiKeyService;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<(string RawToken, UserSession Session)?> CreateAdminSessionAsync(string apiKey, HttpContext httpContext)
    {
        if (!_apiKeyService.ValidateApiKey(apiKey))
        {
            return null;
        }

        var (rawToken, tokenHash) = GenerateSessionToken();

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = tokenHash,
            SessionType = "admin",
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(AdminSessionDurationHours),
            LastSeenAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        _dbContext.UserSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created admin session {SessionId} for IP {IP}", session.Id, session.IpAddress);
        return (rawToken, session);
    }

    public async Task<(string RawToken, UserSession Session)?> CreateGuestSessionAsync(HttpContext httpContext)
    {
        if (!IsGuestAccessEnabled())
        {
            return null;
        }

        var (rawToken, tokenHash) = GenerateSessionToken();
        var durationHours = GetGuestDurationHours();

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = tokenHash,
            SessionType = "guest",
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours),
            LastSeenAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        _dbContext.UserSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created guest session {SessionId} for IP {IP}, expires in {Hours}h",
            session.Id, session.IpAddress, durationHours);
        return (rawToken, session);
    }

    public async Task<UserSession?> ValidateSessionAsync(string rawToken)
    {
        var tokenHash = HashToken(rawToken);

        var session = await _dbContext.UserSessions
            .FirstOrDefaultAsync(s => s.SessionTokenHash == tokenHash);

        if (session == null)
            return null;

        if (session.IsRevoked)
            return null;

        if (session.ExpiresAtUtc <= DateTime.UtcNow)
            return null;

        return session;
    }

    public async Task<bool> RevokeSessionAsync(Guid sessionId)
    {
        var session = await _dbContext.UserSessions.FindAsync(sessionId);
        if (session == null)
            return false;

        session.IsRevoked = true;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Revoked session {SessionId}", sessionId);
        return true;
    }

    public async Task<int> RevokeAllGuestSessionsAsync()
    {
        var now = DateTime.UtcNow;
        var count = await _dbContext.UserSessions
            .Where(s => s.SessionType == "guest" && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsRevoked, true)
                .SetProperty(x => x.RevokedAtUtc, now));

        _logger.LogInformation("Revoked {Count} guest sessions", count);
        return count;
    }

    public async Task<List<UserSession>> GetActiveSessionsAsync()
    {
        var now = DateTime.UtcNow;
        return await _dbContext.UserSessions
            .Where(s => !s.IsRevoked && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .ToListAsync();
    }

    public async Task UpdateLastSeenAsync(UserSession session)
    {
        if ((DateTime.UtcNow - session.LastSeenAtUtc).TotalSeconds < 60)
            return;

        session.LastSeenAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    public async Task<int> CleanupExpiredSessionsAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var count = await _dbContext.UserSessions
            .Where(s => s.ExpiresAtUtc < cutoff || (s.IsRevoked && s.RevokedAtUtc < cutoff))
            .ExecuteDeleteAsync();

        if (count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired/revoked sessions", count);
        }
        return count;
    }

    public void SetSessionCookie(HttpContext httpContext, string rawToken, DateTime expiresAtUtc)
    {
        httpContext.Response.Cookies.Append(CookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = new DateTimeOffset(expiresAtUtc)
        });
    }

    public void ClearSessionCookie(HttpContext httpContext)
    {
        httpContext.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
    }

    public static string? GetSessionTokenFromCookie(HttpContext httpContext)
    {
        return httpContext.Request.Cookies[CookieName];
    }

    public bool IsGuestAccessEnabled()
    {
        var locked = _configuration.GetValue<bool>("Security:GuestModeLocked");
        return !locked;
    }

    public int GetGuestDurationHours()
    {
        return _configuration.GetValue("Security:GuestDurationHours", DefaultGuestDurationHours);
    }

    private static (string RawToken, string TokenHash) GenerateSessionToken()
    {
        var tokenBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(tokenBytes);

        var rawToken = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        var tokenHash = HashToken(rawToken);
        return (rawToken, tokenHash);
    }

    private static string HashToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
