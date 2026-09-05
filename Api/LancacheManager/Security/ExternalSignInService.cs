using System.Security.Cryptography;
using System.Text;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

public sealed class ExternalSignInService
{
    private static readonly SemaphoreSlim _setupLock = new(1, 1);
    private readonly AccessService _accessService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly SessionService _sessionService;

    public ExternalSignInService(
        AccessService accessService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        SessionService sessionService)
    {
        _accessService = accessService;
        _dbContextFactory = dbContextFactory;
        _sessionService = sessionService;
    }

    public async Task<ExternalSignInResult?> TryCreateSessionAsync(
        OidcSettings settings,
        bool owner,
        string issuer,
        string subject,
        HttpContext httpContext)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        UserAccount? account = null;
        Guid accountId;
        if (owner && settings.OwnerAccountId is { } ownerAccountId)
        {
            account = await context.UserAccounts.SingleOrDefaultAsync(candidate => candidate.Id == ownerAccountId);
        }

        if (owner && account is null)
        {
            account = await context.UserAccounts.SingleOrDefaultAsync(candidate => candidate.IsMainAdmin);
        }

        if (owner)
        {
            accountId = account?.Id ?? settings.OwnerAccountId ?? Guid.NewGuid();
        }
        else
        {
            accountId = _accessService.GetOrCreateAccountId(
                settings.Id,
                issuer,
                subject,
                settings.Revision);
            account = await context.UserAccounts.SingleOrDefaultAsync(candidate => candidate.Id == accountId);
        }

        var accountCreated = false;
        if (account is null)
        {
            accountCreated = true;
            var username = await AvailableUsernameAsync(context, issuer, subject, accountId);
            account = new UserAccount
            {
                Id = accountId,
                Username = username,
                Role = owner ? SessionType.Admin : SessionType.User,
                IsMainAdmin = owner,
                CreatedAtUtc = DateTime.UtcNow
            };
            // An external identity has no usable local credential. Keeping this empty lets the
            // access-mode guard distinguish it from a password the owner deliberately established.
            account.PasswordHash = string.Empty;
            context.UserAccounts.Add(account);
        }

        if (account.IsDisabled || (owner && !account.IsMainAdmin))
        {
            return null;
        }

        account.LastLoginAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();

        if (owner && settings.OwnerAccountId != account.Id)
        {
            _accessService.SetOidcOwnerAccount(account.Id, settings.Revision);
        }

        var session = await _sessionService.CreateAccountSessionAsync(httpContext, account);
        return new ExternalSignInResult(
            account.Id,
            session.Session.Id,
            session.RawToken,
            session.Session.ExpiresAtUtc,
            accountCreated);
    }

    public void SetSessionCookie(HttpContext httpContext, ExternalSignInResult signIn)
        => _sessionService.SetSessionCookie(httpContext, signIn.RawToken, signIn.ExpiresAtUtc);

    public async Task CancelAsync(ExternalSignInResult signIn)
    {
        await _sessionService.RevokeSessionAsync(signIn.SessionId);
        if (!signIn.AccountCreated)
        {
            return;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var accountInUse = _accessService.HasOwnerAccount(signIn.AccountId)
            || await context.UserSessions.AnyAsync(session =>
                session.AccountId == signIn.AccountId
                && session.Id != signIn.SessionId
                && !session.IsRevoked);
        var account = accountInUse
            ? null
            : await context.UserAccounts.SingleOrDefaultAsync(candidate => candidate.Id == signIn.AccountId);
        if (account is not null)
        {
            context.UserAccounts.Remove(account);
            await context.SaveChangesAsync();
        }
    }

    public async Task<IDisposable> EnterSetupAsync(CancellationToken cancellationToken)
    {
        await _setupLock.WaitAsync(cancellationToken);
        return new SetupLease();
    }

    public async Task<bool> CompleteSetupAsync(ExternalSignInResult? signIn, Func<bool> promote)
    {
        bool promoted;
        try
        {
            promoted = promote();
        }
        catch
        {
            if (signIn is not null)
            {
                await CancelAsync(signIn);
            }
            throw;
        }

        if (!promoted)
        {
            if (signIn is not null)
            {
                await CancelAsync(signIn);
            }
            return false;
        }

        if (_accessService.IsAuthenticationEnabled())
        {
            await _sessionService.RetireSharedAdminSessionAsync();
        }
        return true;
    }

    private static string Username(string issuer, string subject)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\0{subject}"));
        return $"oidc-{Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static async Task<string> AvailableUsernameAsync(
        AppDbContext context,
        string issuer,
        string subject,
        Guid accountId)
    {
        var username = Username(issuer, subject);
        if (!await context.UserAccounts.AnyAsync(account => account.Username == username))
        {
            return username;
        }

        var reserved = $"oidc-{accountId:N}";
        if (await context.UserAccounts.AnyAsync(account => account.Username == reserved))
        {
            throw new ValidationException("Could not reserve an external account name");
        }

        return reserved;
    }

    private sealed class SetupLease : IDisposable
    {
        public void Dispose() => _setupLock.Release();
    }
}

public sealed record ExternalSignInResult(
    Guid AccountId,
    Guid SessionId,
    string RawToken,
    DateTime ExpiresAtUtc,
    bool AccountCreated);
