using System.Text.Json;
using LancacheManager.Controllers;
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
/// Clearing the Battle.net or Riot guest thread limit on the Users page sends the field as null. A body that
/// leaves the field out keeps the stored limit, so the null has to be read as a clear, not as a missing field.
/// </summary>
public sealed class GuestThreadLimitExplicitClearTests : IDisposable
{
    // The body the page sends when the administrator clears the thread limit.
    private const string BodyClearingThreadLimit = """{"enabledByDefault":true,"durationHours":3,"maxThreadCount":null}""";

    private readonly string _root;
    private readonly ApiKeyService _apiKeyService;
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;

    public GuestThreadLimitExplicitClearTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-guest-thread-clear-{Guid.NewGuid():N}");
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
    public async Task BattleNetAndRiotSavesWithANullThreadLimitClearTheStoredLimitAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = NewController(database);
        _stateService.SetBattleNetDefaultGuestMaxThreadCount(4);
        _stateService.SetRiotDefaultGuestMaxThreadCount(4);

        var battleNetSave = await controller.SetBattleNetPrefillConfigAsync(
            JsonSerializer.Deserialize<BattleNetGuestPrefillConfigRequest>(BodyClearingThreadLimit, JsonSerializerOptions.Web)!);
        var riotSave = await controller.SetRiotPrefillConfigAsync(
            JsonSerializer.Deserialize<RiotGuestPrefillConfigRequest>(BodyClearingThreadLimit, JsonSerializerOptions.Web)!);

        Assert.Null(Assert.IsType<SetGuestPrefillConfigResponse>(Assert.IsType<OkObjectResult>(battleNetSave.Result).Value).MaxThreadCount);
        Assert.Null(Assert.IsType<SetGuestPrefillConfigResponse>(Assert.IsType<OkObjectResult>(riotSave.Result).Value).MaxThreadCount);
        Assert.Null(_stateService.GetBattleNetDefaultGuestMaxThreadCount());
        Assert.Null(_stateService.GetRiotDefaultGuestMaxThreadCount());
    }

    private AuthController NewController(TestDatabase database)
    {
        var notifier = new ScheduledRunReporterTests.CapturingNotificationService();
        return new AuthController(
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
            new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
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
