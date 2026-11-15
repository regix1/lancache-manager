using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Turning sign-in off first answers with how many accounts it will delete, and the person confirms
/// that number. When an account is created in another browser while that confirm is open, the confirm
/// names a number that is no longer true, so it deletes nothing and is asked again with the new number.
/// The confirm sends the number it showed as <c>confirmedAccountCount</c>.
/// </summary>
// Ending every session touches the process-wide shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class StaleAccountCountConfirmTests
{
    [Fact]
    public async Task AConfirmOfAnOutdatedAccountCount_DeletesNothingAndAsksAgainWithTheNewCountAsync()
    {
        await using var installation = await SignInModeScene.CreateAsync(
            new Dictionary<string, string?>(),
            access =>
            {
                access.Mode = AccountMode.ApiKeyPassword;
                access.SetupVersion = AccessSettings.RequiredSetupVersion;
            });
        var owner = SignInModeScene.Account(Guid.NewGuid(), "owner", SessionType.Admin, mainAdmin: true);
        await installation.SeedAsync(owner, SignInModeScene.Account(Guid.NewGuid(), "member", SessionType.User, mainAdmin: false));
        var (_, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);
        AccessSetupRequest TurningSignInOff() => new()
        {
            Mode = AccountMode.Unauthenticated,
            AcknowledgeUnauthenticated = true,
            ApiKey = installation.ApiKey.GetApiKey()
        };

        // The owner saves unauthenticated mode; the answer asks to confirm deleting the 2 accounts.
        var asked = await installation.Controller<AccessController>(ownerSession).SetUpAsync(TurningSignInOff());
        Assert.Equal(StatusCodes.Status409Conflict, (asked.Result as ObjectResult)?.StatusCode);
        const int shown = 2;

        // Another browser creates an account while the confirm is open; then the owner confirms 2.
        await installation.SeedAsync(SignInModeScene.Account(Guid.NewGuid(), "latecomer", SessionType.User, mainAdmin: false));
        var body = JsonSerializer.SerializeToNode(TurningSignInOff(), JsonSerializerOptions.Web)!.AsObject();
        body["confirmedAccountCount"] = shown;
        var confirmed = await installation.Controller<AccessController>(ownerSession)
            .SetUpAsync(body.Deserialize<AccessSetupRequest>(JsonSerializerOptions.Web)!);

        var wrong = new List<string>();
        var answer = confirmed.Result as ObjectResult;
        if (answer?.StatusCode != StatusCodes.Status409Conflict)
            wrong.Add($"the confirm of {shown} accounts was answered {answer?.StatusCode ?? StatusCodes.Status200OK}, not asked again");
        else if (answer.Value?.GetType().GetProperty("Context")?.GetValue(answer.Value) is not IDictionary<string, object> context
            || !context.TryGetValue("count", out var count)
            || !Equals(count, 3))
            wrong.Add("the confirm was asked again without the new count of 3 accounts");
        await using var read = await installation.Database.Factory.CreateDbContextAsync();
        var left = await read.UserAccounts.Select(account => account.Username).OrderBy(name => name).ToListAsync();
        if (left.Count != 3)
            wrong.Add($"accounts left: {left.Count} ({string.Join(", ", left)}), the latecomer the confirm never counted among the deleted");
        Assert.True(wrong.Count == 0, "an account created while the confirm was open: " + string.Join("; ", wrong));
    }
}
