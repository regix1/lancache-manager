using LancacheManager.Controllers;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Deleting accounts clears each deleted account's saved game logins first, and that clear can fail on a
/// file the app cannot delete. Two deletes must still finish what the person sees: a key rotation that has
/// already changed the key and ended every session still shows the admin the new key, and a start with
/// sign-in off still starts the app.
/// </summary>
// Ending every session touches the process-wide shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SavedLoginClearFailureTests
{
    [Fact]
    public async Task ARotationWhoseSavedLoginClearFails_StillAnswersWithTheNewKeyAsync()
    {
        await using var installation = await SignInModeScene.CreateAsync(new Dictionary<string, string?>(), access =>
        {
            access.Mode = AccountMode.ApiKeyPassword;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var owner = SignInModeScene.Account(installation.Logins.Owner.AccountId!.Value, "owner", SessionType.Admin, mainAdmin: true);
        await installation.SeedAsync(owner, SignInModeScene.Account(installation.Logins.Other.AccountId!.Value, "member", SessionType.User, mainAdmin: false));
        var (_, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);
        var oldKey = installation.ApiKey.GetApiKey();
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

        // The member's saved Steam login cannot be deleted, as when its file is owned by another user.
        installation.Logins.Storage.FailDelete = true;
        IActionResult? answer = null;
        Exception? thrown = null;
        try
        {
            answer = await rotation.RegenerateApiKeyAsync();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        finally
        {
            installation.Logins.Storage.FailDelete = false;
        }

        var newKey = installation.ApiKey.GetApiKey();
        Assert.True(newKey != oldKey, "the rotation did not change the key, so the test proves nothing");
        var wrong = new List<string>();
        if (thrown is not null)
            wrong.Add($"the rotation answered 500 ({thrown.GetType().Name}: {thrown.Message}) after the key changed and every session ended");
        else if ((answer as OkObjectResult)?.Value is not ApiKeyRegenerateResponse { ApiKey: { } shownKey } || shownKey != newKey)
            wrong.Add($"the rotation answered {(answer as ObjectResult)?.StatusCode} without the new key");
        Assert.True(wrong.Count == 0, "when a saved login cannot be cleared during a key rotation, the admin is left without the new key: " + string.Join("; ", wrong));
    }

    [Fact]
    public async Task AStartWithSignInOffWhoseSavedLoginClearFails_StillStartsTheAppAsync()
    {
        await using var installation = await SignInModeScene.CreateAsync(new Dictionary<string, string?>(), access =>
        {
            access.Mode = AccountMode.Unauthenticated;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        // An account left behind by a temporary signed-in mode from Security__SignInMode.
        await installation.SeedAsync(SignInModeScene.Account(installation.Logins.Other.AccountId!.Value, "member", SessionType.User, mainAdmin: false));
        Assert.False(installation.Sessions.IsAuthenticationEnabled(), "the saved mode did not turn sign-in off");

        // The member's saved Steam login cannot be deleted, as when its file is owned by another user.
        installation.Logins.Storage.FailDelete = true;
        Exception? thrown = null;
        try
        {
            await installation.StartAsync();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        finally
        {
            installation.Logins.Storage.FailDelete = false;
        }

        Assert.True(
            thrown is null,
            $"the start-up step threw {thrown?.GetType().Name} ({thrown?.Message}), so app.Run() ends and the app does not start, on every restart");
    }
}
