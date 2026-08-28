using FluentValidation;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using LancacheManager.Validators;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// Reads and writes the accounts people sign in with.
/// </summary>
/// <remarks>
/// Three rules run through every action here, and each of them is enforced against the caller's own
/// account row rather than against the claims on their cookie. The row is the current answer; a claim
/// is a copy taken when the session was minted.
///
/// A user sees and reaches only accounts that are not administrators, and an administrator who is
/// not the owner does not see or reach the owner. Both are enforced by the query every action loads
/// its target through, so an account a caller cannot see is also an account they cannot name by id.
///
/// The account that owns the installation cannot be deleted, disabled, demoted or edited, by anybody
/// including itself. The wipe action is the exception: only that account can run it, and it
/// deletes every row including its own.
///
/// Only that account hands out the administrator role.
/// </remarks>
[ApiController]
[Route("api/accounts")]
[Authorize(Policy = "AccountHolder")]
public class AccountsController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IPasswordHasher<UserAccount> _passwordHasher;
    private readonly SessionService _sessionService;
    private readonly IdentityAuditService _identityAuditService;
    private readonly AccountLockout _accountLockout;
    private readonly AccountClaimWindow _claimWindow;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<AccountsController> _logger;

    public AccountsController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IPasswordHasher<UserAccount> passwordHasher,
        SessionService sessionService,
        IdentityAuditService identityAuditService,
        AccountLockout accountLockout,
        AccountClaimWindow claimWindow,
        ISignalRNotificationService notifications,
        ILogger<AccountsController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _passwordHasher = passwordHasher;
        _sessionService = sessionService;
        _identityAuditService = identityAuditService;
        _accountLockout = accountLockout;
        _claimWindow = claimWindow;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Lists the accounts the caller may see: every account for the owner, every account except the
    /// owner for another administrator, and every account that is not an administrator for a user.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<AccountResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<AccountResponse>>> GetAccountsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var caller = await LoadCallerAccountAsync(context, HttpContext.GetUserSession());

        var accounts = await MainAdminVisibility.AccountsVisibleTo(context, caller)
            .OrderBy(a => a.Username)
            .ToListAsync();

        return Ok(accounts.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Reads one account, through the same query the list is built from. An account the caller may
    /// not see answers as one that does not exist.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountResponse>> GetAccountAsync(Guid id)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var caller = await LoadCallerAccountAsync(context, HttpContext.GetUserSession());

        var account = await MainAdminVisibility.AccountsVisibleTo(context, caller).FirstOrDefaultAsync(a => a.Id == id);
        if (account == null)
        {
            return NotFoundAccount();
        }

        return Ok(ToResponse(account));
    }

    /// <summary>
    /// Creates an account.
    /// </summary>
    /// <remarks>
    /// Asking for the administrator role is the half of role assignment that does not go through
    /// <see cref="SetRoleAsync"/>: without it a user creates an admin instead of promoting one, and
    /// the distinction between the two roles is decorative.
    ///
    /// Throttled because it takes a password, which is the coverage rule every route that accepts a
    /// secret is held to.
    /// </remarks>
    [HttpPost]
    [EnableRateLimiting("accounts")]
    [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<AccountResponse>> CreateAccountAsync([FromBody] CreateAccountRequest request)
    {
        if (request.Role == SessionType.Guest)
        {
            return RoleNotAssignable();
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var session = HttpContext.GetUserSession();
        var caller = await LoadCallerAccountAsync(context, session);

        if (request.Role == SessionType.Admin && !CallerMayGrantAdmin(caller))
        {
            return AdminRoleRefused();
        }

        // The rules the first account was created under, run against this one, so an account made
        // here cannot hold a password that could not have been chosen at setup. The single validator
        // is the one place those rules live.
        var credentials = new AccountCredentialsRequest
        {
            Username = request.Username.Trim(),
            Password = request.Password
        };

        var validated = new AccountCredentialsRequestValidator().Validate(credentials);
        if (!validated.IsValid)
        {
            return CredentialsRefused(validated.Errors[0].ErrorMessage);
        }

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = credentials.Username,
            Role = request.Role,
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
            // IX_UserAccounts_Username is the only constraint this insert can break, nothing here
            // setting IsMainAdmin (AppDbContext.cs:214-226). Re-reading is what separates a name that
            // is taken from a write that failed for some other reason, which has to stay a 500.
            if (!await context.UserAccounts.AnyAsync(a => a.Username == credentials.Username))
            {
                throw;
            }

            _logger.LogWarning(ex, "Account {Username} was not created: the name is already taken", credentials.Username);
            return Conflict(new AccountRefusalResponse
            {
                StageKey = AccountRefusalResponse.UsernameTaken,
                Error = "That username is already taken"
            });
        }

        await RecordAsync(IdentityAuditEvent.AccountCreated, caller, session, account.Id);

        _logger.LogInformation("Account {Username} created with the {Role} role", account.Username, account.Role);

        // Another account holder's accounts table is loaded once and never asked again, so without
        // this it shows the list as it was before this write until that person reloads the page.
        await _notifications.NotifyAllAsync(SignalREvents.AccountsChanged);

        return Created($"/api/accounts/{account.Id}", ToResponse(account));
    }

    /// <summary>
    /// Renames an account and, when the request carries one, sets a new password on it.
    /// </summary>
    /// <remarks>
    /// This is the only way back into an account that is not the main administrator: the recovery
    /// endpoint resets that one account and no other, and there is no mail server to send a link
    /// with. The sessions the old password opened are ended for the same reason recovery ends them -
    /// a password is replaced because somebody lost it, which is also the shape of somebody else
    /// having found it.
    ///
    /// The main administrator is refused here as well as on role change, disable and delete. An
    /// administrator who could set that account's password could sign in as it and hand out the
    /// administrator role, which is the escalation those refusals exist to close.
    ///
    /// Throttled because it takes a password.
    /// </remarks>
    [HttpPut("{id:guid}")]
    [EnableRateLimiting("accounts")]
    [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountResponse>> EditAccountAsync(Guid id, [FromBody] EditAccountRequest request)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var session = HttpContext.GetUserSession();
        var caller = await LoadCallerAccountAsync(context, session);

        var account = await MainAdminVisibility.AccountsVisibleTo(context, caller).FirstOrDefaultAsync(a => a.Id == id);
        if (account == null)
        {
            return NotFoundAccount();
        }

        if (account.IsMainAdmin)
        {
            return MainAdminRefused();
        }

        var credentials = new AccountCredentialsRequest
        {
            Username = request.Username.Trim(),
            Password = request.Password ?? string.Empty
        };

        // A request that leaves the password alone is not made to restate one, so the password rules
        // are run only against a password the caller actually sent.
        var validator = new AccountCredentialsRequestValidator();
        var validated = request.Password == null
            ? validator.Validate(credentials, options => options.IncludeProperties(c => c.Username))
            : validator.Validate(credentials);

        if (!validated.IsValid)
        {
            return CredentialsRefused(validated.Errors[0].ErrorMessage);
        }

        account.Username = credentials.Username;
        if (request.Password != null)
        {
            account.PasswordHash = _passwordHasher.HashPassword(account, request.Password);
        }

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            if (!await context.UserAccounts.AnyAsync(a => a.Username == credentials.Username && a.Id != account.Id))
            {
                throw;
            }

            _logger.LogWarning(ex, "Account {AccountId} was not renamed: the name is already taken", account.Id);
            return Conflict(new AccountRefusalResponse
            {
                StageKey = AccountRefusalResponse.UsernameTaken,
                Error = "That username is already taken"
            });
        }

        if (request.Password != null)
        {
            await _sessionService.RevokeAccountSessionsAsync(account.Id);

            // Somebody who locked themselves out is the reason an administrator sets a password for
            // them, and the lock is a separate term from the password in the sign-in refusal, so the
            // count of failures has to go with the password that produced them.
            _accountLockout.Clear(account.Id);

            // The caller is the actor and the account is the target, which is what separates this row
            // from the one a person changing their own password writes. Without it the trail cannot
            // answer who could have signed in as this account.
            await RecordAsync(IdentityAuditEvent.PasswordChanged, caller, session, account.Id);
        }

        _logger.LogInformation("Account {AccountId} edited", account.Id);

        await _notifications.NotifyAllAsync(SignalREvents.AccountsChanged);

        return Ok(ToResponse(account));
    }

    /// <summary>
    /// Moves an account onto a role.
    /// </summary>
    /// <remarks>
    /// Promoting anybody to administrator is the main administrator's alone, and promoting yourself
    /// runs through this same check rather than a case of its own: a rule that reads the caller's row
    /// needs no exception for the caller being the target.
    ///
    /// The main administrator cannot be moved off the administrator role, which is what makes
    /// refusing to delete it worth anything: demoting it and then deleting it would otherwise reach
    /// the same place.
    /// </remarks>
    [HttpPut("{id:guid}/role")]
    [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountResponse>> SetRoleAsync(Guid id, [FromBody] SetAccountRoleRequest request)
    {
        if (request.Role == SessionType.Guest)
        {
            return RoleNotAssignable();
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var session = HttpContext.GetUserSession();
        var caller = await LoadCallerAccountAsync(context, session);

        var account = await MainAdminVisibility.AccountsVisibleTo(context, caller).FirstOrDefaultAsync(a => a.Id == id);
        if (account == null)
        {
            return NotFoundAccount();
        }

        if (account.IsMainAdmin)
        {
            return MainAdminRefused();
        }

        if (request.Role == SessionType.Admin && !CallerMayGrantAdmin(caller))
        {
            return AdminRoleRefused();
        }

        if (account.Id == caller?.Id)
        {
            return SelfRefused();
        }

        account.Role = request.Role;
        await context.SaveChangesAsync();

        // The session carries its own copy of the role, so without this the person keeps the role
        // they had until they sign out, and an account holder's session does not expire.
        await _sessionService.RevokeAccountSessionsAsync(account.Id);

        await RecordAsync(IdentityAuditEvent.RoleChanged, caller, session, account.Id);

        _logger.LogInformation("Account {AccountId} moved to the {Role} role", account.Id, account.Role);

        await _notifications.NotifyAllAsync(SignalREvents.AccountsChanged);

        return Ok(ToResponse(account));
    }

    /// <summary>
    /// Turns an account off, or back on.
    /// </summary>
    /// <remarks>
    /// One action for both directions because they are one column, and because an installation that
    /// can disable an account and not re-enable it has locked somebody out permanently by mistake.
    ///
    /// Disabling ends the account's live sessions rather than waiting for them to expire. Session
    /// validation reads the flag too, so a session survives the disable by no more than its next
    /// request either way, but leaving the row live would leave it counted and shown as signed in.
    /// </remarks>
    [HttpPut("{id:guid}/disabled")]
    [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountResponse>> SetDisabledAsync(Guid id, [FromBody] SetAccountDisabledRequest request)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var session = HttpContext.GetUserSession();
        var caller = await LoadCallerAccountAsync(context, session);

        var account = await MainAdminVisibility.AccountsVisibleTo(context, caller).FirstOrDefaultAsync(a => a.Id == id);
        if (account == null)
        {
            return NotFoundAccount();
        }

        if (account.IsMainAdmin)
        {
            return MainAdminRefused();
        }

        if (account.Id == caller?.Id)
        {
            return SelfRefused();
        }

        account.IsDisabled = request.Disabled;
        await context.SaveChangesAsync();

        if (request.Disabled)
        {
            await _sessionService.RevokeAccountSessionsAsync(account.Id);
        }

        await RecordAsync(
            request.Disabled ? IdentityAuditEvent.AccountDisabled : IdentityAuditEvent.AccountEnabled,
            caller,
            session,
            account.Id);

        _logger.LogInformation(
            "Account {AccountId} was {State}", account.Id, request.Disabled ? "disabled" : "enabled");

        await _notifications.NotifyAllAsync(SignalREvents.AccountsChanged);

        return Ok(ToResponse(account));
    }

    /// <summary>
    /// Deletes an account.
    /// </summary>
    /// <remarks>
    /// The row goes and the sessions it opened are revoked. Session validation rejects a session
    /// whose account is gone, so the revocation is not what makes the person signed out; it is what
    /// stops the rows sitting in the session list as live.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> DeleteAccountAsync(Guid id)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var session = HttpContext.GetUserSession();
        var caller = await LoadCallerAccountAsync(context, session);

        var account = await MainAdminVisibility.AccountsVisibleTo(context, caller).FirstOrDefaultAsync(a => a.Id == id);
        if (account == null)
        {
            return NotFoundAccount();
        }

        if (account.IsMainAdmin)
        {
            return MainAdminRefused();
        }

        if (account.Id == caller?.Id)
        {
            return SelfRefused();
        }

        context.UserAccounts.Remove(account);
        await context.SaveChangesAsync();

        await _sessionService.RevokeAccountSessionsAsync(account.Id);

        await RecordAsync(IdentityAuditEvent.AccountDeleted, caller, session, account.Id);

        _logger.LogInformation("Account {AccountId} deleted", account.Id);

        await _notifications.NotifyAllAsync(SignalREvents.AccountsChanged);

        return Ok(MessageResponse.Ok("Account deleted"));
    }

    /// <summary>
    /// Deletes every account, including the one that owns the installation, and ends every session.
    /// </summary>
    /// <remarks>
    /// Single-account delete refuses the main administrator so that row cannot vanish by accident.
    /// This is the explicit exception: the owner is emptying the table so the first-admin wizard can
    /// run again. Anyone who is not that owner is refused with its own message and stage key, because
    /// a refusal that talks about granting the administrator role names an action the person did not
    /// take. A missing account row — API key only, or authentication
    /// off — is not treated as a grant, because <see cref="CallerMayGrantAdmin"/> would admit those
    /// callers.
    ///
    /// Each removed row is recorded as <see cref="IdentityAuditEvent.AccountDeleted"/> while the
    /// caller's session still exists, then every session is deleted rather than revoked one account
    /// at a time. The claim window is opened again so first-admin creation does not wait for a
    /// restart on a process that has already been up longer than the hour.
    /// </remarks>
    [HttpPost("wipe")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> WipeAccountsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var session = HttpContext.GetUserSession();
        var caller = await LoadCallerAccountAsync(context, session);

        if (caller?.IsMainAdmin != true)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new AccountRefusalResponse
                {
                    StageKey = AccountRefusalResponse.WipeRequiresMainAdmin,
                    Error = "Only the account that owns this installation can delete every account"
                });
        }

        var accounts = await context.UserAccounts.ToListAsync();
        context.UserAccounts.RemoveRange(accounts);
        await context.SaveChangesAsync();
        // The table is already empty. Open the hour before audit or session-clear can throw, or
        // first-admin stays refused until a restart.
        _claimWindow.Reopen();

        foreach (var removed in accounts)
        {
            await RecordAsync(IdentityAuditEvent.AccountDeleted, caller, session, removed.Id);
        }

        await _sessionService.ClearAllSessionsAsync();

        _logger.LogWarning("Every account was deleted, including the one that owns the installation");

        // One broadcast for the whole table rather than one per removed row: the list is reloaded
        // whole, and the rows all went in a single write.
        await _notifications.NotifyAllAsync(SignalREvents.AccountsChanged);

        return Ok(MessageResponse.Ok("Accounts deleted"));
    }

    /// <summary>
    /// Whether the caller may hand out the administrator role, read from the caller's stored account
    /// row.
    /// </summary>
    /// <remarks>
    /// The claim on the cookie says "admin" for every administrator, so a check that read it would
    /// admit all of them and the rule would mean nothing.
    ///
    /// A caller with no account row is answered by the authentication setting: with it off there is
    /// no access control to contradict, and with it on the API key's way to a first administrator is
    /// the create-first-admin endpoint rather than a standing right to mint more.
    /// </remarks>
    private bool CallerMayGrantAdmin(UserAccount? caller)
    {
        return caller?.IsMainAdmin ?? !_sessionService.IsAuthenticationEnabled();
    }

    private static async Task<UserAccount?> LoadCallerAccountAsync(AppDbContext context, UserSession? session)
    {
        return session?.AccountId is { } accountId
            ? await context.UserAccounts.FirstOrDefaultAsync(a => a.Id == accountId)
            : null;
    }

    /// <summary>
    /// Records the change against the caller. Both actor columns are null for a caller that has
    /// neither an account nor a session row, and a failed write is swallowed inside the audit service
    /// so that a logging fault cannot undo an account change that already committed.
    /// </summary>
    private Task RecordAsync(
        IdentityAuditEvent auditEvent, UserAccount? caller, UserSession? session, Guid targetAccountId)
    {
        return _identityAuditService.RecordAsync(auditEvent, caller?.Id, session?.Id, targetAccountId);
    }

    private static AccountResponse ToResponse(UserAccount account)
    {
        return new AccountResponse
        {
            Id = account.Id,
            Username = account.Username,
            Role = account.Role,
            IsMainAdmin = account.IsMainAdmin,
            IsDisabled = account.IsDisabled,
            CreatedAtUtc = DateTime.SpecifyKind(account.CreatedAtUtc, DateTimeKind.Utc),
            LastLoginAtUtc = account.LastLoginAtUtc is { } lastLogin
                ? DateTime.SpecifyKind(lastLogin, DateTimeKind.Utc)
                : null
        };
    }

    private NotFoundObjectResult NotFoundAccount()
    {
        return NotFound(new AccountRefusalResponse
        {
            StageKey = AccountRefusalResponse.AccountNotFound,
            Error = "No such account"
        });
    }

    private ObjectResult MainAdminRefused()
    {
        return StatusCode(
            StatusCodes.Status403Forbidden,
            new AccountRefusalResponse
            {
                StageKey = AccountRefusalResponse.MainAdminProtected,
                Error = "The account that owns this installation cannot be changed here"
            });
    }

    private ObjectResult AdminRoleRefused()
    {
        return StatusCode(
            StatusCodes.Status403Forbidden,
            new AccountRefusalResponse
            {
                StageKey = AccountRefusalResponse.AdminRoleRequiresMainAdmin,
                Error = "Only the account that owns this installation can grant the administrator role"
            });
    }

    /// <summary>
    /// Refuses a change the caller is making to its own account. Delete, disable and set-role all end
    /// the caller's own sessions, and none of the three can be put back by the person who did it:
    /// re-creating an account and granting the admin role both belong to the account that owns the
    /// installation. Renaming and setting a password on your own account are not refused here.
    /// </summary>
    private ObjectResult SelfRefused()
    {
        return StatusCode(
            StatusCodes.Status403Forbidden,
            new AccountRefusalResponse
            {
                StageKey = AccountRefusalResponse.SelfProtected,
                Error = "You cannot delete, disable or change the role of the account you are signed in as"
            });
    }

    private BadRequestObjectResult RoleNotAssignable()
    {
        return BadRequest(new AccountRefusalResponse
        {
            StageKey = AccountRefusalResponse.RoleNotAssignable,
            Error = "An account can hold the administrator or the user role"
        });
    }

    private BadRequestObjectResult CredentialsRefused(string error)
    {
        return BadRequest(new AccountRefusalResponse
        {
            StageKey = AccountRefusalResponse.CredentialsRejected,
            Error = error
        });
    }
}
