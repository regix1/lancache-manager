using System.Net;
using System.Reflection;
using System.Text;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Getting back into an installation whose main administrator forgot the password. The main
/// administrator cannot be deleted, disabled or demoted and there is no mail server, so the API key
/// is the only remaining proof of ownership - and a reset that could do anything beyond the password
/// would be a way around every one of those protections.
/// </summary>
public sealed class MainAdminRecoveryTests : IDisposable
{
    private const string OldPassword = "Forgotten-Horse-9";
    private const string NewPassword = "Remembered-Otter-4";

    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly ApiKeyService _apiKeyService;

    public MainAdminRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-main-admin-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _configuration = NewConfiguration(authenticationEnabled: true);
        _apiKeyService = NewApiKeyService(_configuration);
    }

    /// <summary>
    /// The whole point: the password the caller sent is the one that works afterwards, and the
    /// forgotten one no longer does.
    /// </summary>
    [Fact]
    public async Task RecoverySetsTheNewPasswordAndRetiresTheOldOne()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var result = await RecoverAsync(database, NewRequest("owner", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));

        var account = await ReadAccountAsync(database, "owner");
        Assert.Equal(PasswordVerificationResult.Success, Verify(account, NewPassword));
        Assert.Equal(PasswordVerificationResult.Failed, Verify(account, OldPassword));
    }

    /// <summary>
    /// Setup status offers recovery after the host script requests it. Leaving that request active
    /// after a successful reset would send the operator back to the same form.
    /// </summary>
    [Fact]
    public async Task RecoveryClosesTheClaimWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var window = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        Assert.True(window.OpenRecovery());

        var result = await NewController(database.Factory, _apiKeyService, claimWindow: window)
            .RecoverMainAdminPasswordAsync(
                NewRequest("owner", _apiKeyService.GetApiKey()),
                NewSessionService(database.Factory),
                new AccountLockout(NullLogger<AccountLockout>.Instance));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.False(window.IsOpen);
        Assert.False(window.IsRecoveryOpen);

        var second = await NewController(database.Factory, _apiKeyService, claimWindow: window)
            .RecoverMainAdminPasswordAsync(
                NewRequest("owner", _apiKeyService.GetApiKey()),
                NewSessionService(database.Factory),
                new AccountLockout(NullLogger<AccountLockout>.Instance));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(second));
        Assert.Equal(AccountSetupRefusalResponse.RecoveryWindowClosed, StageKeyOf(second));
    }

    /// <summary>
    /// Starting the process opens the first-account claim deadline, but an installation that already
    /// has an account must stay on sign-in until the host recovery script explicitly asks otherwise.
    /// </summary>
    [Fact]
    public async Task AProcessStartAloneDoesNotOpenPasswordRecovery()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var window = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        Assert.True(window.IsOpen);
        Assert.False(window.IsRecoveryOpen);

        var result = await NewController(database.Factory, _apiKeyService, claimWindow: window)
            .RecoverMainAdminPasswordAsync(
                NewRequest("owner", _apiKeyService.GetApiKey()),
                NewSessionService(database.Factory),
                new AccountLockout(NullLogger<AccountLockout>.Instance));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.RecoveryWindowClosed, StageKeyOf(result));
        Assert.Equal(PasswordVerificationResult.Success, Verify(await ReadAccountAsync(database, "owner"), OldPassword));
    }

    /// <summary>
    /// The host script proves the installation key before recovery appears. A wrong key must not
    /// turn a routine restart into a recovery window.
    /// </summary>
    [Fact]
    public void RecoveryRequestRequiresTheInstallationKey()
    {
        var window = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        var controller = NewController(
            new ThrowingDbContextFactory(),
            _apiKeyService,
            claimWindow: window);

        var refused = controller.OpenMainAdminRecovery(
            new RecoveryWindowRequest { ApiKey = "not-the-key" });

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(refused));
        Assert.False(window.IsRecoveryOpen);

        var opened = controller.OpenMainAdminRecovery(
            new RecoveryWindowRequest { ApiKey = _apiKeyService.GetApiKey() });

        Assert.Equal(StatusCodes.Status200OK, StatusOf(opened));
        Assert.True(window.IsRecoveryOpen);
    }

    /// <summary>
    /// The key is the whole of what stands between anyone who can reach the port and the account
    /// that owns the installation, so an absent key and a wrong one are both refused, and refused
    /// the same way.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-the-key")]
    public async Task RecoveryIsRefusedWithoutAValidKey(string apiKey)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var result = await RecoverAsync(database, NewRequest("owner", apiKey));

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.ApiKeyRequired, StageKeyOf(result));

        var account = await ReadAccountAsync(database, "owner");
        Assert.Equal(PasswordVerificationResult.Success, Verify(account, OldPassword));
    }

    /// <summary>
    /// Every ordinary sign-in needs the key, a username and a password together. This endpoint is
    /// the one that accepts the key on its own, so on a process that has been up past the window it
    /// refuses: a key that leaked by itself must not be a remote takeover of the account that
    /// cannot be deleted or demoted. Reopening the window costs a restart, which needs the host the
    /// key is stored on, and that is the second factor.
    /// </summary>
    [Fact]
    public async Task RecoveryIsRefusedOnceTheClaimWindowHasClosed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var closed = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        closed.Expire();

        var result = await NewController(database.Factory, _apiKeyService, claimWindow: closed)
            .RecoverMainAdminPasswordAsync(
                NewRequest("owner", _apiKeyService.GetApiKey()),
                NewSessionService(database.Factory),
                new AccountLockout(NullLogger<AccountLockout>.Instance));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.RecoveryWindowClosed, StageKeyOf(result));

        // The refusal has to leave the password alone, or the endpoint would still be a way in.
        var account = await ReadAccountAsync(database, "owner");
        Assert.Equal(PasswordVerificationResult.Success, Verify(account, OldPassword));
        Assert.Equal(PasswordVerificationResult.Failed, Verify(account, NewPassword));
    }

    /// <summary>
    /// The key is read from the body and nowhere else. A correct key in the X-Api-Key header with
    /// nothing in the body is refused, which is what keeps this endpoint working on the day the
    /// header path stops authorizing ordinary routes.
    /// </summary>
    [Fact]
    public async Task RecoveryDoesNotReadTheKeyFromTheHeader()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();

        var controller = NewController(database.Factory, _apiKeyService);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.RecoverMainAdminPasswordAsync(
            NewRequest("owner", apiKey: string.Empty),
            NewSessionService(database.Factory),
            new AccountLockout(NullLogger<AccountLockout>.Instance));

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.ApiKeyRequired, StageKeyOf(result));

        var account = await ReadAccountAsync(database, "owner");
        Assert.Equal(PasswordVerificationResult.Success, Verify(account, OldPassword));
    }

    /// <summary>
    /// Naming somebody else does not reset somebody else, and does not quietly reset the main
    /// administrator either. Without this the endpoint is a way to take over any account on the
    /// installation with the API key alone.
    /// </summary>
    [Fact]
    public async Task RecoveryCannotTargetAnAccountThatIsNotTheMainAdmin()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);
        await SeedAccountAsync(database.Factory, "helper", mainAdmin: false);

        var result = await RecoverAsync(database, NewRequest("helper", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.MainAdminNotFound, StageKeyOf(result));

        Assert.Equal(PasswordVerificationResult.Success, Verify(await ReadAccountAsync(database, "helper"), OldPassword));
        Assert.Equal(PasswordVerificationResult.Success, Verify(await ReadAccountAsync(database, "owner"), OldPassword));
    }

    /// <summary>
    /// An installation whose first account has not been created yet has nothing to recover, and the
    /// way in is the create endpoint rather than this one.
    /// </summary>
    [Fact]
    public async Task RecoveryIsRefusedWhileTheInstallationHasNoMainAdmin()
    {
        await using var database = await TestDatabase.CreateAsync();

        var result = await RecoverAsync(database, NewRequest("owner", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.MainAdminNotFound, StageKeyOf(result));

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(context.UserAccounts);
    }

    /// <summary>
    /// A password and nothing else. The role and the main-administrator flag survive the reset, and
    /// so does every other account: a recovery that could clear either would be a way around the
    /// rule that the main administrator cannot be demoted.
    /// </summary>
    [Fact]
    public async Task RecoveryChangesNothingButThePassword()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);
        var helper = await SeedAccountAsync(database.Factory, "helper", mainAdmin: false, role: SessionType.User);

        var result = await RecoverAsync(database, NewRequest("owner", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));

        var recovered = await ReadAccountAsync(database, "owner");
        Assert.Equal(owner.Id, recovered.Id);
        Assert.Equal("owner", recovered.Username);
        Assert.Equal(SessionType.Admin, recovered.Role);
        Assert.True(recovered.IsMainAdmin);
        Assert.False(recovered.IsDisabled);

        var untouched = await ReadAccountAsync(database, "helper");
        Assert.Equal(helper.PasswordHash, untouched.PasswordHash);
        Assert.Equal(SessionType.User, untouched.Role);
        Assert.False(untouched.IsMainAdmin);
    }

    /// <summary>
    /// The other half of the same guarantee, from the input side: there is no field on the request
    /// that names a role or the main-administrator flag, so no body a caller can send reaches either
    /// column. Adding one is the change this is here to stop passing unnoticed.
    /// </summary>
    [Fact]
    public void NothingOnTheRequestCanNameARoleOrTheMainAdminFlag()
    {
        var settable = typeof(AccountCredentialsRequest).GetProperties()
            .Where(property => property.Name.Contains("Role", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Admin", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Name)
            .ToArray();

        Assert.True(
            settable.Length == 0,
            $"AccountCredentialsRequest now carries {string.Join(", ", settable)}, which the recovery endpoint "
            + "binds. Recovery must write the password column and nothing else.");
    }

    /// <summary>
    /// A password is recovered because somebody lost it, which is the same shape as somebody else
    /// having found it. Sessions the old password already opened are ended, so the reset actually
    /// takes the account back rather than adding a second way in beside the intruder's.
    /// </summary>
    [Fact]
    public async Task RecoveryRevokesTheMainAdminsLiveSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var ownerSession = await SeedSessionAsync(database.Factory, owner.Id);
        var guestSession = await SeedSessionAsync(database.Factory, accountId: null);

        var result = await RecoverAsync(database, NewRequest("owner", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));

        await using var context = database.Factory.CreateDbContext();
        var revoked = await context.UserSessions.SingleAsync(s => s.Id == ownerSession);
        Assert.True(revoked.IsRevoked);
        Assert.NotNull(revoked.RevokedAtUtc);

        // A session with no account is nobody's to revoke here, and revoking every session on the
        // installation would sign out people whose password was never lost.
        Assert.False((await context.UserSessions.SingleAsync(s => s.Id == guestSession)).IsRevoked);
    }

    /// <summary>
    /// Resetting the owner's password with nothing but the installation key is the event an operator
    /// most needs to find afterwards, whether they did it or somebody else did.
    /// </summary>
    [Fact]
    public async Task RecoveryRecordsTheIdentityEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);
        var before = DateTime.UtcNow;

        var result = await RecoverAsync(database, NewRequest("owner", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(IdentityAuditEvent.MainAdminPasswordRecovered, entry.Event);
        Assert.Equal(owner.Id, entry.TargetAccountId);
        Assert.InRange(entry.PerformedAtUtc, before, DateTime.UtcNow);

        // The caller proved the API key. It has no account and no session, and the columns are
        // nullable so that this records as a real event rather than throwing.
        Assert.Null(entry.PerformedByAccountId);
        Assert.Null(entry.PerformedBySessionId);
    }

    /// <summary>
    /// An audit trail that can block a recovery turns a logging fault into the lockout this endpoint
    /// exists to end. The password is reset whether or not the row lands.
    /// </summary>
    [Fact]
    public async Task AFailedAuditWriteStillLeavesThePasswordReset()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var controller = NewController(
            database.Factory,
            _apiKeyService,
            auditService: new IdentityAuditService(
                new ThrowingDbContextFactory(), NullLogger<IdentityAuditService>.Instance));

        var result = await controller.RecoverMainAdminPasswordAsync(
            NewRequest("owner", _apiKeyService.GetApiKey()),
            NewSessionService(database.Factory),
            new AccountLockout(NullLogger<AccountLockout>.Instance));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal(PasswordVerificationResult.Success, Verify(await ReadAccountAsync(database, "owner"), NewPassword));

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(context.IdentityAuditEntries);
    }

    /// <summary>
    /// Nothing on this path reads Security:EnableAuthentication, and an installation running with it
    /// off is exactly the one whose operator has never typed the password and is most likely to have
    /// lost it.
    /// </summary>
    [Fact]
    public async Task RecoveryWorksWithAuthenticationDisabled()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var configuration = NewConfiguration(authenticationEnabled: false);
        var apiKeyService = NewApiKeyService(configuration);
        var controller = NewController(database.Factory, apiKeyService);

        var result = await controller.RecoverMainAdminPasswordAsync(
            NewRequest("owner", apiKeyService.GetApiKey()),
            NewSessionService(database.Factory, configuration, apiKeyService),
            new AccountLockout(NullLogger<AccountLockout>.Instance));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal(PasswordVerificationResult.Success, Verify(await ReadAccountAsync(database, "owner"), NewPassword));
    }

    /// <summary>
    /// Getting the password wrong five times is what sends an operator here, and those failures count
    /// because counting them takes the API key the operator holds. The lock and the password are
    /// separate terms in the sign-in refusal and both refusals read the same by design, so a recovery
    /// that leaves the count standing answers "Password reset" and is then followed by a sign-in
    /// refused as though the new password were wrong.
    /// </summary>
    [Fact]
    public async Task RecoveryLetsALockedOutOwnerSignInStraightAway()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", mainAdmin: true);

        var lockout = new AccountLockout(NullLogger<AccountLockout>.Instance);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            lockout.RecordFailure(owner.Id);
        }

        Assert.True(lockout.IsLocked(owner.Id));

        var recovered = await NewController(database.Factory, _apiKeyService)
            .RecoverMainAdminPasswordAsync(
                NewRequest("owner", _apiKeyService.GetApiKey()),
                NewSessionService(database.Factory),
                lockout);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(recovered));

        var signIn = new AuthController(
            NewSessionService(database.Factory),
            NullLogger<AuthController>.Instance,
            database.Factory,
            stateService: null!,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            _apiKeyService,
            new PasswordHasher<UserAccount>(),
            lockout,
            new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var signedIn = await signIn.LoginAsync(new LoginRequest
        {
            Username = "owner",
            Password = NewPassword,
            ApiKey = _apiKeyService.GetApiKey()
        });

        Assert.Equal(
            StatusCodes.Status200OK,
            Assert.IsAssignableFrom<ObjectResult>(signedIn.Result).StatusCode);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<ActionResult<MessageResponse>> RecoverAsync(TestDatabase database, AccountCredentialsRequest request) =>
        NewController(database.Factory, _apiKeyService)
            .RecoverMainAdminPasswordAsync(
                request,
                NewSessionService(database.Factory),
                new AccountLockout(NullLogger<AccountLockout>.Instance));

    private IConfiguration NewConfiguration(bool authenticationEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, $"api_key-{authenticationEnabled}.txt"),
                ["Security:EnableAuthentication"] = authenticationEnabled.ToString()
            })
            .Build();

    private static ApiKeyService NewApiKeyService(IConfiguration configuration) =>
        new(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);

    private static AccountSetupController NewController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ApiKeyService apiKeyService,
        IdentityAuditService? auditService = null,
        AccountClaimWindow? claimWindow = null) =>
        new(
            dbContextFactory,
            apiKeyService,
            claimWindow ?? NewRecoveryWindow(),
            new PasswordHasher<UserAccount>(),
            auditService ?? new IdentityAuditService(dbContextFactory, NullLogger<IdentityAuditService>.Instance),
            NullLogger<AccountSetupController>.Instance);

    private static AccountClaimWindow NewRecoveryWindow()
    {
        var window = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        Assert.True(window.OpenRecovery());
        return window;
    }

    private SessionService NewSessionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IConfiguration? configuration = null,
        ApiKeyService? apiKeyService = null) =>
        new(
            dbContextFactory,
            apiKeyService ?? _apiKeyService,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            configuration ?? _configuration);

    private static AccountCredentialsRequest NewRequest(string username, string apiKey) =>
        new() { Username = username, Password = NewPassword, ApiKey = apiKey };

    private static async Task<UserAccount> SeedAccountAsync(
        TestDbContextFactory dbContextFactory,
        string username,
        bool mainAdmin,
        SessionType role = SessionType.Admin)
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            Role = role,
            IsMainAdmin = mainAdmin,
            CreatedAtUtc = DateTime.UtcNow
        };
        account.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(account, OldPassword);

        await using var context = dbContextFactory.CreateDbContext();
        context.UserAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private static async Task<Guid> SeedSessionAsync(TestDbContextFactory dbContextFactory, Guid? accountId)
    {
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = Guid.NewGuid().ToString("N"),
            SessionType = accountId == null ? SessionType.Guest : SessionType.Admin,
            AccountId = accountId,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(1)
        };

        await using var context = dbContextFactory.CreateDbContext();
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private static async Task<UserAccount> ReadAccountAsync(TestDatabase database, string username)
    {
        await using var context = database.Factory.CreateDbContext();
        return await context.UserAccounts.SingleAsync(a => a.Username == username);
    }

    private static PasswordVerificationResult Verify(UserAccount account, string password) =>
        new PasswordHasher<UserAccount>().VerifyHashedPassword(account, account.PasswordHash, password);

    private static int StatusOf(ActionResult<MessageResponse> result) =>
        Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode ?? 0;

    private static string StageKeyOf(ActionResult<MessageResponse> result) =>
        Assert.IsType<AccountSetupRefusalResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).StageKey;
}

