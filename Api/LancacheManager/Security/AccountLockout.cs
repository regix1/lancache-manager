using System.Collections.Concurrent;

namespace LancacheManager.Security;

/// <summary>
/// Counts failed password attempts per account and refuses the account once there have been too many
/// inside the observation window.
///
/// This is the guess-rate limit that the per-IP limiter cannot be: that one throttles a single caller,
/// so a password attack spread over a botnet passes it untouched while every request lands on the same
/// account. This counter follows the account instead of the caller.
///
/// Only an attempt that named a real account is counted. A wrong API key and an unknown username
/// cannot be attributed to anyone, so they are the per-IP limiter's to bound and reach none of this.
///
/// State is per process and is not persisted: a restart clears every counter, which is the same
/// tradeoff the rate limiter makes, and the operator who can restart the container can read the API
/// key off disk anyway.
/// </summary>
public class AccountLockout
{
    /// <summary>
    /// Attempts allowed inside <see cref="_window"/> before the account is refused. Few enough to stop
    /// online guessing, generous enough that somebody typing a password they half-remember does not
    /// lock themselves out on a bad morning.
    /// </summary>
    private const int MaxAttempts = 5;

    /// <summary>
    /// How long failures are remembered, and how long a locked account stays locked. Both, because the
    /// window runs from the last counted failure, and an attempt made while the account is already
    /// locked is refused without being counted: the sign-in path checks the lock before it counts, and
    /// the change-password path answers a locked account ahead of its own count. So the account reopens
    /// this long after the <see cref="MaxAttempts"/>th counted failure however hard the guessing carries
    /// on in the meantime.
    ///
    /// Not extending on every attempt is the deliberate half. A window that did would hand anybody who
    /// knows a username a way to hold its owner out of their own account for as long as they cared to
    /// keep typing.
    /// </summary>
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, AccountAttempts> _attempts = new();
    private readonly ILogger<AccountLockout> _logger;

    public AccountLockout(ILogger<AccountLockout> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// True while the account has had too many recent failures. Callers answer a locked account exactly
    /// as they answer a wrong password, so this never appears in a response on its own.
    /// </summary>
    public bool IsLocked(Guid accountId)
    {
        if (!_attempts.TryGetValue(accountId, out var attempts))
        {
            return false;
        }

        lock (attempts)
        {
            // A window that has run out is the same as no record at all. Dropping the entry here is
            // also what keeps the dictionary from growing for accounts that failed once months ago,
            // since every account that is ever checked passes through this line.
            if (DateTime.UtcNow >= attempts.WindowEndsAtUtc)
            {
                _attempts.TryRemove(accountId, out _);
                return false;
            }

            return attempts.Count >= MaxAttempts;
        }
    }

    /// <summary>
    /// Records one failed password attempt against an account, whether it came from the login endpoint
    /// or from the current-password check on the change-password endpoint. Counting both is what stops
    /// an attacker holding any live session from guessing against the endpoint that is not counted.
    /// </summary>
    public void RecordFailure(Guid accountId)
    {
        var now = DateTime.UtcNow;
        var attempts = _attempts.GetOrAdd(accountId, _ => new AccountAttempts());

        lock (attempts)
        {
            if (now >= attempts.WindowEndsAtUtc)
            {
                attempts.Count = 0;
            }

            attempts.Count++;
            attempts.WindowEndsAtUtc = now + _window;

            if (attempts.Count == MaxAttempts)
            {
                _logger.LogWarning(
                    "Account {AccountId} locked after {Count} failed password attempts; it unlocks at {UnlocksAtUtc:u}",
                    accountId, attempts.Count, attempts.WindowEndsAtUtc);
            }
        }
    }

    /// <summary>
    /// Forgets an account's failures. Called on a successful sign-in, so the count only ever reflects a
    /// run of failures rather than a tally of every mistype since the process started.
    /// </summary>
    public void Clear(Guid accountId) => _attempts.TryRemove(accountId, out _);

    private sealed class AccountAttempts
    {
        public int Count { get; set; }

        public DateTime WindowEndsAtUtc { get; set; }
    }
}
