namespace LancacheManager.Security;

/// <summary>
/// How long after a start the first administrator account may still be created. An installation
/// that has been running for longer than this has to be restarted before anyone can claim it, so a
/// forgotten instance left on a network does not stay claimable indefinitely.
///
/// The window is a second lock, not the only one: the API key is required as well, and the endpoint
/// refuses the moment an account exists.
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

    /// <summary>
    /// Fixed at construction, which the host does before it starts any hosted service and therefore
    /// before Kestrel accepts a request. A value computed on first use instead would start the hour
    /// at the first call to the endpoint, which is no limit at all.
    /// </summary>
    private readonly DateTime _closesAtUtc = DateTime.UtcNow + _window;

    public AccountClaimWindow(ILogger<AccountClaimWindow> logger)
    {
        _logger = logger;
    }

    public bool IsOpen => DateTime.UtcNow < _closesAtUtc;

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
