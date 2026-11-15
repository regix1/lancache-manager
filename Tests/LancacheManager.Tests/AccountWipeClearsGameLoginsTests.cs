using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Tests;

/// <summary>
/// Deleting every account deletes what each account saved, its Steam, Epic and Xbox logins among it.
/// Those logins are files, not rows, so no database cascade removes them: left behind, the active login
/// an account owned keeps signing prefill in as a person who no longer has an account. Each way every
/// account is deleted is run: the switch to unauthenticated mode, the delete-every-account action, and a
/// start that finds <c>Security__SignInMode</c> forcing unauthenticated mode.
/// </summary>
// Ending every session touches the process-wide shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountWipeClearsGameLoginsTests
{
    [Theory]
    [InlineData("the switch to unauthenticated mode")]
    [InlineData("the delete-every-account action")]
    [InlineData("a start with Security__SignInMode forcing unauthenticated mode")]
    public async Task DeletingEveryAccount_LeavesNoSavedOrActiveGameLoginOfADeletedAccountAsync(string deletion)
    {
        var atStart = deletion.StartsWith("a start", StringComparison.Ordinal);
        await using var installation = await SignInModeScene.CreateAsync(
            new Dictionary<string, string?>(),
            access =>
            {
                access.Mode = AccountMode.ApiKeyPassword;
                access.SetupVersion = AccessSettings.RequiredSetupVersion;
            });
        var logins = installation.Logins;
        var ownerId = logins.Owner.AccountId!.Value;
        var memberId = logins.Other.AccountId!.Value;
        var owner = SignInModeScene.Account(ownerId, "owner", SessionType.Admin, mainAdmin: true);
        await installation.SeedAsync(owner, SignInModeScene.Account(memberId, "member", SessionType.User, mainAdmin: false));
        var (_, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);

        // The owner signed Steam and Xbox in, the member signed Epic in.
        logins.Seed();
        logins.Epic.SaveAuthData(new EpicAuthData { OwnerAccountId = memberId, RefreshToken = "epic", AccountId = "epic-account" });
        logins.Xbox.SaveAuthData(new XboxAuthData { OwnerAccountId = ownerId, RefreshToken = "xbox", DeviceKeyPkcs8 = "device-key" });
        var deleted = new[] { ownerId, memberId };
        var directories = new (string Service, string Directory)[]
        {
            ("Steam", logins.Storage.GetAuthDirectory()),
            ("Epic", logins.Epic.GetAuthDirectory()),
            ("Xbox", logins.Xbox.GetAuthDirectory())
        };
        string SavedPath(string directory, Guid account) => Path.Combine(directory, "saved", $"{account:N}.json");
        Assert.True(File.Exists(SavedPath(directories[0].Directory, ownerId)), "the owner's Steam login was not saved, so the test proves nothing");

        if (deletion.StartsWith("the switch", StringComparison.Ordinal))
        {
            var saved = await installation.Controller<AccessController>(ownerSession).SetUpAsync(
                UnauthenticatedModeWipesAccountsTests.ConfirmingAccountWipe(new AccessSetupRequest
                {
                    Mode = AccountMode.Unauthenticated,
                    AcknowledgeUnauthenticated = true,
                    ApiKey = installation.ApiKey.GetApiKey()
                }, accountCount: 2));
            Assert.True(saved.Result is OkObjectResult, $"the confirmed switch was answered {(saved.Result as ObjectResult)?.StatusCode}");
        }
        else if (!atStart)
        {
            var wiped = await installation.Controller<AccountsController>(ownerSession).WipeAccountsAsync();
            Assert.True(wiped.Result is OkObjectResult, $"the delete-every-account action was answered {(wiped.Result as ObjectResult)?.StatusCode}");
        }

        await using var restarted = atStart
            ? installation.Restart(new Dictionary<string, string?> { ["Security:SignInMode"] = "unauthenticated" })
            : null;
        if (restarted is not null)
            await restarted.StartAsync();

        await using (var read = await installation.Database.Factory.CreateDbContextAsync())
            Assert.True(!read.UserAccounts.Any(), $"{deletion} left accounts in place, so it never reached the logins");

        var wrong = new List<string>();
        foreach (var (service, directory) in directories)
            foreach (var account in deleted.Where(account => File.Exists(SavedPath(directory, account))))
                wrong.Add($"the saved {service} login of deleted account {(account == ownerId ? "owner" : "member")} is still on disk");
        var steam = logins.Storage.GetAuthData();
        if (steam.OwnerAccountId is { } steamOwner && deleted.Contains(steamOwner) && steam.RefreshToken is not null)
            wrong.Add("the active Steam login still signs prefill in as the deleted owner");
        var epic = logins.Epic.GetAuthData();
        if (epic.OwnerAccountId is { } epicOwner && deleted.Contains(epicOwner) && epic.RefreshToken is not null)
            wrong.Add("the active Epic login still signs prefill in as the deleted member");
        var xbox = logins.Xbox.GetAuthData();
        if (xbox.OwnerAccountId is { } xboxOwner && deleted.Contains(xboxOwner) && xbox.RefreshToken is not null)
            wrong.Add("the active Xbox login still signs prefill in as the deleted owner");
        Assert.True(wrong.Count == 0, $"after {deletion} deleted every account: " + string.Join("; ", wrong));
    }
}
