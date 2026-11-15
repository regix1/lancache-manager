using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The account deletes beside the wipe: a start with sign-in off deletes even the owner an environment
/// login allows and ends its session, and deleting one account or rotating the API key takes each
/// deleted account's saved game logins with it, as the wipe does.
/// </summary>
// The shared session is process-wide (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountDeletionEdgesTests
{
    private const string OwnerSubject = "owner-subject";

    /// <summary>
    /// With sign-in off, a cookie on the main admin's own session wins over the shared session, so an
    /// owner left by the start would stay signed in as the account in its browser. With sign-in off no
    /// account is kept, an environment-allowed one included, so the owner and its session both go.
    /// </summary>
    [Fact]
    public async Task AStartWithSignInOff_DeletesTheOwnerAnEnvironmentLoginAllowsAndEndsItsSessionAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Security:SignInMode"] = "unauthenticated",
            ["Security:GitHub:OwnerSubject"] = OwnerSubject
        };
        var owner = SignInModeScene.Account(Guid.NewGuid(), "owner", SessionType.Admin, mainAdmin: true);
        await using var installation = await SignInModeScene.CreateAsync(settings, access =>
        {
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
            access.Logins.Add(new OidcSettings
            {
                Id = "github",
                Kind = LoginKind.GitHub,
                ClientId = "github-client",
                ClientSecret = "secret",
                DisplayName = "GitHub",
                OwnerIssuer = "https://github.com",
                OwnerSubject = OwnerSubject,
                OwnerAccountId = owner.Id,
                AccountIds = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["github-" + OwnerSubject] = owner.Id }
            });
        });
        await installation.SeedAsync(owner);
        Assert.True(installation.Access.IsAllowedByEnvironment(owner.Id), "the environment login does not allow the owner, so the test proves nothing");
        var (ownerToken, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);

        await using var restarted = installation.Restart(settings);
        await restarted.StartAsync();

        var wrong = new List<string>();
        if (await restarted.Sessions.ValidateSessionAsync(ownerToken) is not null)
            wrong.Add($"the owner's account session {ownerSession.Id} still signs its browser in as the account");
        await using (var read = await installation.Database.Factory.CreateDbContextAsync())
        {
            if (await read.UserAccounts.AnyAsync(account => account.Id == owner.Id))
                wrong.Add("the owner the environment login allows is still an account with sign-in off");
        }
        if (restarted.State.GetState().Access.Logins.Any(login => login.AccountIds.ContainsValue(owner.Id) || login.OwnerAccountId == owner.Id))
            wrong.Add("a sign-in link still names the deleted owner");
        Assert.True(wrong.Count == 0, "after a start with sign-in off: " + string.Join("; ", wrong));
    }

    [Fact]
    public async Task DeletingOneAccount_TakesItsSavedAndActiveGameLoginsAsync()
    {
        await using var installation = await SignedInInstallationAsync();
        var (ownerSession, memberId) = await SeedOwnerAndMemberAsync(installation);

        var deleted = await installation.Controller<AccountsController>(ownerSession).DeleteAccountAsync(memberId);

        Assert.True(deleted.Result is OkObjectResult, $"deleting the member was answered {(deleted.Result as ObjectResult)?.StatusCode}");
        AssertNoEpicLoginOf(installation, memberId, "deleting the member");
    }

    [Fact]
    public async Task RotatingTheApiKey_TakesTheSavedGameLoginsOfEveryAccountItDeletesAsync()
    {
        await using var installation = await SignedInInstallationAsync();
        var (ownerSession, memberId) = await SeedOwnerAndMemberAsync(installation);
        var request = new DefaultHttpContext { RequestServices = installation.App };
        request.Items["Session"] = ownerSession;
        var rotation = new ApiKeysController(
            installation.Database.Factory,
            installation.ApiKey,
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            installation.Logins.Storage,
            installation.Logins.Xbox,
            installation.Logins.Epic,
            installation.State,
            installation.Sessions,
            installation.App.GetRequiredService<IdentityAuditService>(),
            installation.App.GetRequiredService<IConfiguration>(),
            new AuthenticationHelper(installation.ApiKey, NullLogger<AuthenticationHelper>.Instance),
            NullLogger<ApiKeysController>.Instance,
            installation.Access)
        {
            ControllerContext = new ControllerContext { HttpContext = request }
        };

        Assert.IsType<OkObjectResult>(await rotation.RegenerateApiKeyAsync());

        await using (var read = await installation.Database.Factory.CreateDbContextAsync())
            Assert.False(await read.UserAccounts.AnyAsync(account => account.Id == memberId), "the rotation kept the member, so it never reached the logins");
        AssertNoEpicLoginOf(installation, memberId, "rotating the API key");
    }

    /// <summary>An installation with sign-in on and a finished setup.</summary>
    private static Task<SignInModeScene> SignedInInstallationAsync()
        => SignInModeScene.CreateAsync(new Dictionary<string, string?>(), access =>
        {
            access.Mode = AccountMode.ApiKeyPassword;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });

    /// <summary>
    /// The owner, signed in, and a member who signed Epic in, so the member's Epic login is both the
    /// active one and saved under the member's id.
    /// </summary>
    private static async Task<(UserSession OwnerSession, Guid MemberId)> SeedOwnerAndMemberAsync(SignInModeScene installation)
    {
        var ownerId = installation.Logins.Owner.AccountId!.Value;
        var memberId = installation.Logins.Other.AccountId!.Value;
        var owner = SignInModeScene.Account(ownerId, "owner", SessionType.Admin, mainAdmin: true);
        await installation.SeedAsync(owner, SignInModeScene.Account(memberId, "member", SessionType.User, mainAdmin: false));
        var (_, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);
        installation.Logins.Epic.SaveAuthData(new EpicAuthData { OwnerAccountId = memberId, RefreshToken = "epic", AccountId = "epic-account" });
        Assert.True(File.Exists(SavedEpicPath(installation, memberId)), "the member's Epic login was not saved, so the test proves nothing");
        return (ownerSession, memberId);
    }

    private static string SavedEpicPath(SignInModeScene installation, Guid accountId)
        => Path.Combine(installation.Logins.Epic.GetAuthDirectory(), "saved", $"{accountId:N}.json");

    private static void AssertNoEpicLoginOf(SignInModeScene installation, Guid memberId, string deletion)
    {
        var wrong = new List<string>();
        if (File.Exists(SavedEpicPath(installation, memberId)))
            wrong.Add("the deleted member's saved Epic login is still on disk");
        var active = installation.Logins.Epic.GetAuthData();
        if (active.OwnerAccountId == memberId && active.RefreshToken is not null)
            wrong.Add("the active Epic login still signs prefill in as the deleted member");
        Assert.True(wrong.Count == 0, $"after {deletion}: " + string.Join("; ", wrong));
    }
}
