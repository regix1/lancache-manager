using System.Text.Json;
using LancacheManager.Controllers;
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
/// The owner confirms a key rotation that promises to delete every other account. When that delete
/// cannot finish, the owner still gets the new key, and the answer also says, by a key both shipped
/// languages translate, that the other accounts were kept. A rotation whose delete finishes carries no
/// such key, so the owner can tell the two apart.
/// </summary>
// Ending every session touches the process-wide shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class RotationKeptAccountsNoticeTests
{
    [Fact]
    public async Task ARotationThatKeepsTheOtherAccounts_SaysSoByKeyAsync()
    {
        await using var installation = await SignInModeScene.CreateAsync(new Dictionary<string, string?>(), access =>
        {
            access.Mode = AccountMode.ApiKeyPassword;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var memberId = installation.Logins.Other.AccountId!.Value;
        var owner = SignInModeScene.Account(installation.Logins.Owner.AccountId!.Value, "owner", SessionType.Admin, mainAdmin: true);
        await installation.SeedAsync(owner, SignInModeScene.Account(memberId, "member", SessionType.User, mainAdmin: false));
        var (_, ownerSession) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);
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

        // The member's saved Steam login cannot be deleted, as when its file is owned by another user,
        // so the rotation cannot delete the member's account.
        installation.Logins.Storage.FailDelete = true;
        IActionResult kept;
        try
        {
            kept = await rotation.RegenerateApiKeyAsync();
        }
        finally
        {
            installation.Logins.Storage.FailDelete = false;
        }
        await using (var read = await installation.Database.Factory.CreateDbContextAsync())
            Assert.True(await read.UserAccounts.AnyAsync(account => account.Id == memberId), "the rotation deleted the member, so the test proves nothing");

        // The same owner rotates again, and this time the delete finishes.
        var cleared = await rotation.RegenerateApiKeyAsync();
        await using (var read = await installation.Database.Factory.CreateDbContextAsync())
            Assert.False(await read.UserAccounts.AnyAsync(account => account.Id == memberId), "the second rotation kept the member, so it is no clean rotation to compare with");

        var keptAnswer = Assert.IsType<OkObjectResult>(kept).Value;
        var clearedAnswer = Assert.IsType<OkObjectResult>(cleared).Value;
        var clearedKeys = TranslatedKeysIn(clearedAnswer).ToHashSet(StringComparer.Ordinal);
        var notice = TranslatedKeysIn(keptAnswer).Where(key => !clearedKeys.Contains(key)).ToList();
        Assert.True(
            notice.Count > 0,
            "a rotation that kept the other accounts answers the owner "
            + JsonSerializer.Serialize(keptAnswer, JsonSerializerOptions.Web)
            + ", which carries no translated key a clean rotation lacks, so the dialog says only that the key was regenerated while the member's account and sign-in links remain");
    }

    /// <summary>Every string in the answer, as the page receives it, that both shipped languages translate.</summary>
    private static IEnumerable<string> TranslatedKeysIn(object? answer)
    {
        var body = JsonSerializer.SerializeToElement(answer, JsonSerializerOptions.Web);
        return StringsIn(body).Where(LocaleKeys.TranslatedInBothLocales);
    }

    private static IEnumerable<string> StringsIn(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => StringsIn(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(StringsIn),
        _ => []
    };
}
