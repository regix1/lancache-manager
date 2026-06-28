using System.Text.Json;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Tests;

public sealed class CredentialChallengeParsingTests
{
    [Fact]
    public void TryParseFromResponse_ParsesInlineAutoLoginChallenge()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "type": "credential-challenge",
              "challengeId": "abc123",
              "credentialType": "auto-login",
              "serverPublicKey": "base64key"
            }
            """);

        var response = new CommandResponse
        {
            Success = true,
            Data = doc.RootElement.Clone()
        };

        var challenge = CredentialChallenge.TryParseFromResponse(response);

        Assert.NotNull(challenge);
        Assert.Equal("abc123", challenge!.ChallengeId);
        Assert.Equal("auto-login", challenge.CredentialType);
        Assert.Equal("base64key", challenge.ServerPublicKey);
    }

    [Fact]
    public void TryParseFromResponse_ReturnsNullWhenUnsuccessful()
    {
        var response = new CommandResponse { Success = false };
        Assert.Null(CredentialChallenge.TryParseFromResponse(response));
    }
}
