using LancacheManager.Controllers;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Unauthenticated mode keeps no accounts, and an account an environment login allows is no exception:
/// with sign-in off nobody can sign in to it. So the confirmed switch to unauthenticated mode is
/// accepted while such an account exists, and it deletes that account with every other one.
/// </summary>
// Ending every session touches the process-wide shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SignInOffDeletesEnvironmentAllowedAccountsTests
{
    private const string OwnerSubject = "owner-subject";

    [Fact]
    public async Task TheConfirmedSwitchToUnauthenticatedMode_DeletesAnAccountAnEnvironmentLoginAllowsAsync()
    {
        var owner = SignInModeScene.Account(Guid.NewGuid(), "owner", SessionType.Admin, mainAdmin: true);
        await using var installation = await SignInModeScene.CreateAsync(
            new Dictionary<string, string?> { ["Security:GitHub:OwnerSubject"] = OwnerSubject },
            access =>
            {
                access.Mode = AccountMode.ApiKeyPassword;
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
        await installation.SeedAsync(owner, SignInModeScene.Account(Guid.NewGuid(), "member", SessionType.User, mainAdmin: false));
        Assert.True(installation.Access.IsAllowedByEnvironment(owner.Id), "the environment login does not allow the owner, so the test proves nothing");
        var (_, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);

        // The owner confirms that turning sign-in off deletes every account.
        var switched = await installation.Controller<AccessController>(ownerSession).SetUpAsync(
            UnauthenticatedModeWipesAccountsTests.ConfirmingAccountWipe(new AccessSetupRequest
            {
                Mode = AccountMode.Unauthenticated,
                AcknowledgeUnauthenticated = true,
                ApiKey = installation.ApiKey.GetApiKey()
            }, accountCount: 2));

        var wrong = new List<string>();
        if (switched.Result is not OkObjectResult)
        {
            var refusal = switched.Result as ObjectResult;
            wrong.Add($"the confirmed switch was answered {refusal?.StatusCode} ({refusal?.Value?.GetType().GetProperty("StageKey")?.GetValue(refusal.Value)})");
        }
        await using var read = await installation.Database.Factory.CreateDbContextAsync();
        var left = await read.UserAccounts.Select(account => account.Username).ToListAsync();
        if (left.Count > 0)
            wrong.Add($"{left.Count} accounts remain ({string.Join(", ", left)})");
        Assert.True(wrong.Count == 0, "with an account an environment login allows: " + string.Join("; ", wrong));
    }
}
