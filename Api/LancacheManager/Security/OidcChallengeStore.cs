using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LancacheManager.Security;

public sealed class OidcChallengeStore
{
    private readonly ConcurrentDictionary<string, OidcChallenge> _challenges = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public OidcChallengeStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public string Create(long revision, bool setup, bool owner = false, string loginId = "customOidc")
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var entry in _challenges)
        {
            if (entry.Value.ExpiresAtUtc <= now)
            {
                _challenges.TryRemove(entry);
            }
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _challenges[token] = new OidcChallenge(
            loginId,
            revision,
            setup,
            owner,
            now.AddMinutes(5));
        return token;
    }

    internal int Count => _challenges.Count;

    public OidcChallenge? Take(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_challenges.TryRemove(token, out var challenge))
        {
            return null;
        }

        return challenge.ExpiresAtUtc > _timeProvider.GetUtcNow() ? challenge : null;
    }
}

public sealed record OidcChallenge(
    string LoginId,
    long Revision,
    bool Setup,
    bool Owner,
    DateTimeOffset ExpiresAtUtc);
