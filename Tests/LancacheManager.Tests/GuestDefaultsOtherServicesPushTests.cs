using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The Steam guest defaults route also saves the Battle.net and Riot defaults when the caller sends them. An
/// open Guest Configuration screen updates each service's panel only from that service's own event, so a save
/// that changes the Battle.net or Riot defaults has to send those events, or the panels keep the old values
/// until a reload.
/// </summary>
public sealed class GuestDefaultsOtherServicesPushTests : IDisposable
{
    private readonly string _root;
    private readonly ApiKeyService _apiKeyService;
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;

    public GuestDefaultsOtherServicesPushTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-guest-defaults-push-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var pathResolver = new AuthCredentialFormatTests.TempDirPathResolver(_root);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "true"
            })
            .Build();
        _apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, _configuration, pathResolver);
        var encryption = new SecureStateEncryptionService(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys"))),
            _apiKeyService,
            NullLogger<SecureStateEncryptionService>.Instance);
        _stateService = new StateService(
            NullLogger<StateService>.Instance,
            pathResolver,
            encryption,
            new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption));
    }

    [Fact]
    public async Task SteamSaveCarryingBattleNetAndRiotDefaultsTellsTheirPanelsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        _stateService.SetBattleNetGuestPrefillEnabledByDefault(false);
        _stateService.SetBattleNetGuestPrefillDurationHours(2);
        _stateService.SetRiotGuestPrefillEnabledByDefault(false);
        _stateService.SetRiotGuestPrefillDurationHours(2);

        var notifier = new ScheduledRunReporterTests.CapturingNotificationService();
        var controller = NewController(database, notifier);

        var saved = await controller.SetGuestPrefillConfigAsync(new GuestPrefillConfigRequest
        {
            EnabledByDefault = false,
            DurationHours = 2,
            BattleNetEnabledByDefault = true,
            BattleNetDurationHours = 3,
            RiotEnabledByDefault = true,
            RiotDurationHours = 1
        });
        Assert.IsType<OkObjectResult>(saved.Result);

        // The save itself landed; only the screens' view of it is in question.
        Assert.True(_stateService.GetBattleNetGuestPrefillEnabledByDefault());
        Assert.Equal(3, _stateService.GetBattleNetGuestPrefillDurationHours());
        Assert.True(_stateService.GetRiotGuestPrefillEnabledByDefault());
        Assert.Equal(1, _stateService.GetRiotGuestPrefillDurationHours());

        var stale = new List<string>();
        foreach (var (eventName, panel, enabled, hours) in new[]
                 {
                     (SignalREvents.BattleNetGuestPrefillConfigChanged, "Battle.net", true, 3),
                     (SignalREvents.RiotGuestPrefillConfigChanged, "Riot", true, 1)
                 })
        {
            var last = notifier.Events.LastOrDefault(push => push.EventName == eventName);
            if (last is null)
            {
                stale.Add($"{panel}: no {eventName} was sent, so an open {panel} panel keeps showing off / 2 h");
                continue;
            }

            var sent = JsonSerializer.SerializeToElement(last.Payload, JsonSerializerOptions.Web);
            var pushedEnabled = sent.GetProperty("enabledByDefault").GetBoolean();
            var pushedHours = sent.GetProperty("durationHours").GetInt32();
            if (pushedEnabled != enabled || pushedHours != hours)
            {
                stale.Add($"{panel}: {eventName} carried {pushedEnabled} / {pushedHours} h, the server holds {enabled} / {hours} h");
            }
        }

        Assert.True(stale.Count == 0, string.Join("; ", stale));
    }

    private AuthController NewController(TestDatabase database, ScheduledRunReporterTests.CapturingNotificationService notifier)
    {
        var controller = new AuthController(
            new SessionService(
                database.Factory,
                _apiKeyService,
                NullLogger<SessionService>.Instance,
                _stateService,
                notifier,
                _configuration),
            NullLogger<AuthController>.Instance,
            database.Factory,
            _stateService,
            notifier,
            _apiKeyService,
            new PasswordHasher<UserAccount>(),
            new AccountLockout(NullLogger<AccountLockout>.Instance),
            new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not worth failing a test over.
        }
    }
}
