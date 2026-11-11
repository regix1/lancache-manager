using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
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
/// Two admins saving the same guest prefill defaults at once. Every open Guest Configuration screen
/// takes the pushed values as they arrive, so the last push it receives must carry the value the
/// server holds once both saves are done, whichever save started first.
/// </summary>
public sealed class GuestPrefillConfigOrderTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly string _root;
    private readonly ApiKeyService _apiKeyService;
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;

    public GuestPrefillConfigOrderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-guest-prefill-order-{Guid.NewGuid():N}");
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

    /// <summary>
    /// Admin A turns Steam guest prefill on and its grant pass is still running when admin B turns it
    /// off. The screens must end on off, the value stored last.
    /// </summary>
    [Fact]
    public async Task SteamSaveOverlappingAGrantPass_LastPushCarriesTheStoredValue()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedEligibleGuestAsync(database);
        _stateService.SetGuestPrefillEnabledByDefault(false);

        var notifier = new ScheduledRunReporterTests.CapturingNotificationService(
            SignalREvents.GuestPrefillPermissionChanged);
        var controller = NewController(database, notifier);

        var first = controller.SetGuestPrefillConfigAsync(
            new GuestPrefillConfigRequest { EnabledByDefault = true, DurationHours = 1 });
        await WaitUntilHeldAsync(notifier.WhenBlockedSendBeginsAsync(), first);

        var second = controller.SetGuestPrefillConfigAsync(
            new GuestPrefillConfigRequest { EnabledByDefault = false, DurationHours = 3 });
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1)));

        notifier.ReleaseBlockedSend();
        await Task.WhenAll(first, second).WaitAsync(Bound);

        var stored = Assert.IsType<GuestPrefillConfigResponse>(
            Assert.IsType<OkObjectResult>(controller.GetGuestPrefillConfig().Result).Value);
        Assert.False(stored.EnabledByDefault);
        Assert.Equal(3, stored.DurationHours);

        var lastPush = notifier.Events.Last(push => push.EventName == SignalREvents.GuestPrefillConfigChanged);
        Assert.Equal(stored.EnabledByDefault,
            DefaultGuestPreferencesTests.ReadPayload<bool>(lastPush.Payload, "enabledByDefault"));
        Assert.Equal(stored.DurationHours,
            DefaultGuestPreferencesTests.ReadPayload<int>(lastPush.Payload, "durationHours"));
    }

    /// <summary>
    /// The Epic route already builds its push from the store, but a push built before the other
    /// admin's save can still reach the screens after that save's push. The screens must end on off.
    /// Only the first send is held: holding the second save's push too would leave their order to
    /// the thread pool.
    /// </summary>
    [Fact]
    public async Task EpicSaveWhosePushIsInFlight_LastPushCarriesTheStoredValue()
    {
        await using var database = await TestDatabase.CreateAsync();
        _stateService.SetEpicGuestPrefillEnabledByDefault(false);

        var notifier = DispatchProxy.Create<ISignalRNotificationService, HeldNotifications>();
        var held = (HeldNotifications)(object)notifier;
        var controller = NewController(database, notifier);

        var first = controller.SetEpicPrefillConfigAsync(
            new EpicGuestPrefillConfigRequest { EnabledByDefault = true, DurationHours = 1 });
        await WaitUntilHeldAsync(held.FirstEntered.Task, first);

        var second = controller.SetEpicPrefillConfigAsync(
            new EpicGuestPrefillConfigRequest { EnabledByDefault = false, DurationHours = 3 });
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1)));

        held.ReleaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Bound);

        Assert.False(_stateService.GetEpicGuestPrefillEnabledByDefault());
        var lastPush = held.Delivered.Last(push => push.EventName == SignalREvents.EpicGuestPrefillConfigChanged);
        Assert.False(DefaultGuestPreferencesTests.ReadPayload<bool>(lastPush.Payload, "enabledByDefault"));
        Assert.Equal(_stateService.GetEpicGuestPrefillDurationHours(),
            DefaultGuestPreferencesTests.ReadPayload<int>(lastPush.Payload, "durationHours"));
    }

    private AuthController NewController(TestDatabase database, ISignalRNotificationService notifier)
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

    /// <summary>
    /// Waits for the first save to reach its held send. A save that ends before it gets there
    /// rethrows its own failure here instead of leaving the wait to time out.
    /// </summary>
    private static async Task WaitUntilHeldAsync<T>(Task heldSend, Task<T> save)
    {
        await Task.WhenAny(heldSend, save).WaitAsync(Bound);
        if (save.IsCompleted)
        {
            await save;
        }

        Assert.True(heldSend.IsCompleted, "the first save ended without sending the held event");
    }

    private static async Task SeedEligibleGuestAsync(TestDatabase database)
    {
        await using var context = database.Factory.CreateDbContext();
        var now = DateTime.UtcNow;
        context.UserSessions.Add(new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = Guid.NewGuid().ToString("N"),
            SessionType = SessionType.Guest,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1)
        });
        await context.SaveChangesAsync();
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
