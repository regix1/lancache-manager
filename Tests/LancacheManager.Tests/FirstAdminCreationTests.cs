using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Creating the account that owns the installation, and the two things that stop anyone else doing
/// it first: the API key and the window the application opens at startup.
/// </summary>
public sealed class FirstAdminCreationTests : IDisposable
{
    private const string GoodPassword = "Correct-Horse-9";

    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly ApiKeyService _apiKeyService;

    public FirstAdminCreationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-first-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _configuration = NewConfiguration(authenticationEnabled: true);
        _apiKeyService = NewApiKeyService(_configuration);
    }

    /// <summary>
    /// The account that owns the installation: the admin role and the main-admin flag, and a password
    /// that verifies against what was submitted.
    /// </summary>
    [Fact]
    public async Task CreateSucceedsWithTheKeyWhileTheWindowIsOpen()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = NewController(database.Factory, _apiKeyService);

        var result = await controller.CreateFirstAdminAsync(NewRequest("operator", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));

        await using var context = database.Factory.CreateDbContext();
        var account = await context.UserAccounts.SingleAsync();
        Assert.Equal("operator", account.Username);
        Assert.True(account.IsMainAdmin);
        Assert.Equal(SessionType.Admin, account.Role);
        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<UserAccount>().VerifyHashedPassword(account, account.PasswordHash, GoodPassword));
    }

    /// <summary>
    /// Setup status treats an open window plus an existing account as recovery. The create has to
    /// close the window so the operator is not sent back to this form for a password they just set.
    /// </summary>
    [Fact]
    public async Task CreateClosesTheClaimWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var window = NewClaimWindow();
        var controller = NewController(database.Factory, _apiKeyService, window);

        var result = await controller.CreateFirstAdminAsync(NewRequest("operator", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.False(window.IsOpen);
    }

    /// <summary>
    /// An installation that already has somebody in it is not claimable, and saying so is a refusal
    /// rather than a stack trace: the second person through this endpoint is a normal event on a
    /// shared network, not a fault.
    /// </summary>
    [Fact]
    public async Task CreateIsRefusedOnceAnAccountExists()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "existing");
        var controller = NewController(database.Factory, _apiKeyService);

        var result = await controller.CreateFirstAdminAsync(NewRequest("operator", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.AccountExists, StageKeyOf(result));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal("existing", (await context.UserAccounts.SingleAsync()).Username);
    }

    /// <summary>
    /// The other way this installation turns out to be owned: the count found nothing and the insert
    /// found the row anyway. Same refusal as the count's, so it says the same thing rather than
    /// leaving the operator to work out why one 409 reads differently from the other.
    ///
    /// Seeding from inside the save is what makes the order certain. Two live requests reach this
    /// handler by racing, which is why TwoSimultaneousCreatesLeaveExactlyOneAccount asserts the
    /// outcome rather than which of the two refusals produced it.
    /// </summary>
    [Fact]
    public async Task CreateIsRefusedWhenTheAccountArrivesDuringTheWrite()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        var interrupted = new DbContextOptionsBuilder<AppDbContext>(options)
            .AddInterceptors(new AccountArrivesDuringSave(options))
            .Options;

        var controller = NewController(new TestDbContextFactory(interrupted), _apiKeyService);

        var result = await controller.CreateFirstAdminAsync(NewRequest("operator", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.AccountExists, StageKeyOf(result));

        await using var after = new AppDbContext(options);
        Assert.Equal("first", (await after.UserAccounts.SingleAsync()).Username);
    }

    /// <summary>
    /// A forgotten instance left running on a network stops being claimable, and the way back is a
    /// restart, which is what the log line at startup tells the operator.
    /// </summary>
    [Fact]
    public async Task CreateIsRefusedOnceTheWindowHasClosed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var closed = NewClaimWindow();
        CloseWindow(closed);
        var controller = NewController(database.Factory, _apiKeyService, closed);

        var result = await controller.CreateFirstAdminAsync(NewRequest("operator", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.ClaimWindowClosed, StageKeyOf(result));

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(context.UserAccounts);
    }

    /// <summary>
    /// The other half of the window: a restart builds a new one, and it is open. The instance is what
    /// holds the deadline, so this is the same thing the host does on the next start.
    /// </summary>
    [Fact]
    public async Task ARestartReopensTheWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var closed = NewClaimWindow();
        CloseWindow(closed);
        Assert.False(closed.IsOpen);

        var controller = NewController(database.Factory, _apiKeyService, NewClaimWindow());

        var result = await controller.CreateFirstAdminAsync(NewRequest("operator", _apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
    }

    /// <summary>
    /// The window is the second lock, never the only one. Both an absent key and a wrong one are
    /// refused, and are refused identically.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-the-key")]
    public async Task CreateIsRefusedWithoutTheKeyInsideTheWindow(string apiKey)
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = NewController(database.Factory, _apiKeyService);

        var result = await controller.CreateFirstAdminAsync(NewRequest("operator", apiKey));

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal(AccountSetupRefusalResponse.ApiKeyRequired, StageKeyOf(result));

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(context.UserAccounts);
    }

    /// <summary>
    /// An installation that has been running with Security:EnableAuthentication=false still needs a
    /// first account the day it is turned on, and the restart that turning it on requires is the same
    /// restart that opens the window. Nothing on this path reads the flag, and this is what says so.
    /// </summary>
    [Fact]
    public async Task CreateWorksWithAuthenticationDisabled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = NewConfiguration(authenticationEnabled: false);
        var apiKeyService = NewApiKeyService(configuration);
        var controller = NewController(database.Factory, apiKeyService);

        var result = await controller.CreateFirstAdminAsync(NewRequest("operator", apiKeyService.GetApiKey()));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));

        await using var context = database.Factory.CreateDbContext();
        Assert.True((await context.UserAccounts.SingleAsync()).IsMainAdmin);
    }

    /// <summary>
    /// Two people submitting the form at the same moment: exactly one account, and the other caller is
    /// answered 409 rather than 500. The names differ, so the unique username index would let both of
    /// these through and IX_UserAccounts_IsMainAdmin (AppDbContext.cs:222-226) is what does not -
    /// UserAccountConstraintTests.SecondMainAdmin_IsRefusedByTheStore is that index on its own. Which
    /// of the two refusals the loser gets, the read or the index, depends on how far it got before the
    /// winner committed; both are a clean 409, which is why this asserts the outcome rather than the
    /// route to it.
    ///
    /// Both requests need their own connection, which the shared helper gives them: each context it
    /// hands out opens its own against the schema.
    /// </summary>
    [Fact]
    public async Task TwoSimultaneousCreatesLeaveExactlyOneAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var first = NewController(database.Factory, _apiKeyService);
        var second = NewController(database.Factory, _apiKeyService);
        var apiKey = _apiKeyService.GetApiKey();

        using var bothReady = new Barrier(2);

        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                bothReady.SignalAndWait();
                return await first.CreateFirstAdminAsync(NewRequest("operator", apiKey));
            }),
            Task.Run(async () =>
            {
                bothReady.SignalAndWait();
                return await second.CreateFirstAdminAsync(NewRequest("second-operator", apiKey));
            }));

        var statuses = results.Select(StatusOf).OrderBy(status => status).ToArray();
        Assert.Equal(new[] { StatusCodes.Status200OK, StatusCodes.Status409Conflict }, statuses);
        Assert.Equal(
            AccountSetupRefusalResponse.AccountExists,
            StageKeyOf(results.Single(result => StatusOf(result) == StatusCodes.Status409Conflict)));

        await using var context = database.Factory.CreateDbContext();
        Assert.True(await context.UserAccounts.SingleAsync() is { IsMainAdmin: true });
    }

    /// <summary>
    /// The upgrade: a database carrying sessions from a version that had no accounts. Those cookies
    /// still authenticate, so the people holding them would carry on as administrators while the
    /// installation waits for its first account.
    /// </summary>
    [Fact]
    public async Task StartupClearsSessionsWhileNoAccountExists()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionService = NewSessionService(database.Factory);
        Assert.NotNull(await sessionService.CreateAdminSessionAsync(_apiKeyService.GetApiKey(), new DefaultHttpContext()));

        await NewSessionResetService(database.Factory, sessionService).StartAsync(CancellationToken.None);

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(context.UserSessions);
    }

    /// <summary>
    /// And it stops there. Once the installation has an account, a restart must not sign everybody
    /// out.
    /// </summary>
    [Fact]
    public async Task StartupLeavesSessionsAloneOnceAnAccountExists()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionService = NewSessionService(database.Factory);
        Assert.NotNull(await sessionService.CreateAdminSessionAsync(_apiKeyService.GetApiKey(), new DefaultHttpContext()));
        await SeedAccountAsync(database.Factory, "existing");

        await NewSessionResetService(database.Factory, sessionService).StartAsync(CancellationToken.None);

        await using var context = database.Factory.CreateDbContext();
        Assert.Single(context.UserSessions);
    }

    /// <summary>
    /// And an installation running with authentication off is left alone. It never creates an account,
    /// so the account count on its own would clear its sessions on every start, and the preferences
    /// cascade off the session row (AppDbContext.cs:298-303) - the theme, the clock and the refresh
    /// rate would go back to their defaults every restart.
    /// </summary>
    [Fact]
    public async Task StartupLeavesSessionsAloneWhileAuthenticationIsDisabled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        await using (var seedContext = database.Factory.CreateDbContext())
        {
            seedContext.UserSessions.Add(new UserSession
            {
                Id = sessionId,
                SessionTokenHash = "hash",
                SessionType = SessionType.Admin,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            seedContext.UserPreferences.Add(new UserPreferences
            {
                SessionId = sessionId,
                SelectedTheme = "midnight",
                UpdatedAtUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        var sessionService = NewSessionService(
            database.Factory,
            NewConfiguration(authenticationEnabled: false));

        await NewSessionResetService(database.Factory, sessionService).StartAsync(CancellationToken.None);

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(sessionId, Assert.Single(context.UserSessions).Id);
        Assert.Equal("midnight", Assert.Single(context.UserPreferences).SelectedTheme);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

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

    private static AccountClaimWindow NewClaimWindow() => new(NullLogger<AccountClaimWindow>.Instance);

    /// <summary>
    /// Moves the deadline into the past, which is the one thing a test cannot do by waiting: the
    /// window is an hour long and is fixed when the instance is built.
    /// </summary>
    private static void CloseWindow(AccountClaimWindow window)
    {
        var deadline = typeof(AccountClaimWindow)
            .GetField("_closesAtUtc", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AccountClaimWindow no longer has a _closesAtUtc field.");

        deadline.SetValue(window, DateTime.UtcNow.AddMinutes(-1));
    }

    private static AccountSetupController NewController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ApiKeyService apiKeyService,
        AccountClaimWindow? claimWindow = null) =>
        new(
            dbContextFactory,
            apiKeyService,
            claimWindow ?? NewClaimWindow(),
            new PasswordHasher<UserAccount>(),
            new IdentityAuditService(dbContextFactory, NullLogger<IdentityAuditService>.Instance),
            NullLogger<AccountSetupController>.Instance);

    private SessionService NewSessionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IConfiguration? configuration = null) =>
        new(
            dbContextFactory,
            _apiKeyService,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            configuration ?? _configuration);

    private static FirstAdminSessionResetService NewSessionResetService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        SessionService sessionService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sessionService);

        return new FirstAdminSessionResetService(
            dbContextFactory,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FirstAdminSessionResetService>.Instance);
    }

    private static AccountCredentialsRequest NewRequest(string username, string apiKey) =>
        new() { Username = username, Password = GoodPassword, ApiKey = apiKey };

    private static async Task SeedAccountAsync(TestDbContextFactory dbContextFactory, string username)
    {
        await using var context = dbContextFactory.CreateDbContext();
        context.UserAccounts.Add(new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = "hash",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static int StatusOf(ActionResult<MessageResponse> result) =>
        Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode ?? 0;

    private static string StageKeyOf(ActionResult<MessageResponse> result) =>
        Assert.IsType<AccountSetupRefusalResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).StageKey;

    /// <summary>
    /// Puts the account in the table between the count that found none and the insert that runs into
    /// it, on the one connection both share. IX_UserAccounts_IsMainAdmin is what the insert then
    /// breaks (AppDbContext.cs:222-226), which is the shape a request that lost the race sees.
    /// </summary>
    private sealed class AccountArrivesDuringSave(DbContextOptions<AppDbContext> options) : SaveChangesInterceptor
    {
        private bool _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_arrived)
            {
                _arrived = true;

                await using var context = new AppDbContext(options);
                context.UserAccounts.Add(new UserAccount
                {
                    Id = Guid.NewGuid(),
                    Username = "first",
                    PasswordHash = "hash",
                    Role = SessionType.Admin,
                    IsMainAdmin = true,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await context.SaveChangesAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

}
