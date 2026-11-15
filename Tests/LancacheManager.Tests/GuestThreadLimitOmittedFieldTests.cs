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
/// The Users page from before Battle.net and Riot had a guest thread limit saves those services' guest
/// switch and length without a thread limit field. A page left open across the update, or a script posting
/// that body, changes the switch or the length only: the thread limit the server holds stays as it was.
/// </summary>
public sealed class GuestThreadLimitOmittedFieldTests : IDisposable
{
    private const int StoredLimit = 4;

    // The body the page shipped before this change sends for these two services.
    private const string BodyWithoutThreadLimit = """{"enabledByDefault":true,"durationHours":3}""";

    private readonly string _root;
    private readonly ApiKeyService _apiKeyService;
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;

    public GuestThreadLimitOmittedFieldTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-guest-thread-limit-{Guid.NewGuid():N}");
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
    public async Task BattleNetAndRiotSavesWithoutAThreadLimitKeepTheStoredLimitAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = NewSaveController(database);
        _stateService.SetBattleNetDefaultGuestMaxThreadCount(StoredLimit);
        _stateService.SetRiotDefaultGuestMaxThreadCount(StoredLimit);

        var battleNetSave = await controller.SetBattleNetPrefillConfigAsync(
            JsonSerializer.Deserialize<BattleNetGuestPrefillConfigRequest>(BodyWithoutThreadLimit, JsonSerializerOptions.Web)!);
        var riotSave = await controller.SetRiotPrefillConfigAsync(
            JsonSerializer.Deserialize<RiotGuestPrefillConfigRequest>(BodyWithoutThreadLimit, JsonSerializerOptions.Web)!);
        Assert.IsType<OkObjectResult>(battleNetSave.Result);
        Assert.IsType<OkObjectResult>(riotSave.Result);

        var battleNet = Assert.IsType<EpicGuestPrefillConfigResponse>(
            Assert.IsType<OkObjectResult>(controller.GetBattleNetPrefillConfig().Result).Value);
        var riot = Assert.IsType<EpicGuestPrefillConfigResponse>(
            Assert.IsType<OkObjectResult>(controller.GetRiotPrefillConfig().Result).Value);

        // The switch and the length the body carried were saved, so only the thread limit is in question.
        Assert.True(battleNet.EnabledByDefault && battleNet.DurationHours == 3, "the Battle.net save did not store the switch and length it sent");
        Assert.True(riot.EnabledByDefault && riot.DurationHours == 3, "the Riot save did not store the switch and length it sent");
        var lost = new List<string>();
        if (battleNet.MaxThreadCount != StoredLimit)
            lost.Add($"Battle.net guest thread limit is {battleNet.MaxThreadCount?.ToString() ?? "none"}");
        if (riot.MaxThreadCount != StoredLimit)
            lost.Add($"Riot guest thread limit is {riot.MaxThreadCount?.ToString() ?? "none"}");
        Assert.True(
            lost.Count == 0,
            $"a guest save whose body has no thread limit cleared the stored limit of {StoredLimit}: {string.Join("; ", lost)}");
    }

    private AuthController NewSaveController(TestDatabase database)
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
            // A temp folder that will not delete is not worth failing a test over.
        }
    }
}
