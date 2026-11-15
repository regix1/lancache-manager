using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Tests;

/// <summary>
/// The session list names each device from its user agent. Phone agents also name a desktop system
/// ("like Mac OS X" on an iPhone, "Linux" on Android), so the phone has to be recognized first.
/// </summary>
public sealed class UserAgentParserTests
{
    [Fact]
    public void IPhoneReadsAsIos()
    {
        var (os, browser) = UserAgentParser.Parse(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1");

        Assert.Equal("iOS 17.0", os);
        Assert.Equal("Safari 17.0", browser);
    }

    [Fact]
    public void AndroidPhoneReadsAsAndroid()
    {
        var (os, browser) = UserAgentParser.Parse(
            "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36");

        Assert.Equal("Android 14", os);
        Assert.Equal("Chrome 120.0.0.0", browser);
    }

    [Fact]
    public void MacDesktopStillReadsAsMacOs()
    {
        var (os, _) = UserAgentParser.Parse(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15");

        Assert.Equal("macOS 10.15", os);
    }

    [Fact]
    public void LinuxDesktopStillReadsAsLinux()
    {
        var (os, _) = UserAgentParser.Parse(
            "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0");

        Assert.Equal("Linux", os);
    }
}
