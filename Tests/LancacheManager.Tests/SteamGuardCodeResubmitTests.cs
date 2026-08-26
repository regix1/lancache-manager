using LancacheManager.Core.Services.SteamKit2;

namespace LancacheManager.Tests;

/// <summary>
/// SteamKit2 asks the authenticator for a guard code in a loop: it sends the code, and if Steam
/// rejects it the loop calls back with previousCodeWasIncorrect set, expecting a different code.
/// A submitted code is single-use, so handing the same one back gives that loop nothing to change
/// and it resubmits immediately, forever, with no delay and no exit. The sign-in then never returns,
/// which leaves the caller's in-progress flag set and refuses every later attempt.
///
/// These tests pin the only behavior that prevents it: once a code has been rejected, the
/// authenticator fails instead of repeating itself.
/// </summary>
public class SteamGuardCodeResubmitTests
{
    [Fact]
    public async Task DeviceCodeIsHandedOverOnceWhenSteamHasNotRejectedIt()
    {
        var authenticator = new SteamKit2Service.WebAuthenticator("123456", emailCode: null);

        var code = await authenticator.GetDeviceCodeAsync(previousCodeWasIncorrect: false);

        Assert.Equal("123456", code);
        Assert.False(authenticator.CodeWasRejected);
    }

    [Fact]
    public async Task RejectedDeviceCodeIsNotHandedBack()
    {
        var authenticator = new SteamKit2Service.WebAuthenticator("123456", emailCode: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GetDeviceCodeAsync(previousCodeWasIncorrect: true));

        Assert.True(
            authenticator.CodeWasRejected,
            "a rejection has to be recorded, or the caller cannot tell it apart from a first prompt");
    }

    [Fact]
    public async Task EmailCodeIsHandedOverOnceWhenSteamHasNotRejectedIt()
    {
        var authenticator = new SteamKit2Service.WebAuthenticator(twoFactorCode: null, emailCode: "ABCDE");

        var code = await authenticator.GetEmailCodeAsync("user@example.com", previousCodeWasIncorrect: false);

        Assert.Equal("ABCDE", code);
        Assert.False(authenticator.CodeWasRejected);
    }

    [Fact]
    public async Task RejectedEmailCodeIsNotHandedBack()
    {
        var authenticator = new SteamKit2Service.WebAuthenticator(twoFactorCode: null, emailCode: "ABCDE");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GetEmailCodeAsync("user@example.com", previousCodeWasIncorrect: true));

        Assert.True(authenticator.CodeWasRejected);
    }

    [Fact]
    public async Task MissingDeviceCodeStillFailsSoTheModalCanPrompt()
    {
        var authenticator = new SteamKit2Service.WebAuthenticator(twoFactorCode: null, emailCode: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.GetDeviceCodeAsync(previousCodeWasIncorrect: false));

        Assert.True(authenticator.NeedsTwoFactor);
        Assert.False(
            authenticator.CodeWasRejected,
            "never having been given a code is not the same as Steam rejecting one");
    }
}
