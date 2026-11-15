using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Tests;

/// <summary>
/// Every browser on an iPhone or iPad is built on Safari's engine, so its agent ends in "Safari/" and
/// never says "Chrome/" or "Firefox/". Chrome names itself "CriOS/" and Firefox "FxiOS/", and the
/// prefill session list has to show those names rather than Safari.
/// </summary>
public sealed class UserAgentIosBrowserTests
{
    [Theory]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/124.0.6367.88 Mobile/15E148 Safari/604.1",
        "Chrome")]
    [InlineData(
        "Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/124.0.6367.88 Mobile/15E148 Safari/604.1",
        "Chrome")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/125.0 Mobile/15E148 Safari/605.1.15",
        "Firefox")]
    [InlineData(
        "Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/125.0 Mobile/15E148 Safari/605.1.15",
        "Firefox")]
    public void IosBrowserReadsAsItself(string userAgent, string expectedBrowser)
    {
        var (os, browser) = UserAgentParser.Parse(userAgent);

        Assert.StartsWith("iOS", os);
        Assert.NotNull(browser);
        Assert.True(
            browser!.StartsWith(expectedBrowser, StringComparison.Ordinal),
            $"{expectedBrowser} on iOS reads as \"{browser}\"");
    }
}
