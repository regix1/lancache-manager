namespace LancacheManager.Security;

/// <summary>
/// How long after a start the main administrator account may be claimed. Password recovery uses
/// the same deadline, but it is not offered until the host script explicitly requests it. An
/// installation that has been running for longer than this has to be restarted first, so a
/// forgotten instance left on a network does not stay claimable indefinitely.
///
/// The window is a second lock, not the only one: the API key is required as well, and first-admin
/// creation refuses the moment an account exists.
///
/// It guards password recovery for a sharper reason. An ordinary sign-in needs the key, a username
/// and a password together; recovery is the one route that accepts the key by itself. Without this
/// window a key that leaked on its own would be a remote takeover of the one account that cannot be
/// deleted or demoted. Recovery therefore needs both a recent restart and an explicit request from
/// the host script. A routine restart alone must never replace sign-in with the recovery screen.
/// </summary>
public class AccountClaimWindow : IHostedService
{
    /// <summary>
    /// One hour, measured from the start rather than from the moment the operator reaches the
    /// account step.
    ///
    /// Starting the clock at the account step would need the client to tell the server it had got
    /// there, which is a request anyone can send and so is no clock at all. Making the window long
    /// enough to hold a whole wizard run is the version that does not depend on the caller. An hour
    /// covers it with room to spare: the slowest path through setup is submitting database
    /// credentials, and that one ends in a restart anyway
    /// (SetupController.cs:233 tells the operator to restart the container), which opens a fresh
    /// window.
    /// </summary>
    private static readonly TimeSpan _window = TimeSpan.FromHours(1);

    private readonly ILogger<AccountClaimWindow> _logger;
    private bool _recoveryRequested;

    /// <summary>
    /// Fixed at construction, which the host does before it starts any hosted service and therefore
    /// before Kestrel accepts a request. A value computed on first use instead would start the hour
    /// at the first call to the endpoint, which is no limit at all. Wipe resets it through
    /// <see cref="Reopen"/> so the owner who just emptied the table can create the first account
    /// without restarting a process that has already been up longer than the hour.
    /// </summary>
    private DateTime _closesAtUtc = DateTime.UtcNow + _window;

    public AccountClaimWindow(ILogger<AccountClaimWindow> logger)
    {
        _logger = logger;
    }

    public bool IsOpen => DateTime.UtcNow < _closesAtUtc;

    public bool IsRecoveryOpen => IsOpen && _recoveryRequested;

    /// <summary>
    /// Offers main-administrator recovery for the remainder of the post-start claim window. This is
    /// called only after the recovery script proves the API key. Keeping it separate from startup is
    /// what stops an ordinary container restart from opening the recovery screen.
    /// </summary>
    public bool OpenRecovery()
    {
        if (!IsOpen)
        {
            return false;
        }

        _recoveryRequested = true;
        _logger.LogInformation(
            "Main administrator password recovery is available until {ClosesAtUtc:u}",
            _closesAtUtc);
        return true;
    }

    /// <summary>
    /// Starts a new hour from now. First-admin creation checks this window, not whether the table is
    /// empty, so a wipe on a long-running process would otherwise leave the owner on that screen
    /// with every submit refused until they restart.
    /// </summary>
    public void Reopen()
    {
        _closesAtUtc = DateTime.UtcNow + _window;
        _recoveryRequested = false;
        _logger.LogInformation(
            "First administrator account can be created until {ClosesAtUtc:u}; a restart reopens the window",
            _closesAtUtc);
    }

    /// <summary>
    /// Shuts the window immediately, the same state a process reaches once the hour has elapsed.
    /// </summary>
    internal void Expire()
    {
        _closesAtUtc = DateTime.UtcNow;
        _recoveryRequested = false;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The operator needs to know when it shuts, because the endpoint's refusal afterwards looks
        // the same as a wrong key from the outside.
        _logger.LogInformation(
            "First administrator account can be created until {ClosesAtUtc:u}; a restart reopens the window",
            _closesAtUtc);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