/// <summary>
/// The limiter half of the same endpoint, which needs the whole application rather than a controller
/// instance. Its own class so the tests above are not serialized behind every other test that boots
/// a host.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class MainAdminRecoveryThrottlingTests
{
    /// <summary>
    /// Anonymous, and it takes the API key in the body, so one address can otherwise sit on it
    /// guessing for as long as it likes. Five wrong keys from one address is the limit, and a
    /// different address is unaffected by them.
    /// </summary>
    [Fact]
    public async Task RecoveryIsThrottledPerCallerAddress()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        const string body = """{"apiKey":"not-the-key","username":"owner","password":"Remembered-Otter-4"}""";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = await SendAsync(host.Application.Server, "10.0.7.1", body);

            Assert.Equal(StatusCodes.Status401Unauthorized, refused);
        }

        var throttled = await SendAsync(host.Application.Server, "10.0.7.1", body);
        var other = await SendAsync(host.Application.Server, "10.0.7.2", body);

        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled);
        Assert.Equal(StatusCodes.Status401Unauthorized, other);
    }

    /// <summary>
    /// Sends one request through the whole pipeline with the caller's address set, which the test
    /// host leaves null and the limiter partitions on.
    /// </summary>
    private static async Task<int> SendAsync(TestServer server, string callerAddress, string body)
    {
        var context = await server.SendAsync(request =>
        {
            var payload = Encoding.UTF8.GetBytes(body);

            request.Connection.RemoteIpAddress = IPAddress.Parse(callerAddress);
            request.Request.Method = "POST";
            request.Request.Scheme = "http";
            request.Request.Host = new HostString("localhost");
            request.Request.Path = "/api/account-setup/recover-main-admin";
            request.Request.ContentType = "application/json";
            request.Request.ContentLength = payload.Length;
            request.Request.Body = new MemoryStream(payload);
        });

        return context.Response.StatusCode;
    }
}
