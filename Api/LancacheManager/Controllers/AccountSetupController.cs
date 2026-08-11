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
    /// (SetupController.cs:94-104): there is no session to require, because this is what a person
    /// does before any account exists to sign in as, and it has to work whether authentication is
    /// enabled or not - an installation that ran with it off still needs a first account the day it
    /// is turned on.
    ///
    /// What is required instead is the API key, and unlike the setup repair endpoints an
    /// authenticated caller is not accepted in its place: with no accounts yet, the only sessions
    /// that can exist are a guest's and an API-key caller's, and a guest must not be able to claim
    /// the installation. [35]
    ///
    /// The key comes out of the body rather than the X-Api-Key header so that narrowing the header
    /// leaves this endpoint working. [32]
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
            // reported as an account that already exists. [37d]
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
        // session. [29c]
        await _identityAuditService.RecordAsync(
            IdentityAuditEvent.AccountCreated,
            performedByAccountId: null,
            performedBySessionId: null,
            targetAccountId: account.Id);

        _logger.LogInformation("First administrator account {Username} created", account.Username);

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
    /// header so that narrowing the header leaves this working. [49b][49f]
    ///
    /// It writes one column. The account is selected by the main-administrator flag rather than by
    /// anything the caller sends, and role and flag are read from the stored row and left alone, so
    /// there is no argument here that promotes anybody, unseats the main administrator, or reaches
    /// another account. [49c]
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
        [FromServices] SessionService sessionService)
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
        // reset and keep the intruder signed in. [49e]
        await sessionService.RevokeAccountSessionsAsync(account.Id);

        // No actor: the caller proved the API key and has neither an account nor a session. [29c]
        await _identityAuditService.RecordAsync(
            IdentityAuditEvent.MainAdminPasswordRecovered,
            performedByAccountId: null,
            performedBySessionId: null,
            targetAccountId: account.Id);

        _logger.LogInformation("Password recovered for main administrator {Username}", account.Username);

        return Ok(MessageResponse.Ok("Password reset"));
    }
}
