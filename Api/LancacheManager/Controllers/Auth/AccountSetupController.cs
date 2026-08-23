using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/account-setup")]
public class AccountSetupController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ApiKeyService _apiKeyService;
    private readonly AccountClaimWindow _claimWindow;
    private readonly IPasswordHasher<UserAccount> _passwordHasher;
    private readonly IdentityAuditService _identityAuditService;
    private readonly ILogger<AccountSetupController> _logger;

    public AccountSetupController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ApiKeyService apiKeyService,
        AccountClaimWindow claimWindow,
        IPasswordHasher<UserAccount> passwordHasher,
        IdentityAuditService identityAuditService,
        ILogger<AccountSetupController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _apiKeyService = apiKeyService;
        _claimWindow = claimWindow;
        _passwordHasher = passwordHasher;
        _identityAuditService = identityAuditService;
        _logger = logger;
    }

    /// <summary>
    /// Creates the account that owns the installation.
    /// </summary>
    /// <remarks>
    /// Anonymous at the routing layer and never open, the same shape the setup repair endpoints use
    /// through SetupController.RequireApiKeyAsync: there is no session to require, because this is what a person
    /// does before any account exists to sign in as, and it has to work whether authentication is
    /// enabled or not - an installation that ran with it off still needs a first account the day it
    /// is turned on.
    ///
    /// What is required instead is the API key, and unlike the setup repair endpoints an
    /// authenticated caller is not accepted in its place: with no accounts yet, the only sessions
    /// that can exist are a guest's and an API-key caller's, and a guest must not be able to claim
    /// the installation.
    ///
    /// The key comes out of the body rather than the X-Api-Key header so that narrowing the header
    /// leaves this endpoint working.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    // No antiforgery token, because there is nothing here to forge. The API key in the body is the
    // proof, and a page on another origin can neither read it nor guess it. Requiring a token would
    // instead break the first-run claim on an installation that has no account to sign in as yet.
    [IgnoreAntiforgeryToken]
    [HttpPost("first-admin")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> CreateFirstAdminAsync([FromBody] AccountCredentialsRequest request)
    {
        if (!_apiKeyService.ValidateApiKey(request.ApiKey))
        {
            // A missing key and a wrong one are answered identically. Nothing here can tell the two
            // callers apart usefully, and the endpoint is reachable by anyone who can reach the port.
            return StatusCode(
                StatusCodes.Status401Unauthorized,
                new AccountSetupRefusalResponse
                {
                    StageKey = AccountSetupRefusalResponse.ApiKeyRequired,
                    Error = "A valid API key is required"
                });
        }

        if (!_claimWindow.IsOpen)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new AccountSetupRefusalResponse
                {
                    StageKey = AccountSetupRefusalResponse.ClaimWindowClosed,
                    Error = "The window for creating the first account has closed. Restart the application to reopen it."
                });
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // The count is what produces a readable refusal; it is not what makes the creation safe.
        // Two requests can both pass it, which is why the insert below is allowed to be the decider.
        if (await context.UserAccounts.AnyAsync())
        {
            return Conflict(new AccountSetupRefusalResponse
            {
                StageKey = AccountSetupRefusalResponse.AccountExists,
                Error = "An account already exists on this installation"
            });
        }

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = request.Username.Trim(),
            Role = SessionType.Admin,
            IsMainAdmin = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        account.PasswordHash = _passwordHasher.HashPassword(account, request.Password);

        context.UserAccounts.Add(account);

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // IX_UserAccounts_Username and IX_UserAccounts_IsMainAdmin are the only constraints this
            // insert can break (AppDbContext.cs:214-226), and both of them mean a request that
            // passed the same count check committed first. Re-reading is what separates that from a
            // write that failed for some other reason, which has to stay a 500 rather than be
            // reported as an account that already exists.
            if (!await context.UserAccounts.AnyAsync())
            {
                throw;
            }

            _logger.LogWarning(ex, "A concurrent request created the first account; this one was refused");
            return Conflict(new AccountSetupRefusalResponse
            {
                StageKey = AccountSetupRefusalResponse.AccountExists,
                Error = "An account already exists on this installation"
            });
        }

        // No actor: the caller proved the API key and has neither an account nor, necessarily, a
        // session.
        await _identityAuditService.RecordAsync(
            IdentityAuditEvent.AccountCreated,
            performedByAccountId: null,
            performedBySessionId: null,
            targetAccountId: account.Id);

        _logger.LogInformation("First administrator account {Username} created", account.Username);

        // The setup status treats an open window plus an existing account as recovery. Leaving the
        // hour running after this create would send the operator back to the same form to "recover"
        // the password they just set.
        _claimWindow.Expire();

        return Ok(MessageResponse.Ok("Account created"));
    }

    /// <summary>
    /// Sets a new password for the account that owns the installation.
    /// </summary>
    /// <remarks>
    /// The main administrator cannot be deleted, disabled or demoted by anybody, and this
    /// application sends no mail, so a forgotten password has no other way back: without this the
    /// installation is unreachable and the operator's only remedy is the database. The API key is
    /// what proves the caller owns the installation, read from the body rather than the X-Api-Key
    /// header so that narrowing the header leaves this working.
    ///
    /// It writes one column. The account is selected by the main-administrator flag rather than by
    /// anything the caller sends, and role and flag are read from the stored row and left alone, so
    /// there is no argument here that promotes anybody, unseats the main administrator, or reaches
    /// another account.
    ///
    /// SessionService arrives on the action rather than the constructor because only this endpoint
    /// needs it, the way SessionsController.cs:313-314 takes its lookup services.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    // Same reason as first-admin above, and the same consequence: this is the way back in when the
    // password is lost, so it has to work when nothing else does.
    [IgnoreAntiforgeryToken]
    [HttpPost("recover-main-admin")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> RecoverMainAdminPasswordAsync(
        [FromBody] AccountCredentialsRequest request,
        [FromServices] SessionService sessionService,
        [FromServices] AccountLockout accountLockout)
    {
        if (!_apiKeyService.ValidateApiKey(request.ApiKey))
        {
            return StatusCode(
                StatusCodes.Status401Unauthorized,
                new AccountSetupRefusalResponse
                {
                    StageKey = AccountSetupRefusalResponse.ApiKeyRequired,
                    Error = "A valid API key is required"
                });
        }

        // The same window first-admin creation is held to, for the same reason. Every ordinary
        // sign-in needs the key AND a username AND a password (AuthController.cs:259-265); this is
        // the one route that reduces those three to the key alone, so a key that leaked on its own
        // would otherwise be a remote takeover of the account that cannot be deleted or demoted.
        // Requiring the window means recovery also costs a restart, which nobody has without
        // reaching the host the key is stored on.
        if (!_claimWindow.IsOpen)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new AccountSetupRefusalResponse
                {
                    StageKey = AccountSetupRefusalResponse.RecoveryWindowClosed,
                    Error = "The window for recovering the main administrator password has closed. Restart the application to reopen it."
                });
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Trimmed to match how the name was stored (CreateFirstAdminAsync above), so a pasted name
        // carrying a trailing space is the same name rather than an unexplained refusal. Case is
        // left to the column: Username is citext (AppDbContext.cs:209-211), which is what makes the
        // unique index treat "Admin" and "admin" as one person.
        var username = request.Username.Trim();

        var account = await context.UserAccounts
            .SingleOrDefaultAsync(a => a.IsMainAdmin && a.Username == username);

        if (account == null)
        {
            // Either the installation has no main administrator yet or the caller named somebody
            // else. Both are the same answer: this endpoint resets one account and that was not it.
            return NotFound(new AccountSetupRefusalResponse
            {
                StageKey = AccountSetupRefusalResponse.MainAdminNotFound,
                Error = "No main administrator account is registered under that name"
            });
        }

        account.PasswordHash = _passwordHasher.HashPassword(account, request.Password);
        await context.SaveChangesAsync();

        // A password reached this endpoint because somebody lost it, which is also the shape of
        // somebody else having found it. Sessions it already opened would otherwise outlive the
        // reset and keep the intruder signed in.
        await sessionService.RevokeAccountSessionsAsync(account.Id);

        // Getting the password wrong enough times to lock the account is what sends an operator here,
        // and the lock is a separate term from the password in the sign-in refusal, so a new password
        // does not override it. Without this the operator is told "Password reset" and then refused
        // with the same sentence a wrong password gets.
        accountLockout.Clear(account.Id);

        // No actor: the caller proved the API key and has neither an account nor a session.
        await _identityAuditService.RecordAsync(
            IdentityAuditEvent.MainAdminPasswordRecovered,
            performedByAccountId: null,
            performedBySessionId: null,
            targetAccountId: account.Id);

        _logger.LogInformation("Password recovered for main administrator {Username}", account.Username);

        // Same reason as first-admin: GetSetupStatus reports recovery while the window is open and
        // an account exists. A local client clear is overwritten by the next status fetch, which
        // re-opens the wizard and invites another reset of a password that already landed.
        _claimWindow.Expire();

        return Ok(MessageResponse.Ok("Password reset"));
    }
}
