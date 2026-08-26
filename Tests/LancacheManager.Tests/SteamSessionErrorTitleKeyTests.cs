using System.Text.Json;
using LancacheManager.Core.Services.SteamKit2;

namespace LancacheManager.Tests;

// The SteamSessionError toast renders its title straight from the titleStageKey the backend sends,
// and the frontend i18n key check only scans .ts/.tsx files. A key typed wrong in the C# switch
// would ship as a missing translation with every gate green, so each key the mapping can return is
// resolved here against both shipped locale files.
public class SteamSessionErrorTitleKeyTests
{
    // Every errorType that reaches SessionErrorTitleKey: the logon switch in
    // SteamKit2Service.Connection.cs feeds the SteamLogonException call site, the logoff switch
    // feeds the disconnect call site, and ConnectionFailed is the literal the reconnect-failed call
    // site passes. AutoLogout has no emitter but is a switch arm, so it is covered like the rest.
    [Theory]
    [InlineData("SessionReplaced")]
    [InlineData("LoggedInElsewhere")]
    [InlineData("AutoLogout")]
    [InlineData("InvalidCredentials")]
    [InlineData("AuthenticationRequired")]
    [InlineData("SessionExpired")]
    [InlineData("ServerUnavailable")]
    [InlineData("ServiceUnavailable")]
    [InlineData("RateLimited")]
    [InlineData("LoginFailed")]
    [InlineData("ConnectionFailed")]
    public void SessionErrorTitleKeyResolvesInBothLocales(string errorType)
    {
        var root = FindRepositoryRoot();
        using var english = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Web", "src", "i18n", "locales", "en.json")));
        using var chinese = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Web", "src", "i18n", "locales", "zh.json")));

        var key = SteamKit2Service.SessionErrorTitleKey(errorType);

        Assert.False(string.IsNullOrWhiteSpace(Resolve(english.RootElement, key)), $"en.json lacks {key}");
        Assert.False(string.IsNullOrWhiteSpace(Resolve(chinese.RootElement, key)), $"zh.json lacks {key}");
    }

    private static string? Resolve(JsonElement root, string key)
    {
        var current = root;
        foreach (var segment in key.Split('.'))
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }
}
