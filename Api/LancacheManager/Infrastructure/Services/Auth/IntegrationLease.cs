using LancacheManager.Infrastructure.Data;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>Holds one integration's admission gate through a credential dispatch or release.</summary>
public sealed class IntegrationLease : IDisposable, IAsyncDisposable
{
    private Action? _release;
    private readonly Action _validate;
    internal object Store { get; }
    internal long Generation { get; }
    public IntegrationCaller? Caller { get; }

    internal IntegrationLease(object store, long generation, IntegrationCaller? caller, Action validate, Action release)
    {
        Store = store;
        Generation = generation;
        Caller = caller;
        _validate = validate;
        _release = release;
    }

    public void Validate()
    {
        ObjectDisposedException.ThrowIf(_release is null, this);
        _validate();
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public static async Task<IntegrationCaller> ResolveCallerAsync(HttpContext context)
    {
        var session = context.GetUserSession();
        var access = context.RequestServices.GetService<AccessService>();
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var authenticationEnabled = access?.IsAuthenticationEnabled()
            ?? configuration.GetValue<bool>("Security:EnableAuthentication", true);
        var ownsInstallation = false;
        if (authenticationEnabled && session?.AccountId is { } accountId)
        {
            var contexts = context.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var accounts = await contexts.CreateDbContextAsync(context.RequestAborted);
            ownsInstallation = await MainAdminVisibility.OwnsInstallationAsync(accounts, accountId, context.RequestAborted);
        }
        return new(authenticationEnabled ? session?.AccountId : null, authenticationEnabled ? session?.Id : null, authenticationEnabled, ownsInstallation);
    }

    public static void Refuse(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Integration access was refused without a reason.");

        var stageKey = reason switch
        {
            "account-required" => "errors.integration.accountRequired",
            "owned-by-another-account" => "errors.integration.ownedByAnotherAccount",
            "login-in-progress" => "errors.integration.loginInProgress",
            "reauthentication-required" => "errors.integration.reauthenticationRequired",
            "release-in-progress" => "errors.integration.releaseInProgress",
            "attempt-required" => "errors.integration.attemptRequired",
            "attempt-expired" => "errors.integration.attemptExpired",
            "main-owner-required" => "errors.integration.mainOwnerRequired",
            "integration-sign-in-required" => "errors.integration.signInRequired",
            "no-saved-login" => "errors.integration.noSavedLogin",
            "not-supported" => "errors.integration.notSupported",
            _ => throw new InvalidOperationException("Integration access was refused with an unrecognized reason.")
        };
        if (reason is "account-required" or "owned-by-another-account" or "main-owner-required")
            throw new ForbiddenException(reason) { StageKey = stageKey };
        throw new ConflictException(reason) { StageKey = stageKey };
    }

    public static void ValidateCaller(IntegrationLogin login, IntegrationCaller caller)
    {
        if (!caller.AuthenticationEnabled) caller = new(null, null, false);
        if (caller.AccountId != login.AccountId || caller.SessionId != login.SessionId
            || caller.AuthenticationEnabled == login.Shared)
            Refuse("owned-by-another-account");
    }
}
