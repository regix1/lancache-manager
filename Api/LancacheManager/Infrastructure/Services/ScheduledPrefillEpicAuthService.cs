using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Epic OAuth login for scheduled prefill only. Exchanges authorization codes and stores
/// credentials in the isolated scheduled prefill Epic store (no catalog harvest).
/// </summary>
public sealed class ScheduledPrefillEpicAuthService
{
    private readonly EpicApiDirectClient _epicApiClient;
    private readonly IScheduledPrefillEpicAuthStorageService _storage;
    private readonly ILogger<ScheduledPrefillEpicAuthService> _logger;

    public ScheduledPrefillEpicAuthService(
        EpicApiDirectClient epicApiClient,
        IScheduledPrefillEpicAuthStorageService storage,
        ILogger<ScheduledPrefillEpicAuthService> logger)
    {
        _epicApiClient = epicApiClient;
        _storage = storage;
        _logger = logger;
    }

    public string GetAuthorizationUrl()
    {
        var url = _epicApiClient.GetAuthorizationUrl();
        _logger.LogInformation("Generated Epic authorization URL for scheduled prefill login");
        return url;
    }

    public async Task CompleteAuthAsync(string authorizationCode, CancellationToken ct = default)
    {
        var tokens = await _epicApiClient.ExchangeAuthCodeAsync(authorizationCode, ct);

        _storage.SaveAuthData(new EpicAuthData
        {
            RefreshToken = tokens.RefreshToken,
            DisplayName = tokens.DisplayName,
            AccountId = tokens.AccountId,
            LastAuthenticated = DateTime.UtcNow,
            GamesDiscovered = 0
        });

        _logger.LogInformation(
            "Scheduled prefill Epic authentication saved for user: {DisplayName}",
            tokens.DisplayName);
    }
}
