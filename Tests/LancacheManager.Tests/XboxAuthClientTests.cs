using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancacheManager.Services.Xbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Build-verifiable coverage for the manager-side, daemon-free Xbox MSA login port. The live token dance
/// + fragment mint CANNOT be exercised here (no real Microsoft account), so these tests lock the PURE /
/// parsable pieces: the byte-sensitive ECDSA request signer (golden layout, mirrored from the daemon's
/// <c>XblRequestSignerTests</c>), MSA token-response parsing, the <c>XBL3.0</c> header build, and the
/// per-file CDN-fragment extraction the manager feeds into <c>MergeDaemonCatalogAsync</c>.
/// </summary>
public sealed class XboxAuthClientTests
{
    // A fixed Windows-filetime timestamp (Int64). 0x01d51856b75ee000.
    private const long FixedFiletime = 132038524800000000L;

    // The exact buffer that must be hashed+signed for: ts=FixedFiletime, GET /device/authenticate, empty
    // auth, empty body. Layout: [00 00 00 01] [00] int64BE(ts) [00] "GET\0" "/device/authenticate\0" "\0" body "\0".
    private const string ExpectedBufferHex =
        "000000010001d51856b75ee00000474554002f6465766963652f61757468656e746963617465000000";

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    [Fact]
    public async Task RequestDeviceCodeAsync_RequestsLegacyMbiSslScopeOnly()
    {
        string? requestBody = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (request, ct) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(ct);
            return JsonResponse("""
                { "user_code": "ABCD-EFGH", "device_code": "DEV", "verification_uri": "https://microsoft.com/link", "interval": 5, "expires_in": 900 }
                """);
        }));
        var client = new XboxAuthClient(httpClient, NullLogger<XboxAuthClient>.Instance);

        await client.RequestDeviceCodeAsync();

        Assert.NotNull(requestBody);
        var decodedBody = Uri.UnescapeDataString(requestBody!.Replace('+', ' '));
        // The legacy login.live.com flow returns a refresh token with the MBI_SSL scope alone; appending
        // the modern offline_access scope makes the device-code poll fail with invalid_grant.
        Assert.Contains("scope=service::user.auth.xboxlive.com::MBI_SSL", decodedBody);
        Assert.DoesNotContain("offline_access", decodedBody);
    }

    [Fact]
    public async Task PollForTokenAsync_RejectsEphemeralAccessTokenWithoutRefreshToken()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""
                { "token_type": "bearer", "expires_in": 3600, "access_token": "ACCESS" }
                """))));
        var client = new XboxAuthClient(httpClient, NullLogger<XboxAuthClient>.Instance);
        var deviceCode = new XboxDeviceCodeResponse
        {
            DeviceCode = "DEV",
            Interval = 0,
            ExpiresIn = 5
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PollForTokenAsync(deviceCode));

        Assert.Contains("refresh token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_SendsScopeSoLegacyEndpointDoesNotRejectWithInvalidScope()
    {
        string? requestBody = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (request, ct) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(ct);
            return JsonResponse("""
                { "token_type": "bearer", "expires_in": 3600, "access_token": "ACCESS", "refresh_token": "ROTATED" }
                """);
        }));
        var client = new XboxAuthClient(httpClient, NullLogger<XboxAuthClient>.Instance);

        await client.RefreshAccessTokenAsync("SAVED-REFRESH-TOKEN");

        Assert.NotNull(requestBody);
        var decodedBody = Uri.UnescapeDataString(requestBody!.Replace('+', ' '));
        // The legacy login.live.com refresh grant requires the scope; without it the endpoint returns
        // invalid_scope and the saved Xbox login is wiped on every restart.
        Assert.Contains("grant_type=refresh_token", decodedBody);
        Assert.Contains("scope=service::user.auth.xboxlive.com::MBI_SSL", decodedBody);
    }

    [Fact]
    public void SignatureBuffer_HasExactGoldenLayout()
    {
        byte[] buffer = XblRequestSigner.BuildSignatureBuffer(
            FixedFiletime, "GET", "/device/authenticate", string.Empty, Array.Empty<byte>());

        string actualHex = Convert.ToHexString(buffer).ToLowerInvariant();

        Assert.Equal(ExpectedBufferHex, actualHex);

        // Structural invariants in case the golden hex is ever regenerated.
        Assert.Equal(41, buffer.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, buffer[0..4]); // policy version
        Assert.Equal(0x00, buffer[4]);                                      // separator
        Assert.Equal(HexToBytes("01d51856b75ee000"), buffer[5..13]);        // int64 BE timestamp
        Assert.Equal(0x00, buffer[13]);                                     // separator
        Assert.Equal(0x00, buffer[^1]);                                     // trailing null after body
    }

    [Fact]
    public void SignatureBuffer_BodyAndAuthArePlacedAndNullTerminated()
    {
        byte[] body = System.Text.Encoding.ASCII.GetBytes("{\"a\":1}");
        byte[] buffer = XblRequestSigner.BuildSignatureBuffer(
            FixedFiletime, "POST", "/device/authenticate", "XBL3.0 x=uhs;tok", body);

        using var ms = new System.IO.MemoryStream();
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x01 });
        ms.WriteByte(0x00);
        ms.Write(HexToBytes("01d51856b75ee000"));
        ms.WriteByte(0x00);
        void Z(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); ms.Write(b); ms.WriteByte(0x00); }
        Z("POST");
        Z("/device/authenticate");
        Z("XBL3.0 x=uhs;tok");
        ms.Write(body);
        ms.WriteByte(0x00);

        Assert.Equal(ms.ToArray(), buffer);
    }

    [Fact]
    public void SignatureHeader_Is76BytesAndVerifies()
    {
        using var signer = XblRequestSigner.CreateNew();

        string headerBase64 = signer.SignAt(FixedFiletime, "GET", "/device/authenticate", string.Empty, Array.Empty<byte>());
        byte[] header = Convert.FromBase64String(headerBase64);

        // Decoded header layout: [4 policy] + [8 int64BE ts] + [64 r||s IEEE-P1363] = 76 bytes.
        Assert.Equal(76, header.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, header[0..4]);
        Assert.Equal(HexToBytes("01d51856b75ee000"), header[4..12]);

        byte[] signature = header[12..76];
        Assert.Equal(64, signature.Length); // 32-byte r || 32-byte s, NOT DER

        byte[] signedBuffer = HexToBytes(ExpectedBufferHex);
        bool verified = signer.PublicKey.VerifyData(
            signedBuffer, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.True(verified, "Signature did not verify over the golden buffer with SHA256/IEEE-P1363.");
    }

    [Fact]
    public void ProofKey_CoordinatesAre32BytesBase64Url()
    {
        using var signer = XblRequestSigner.CreateNew();
        var jwk = signer.GetProofKey();

        Assert.Equal("P-256", jwk.Crv);
        Assert.Equal("EC", jwk.Kty);

        Assert.DoesNotContain('+', jwk.X!);
        Assert.DoesNotContain('/', jwk.X!);
        Assert.DoesNotContain('=', jwk.X!);

        Assert.Equal(32, Base64UrlDecode(jwk.X!).Length);
        Assert.Equal(32, Base64UrlDecode(jwk.Y!).Length);
    }

    [Fact]
    public void Signer_KeyRoundTrips_ThroughPkcs8()
    {
        using var original = XblRequestSigner.CreateNew();
        string pkcs8 = original.ExportPkcs8Base64();

        using var restored = XblRequestSigner.FromPkcs8Base64(pkcs8);

        // Same device identity: identical public proof-key coordinates.
        var jwkOriginal = original.GetProofKey();
        var jwkRestored = restored.GetProofKey();
        Assert.Equal(jwkOriginal.X, jwkRestored.X);
        Assert.Equal(jwkOriginal.Y, jwkRestored.Y);

        // A signature minted by the restored signer verifies with the original's public key.
        string headerBase64 = restored.SignAt(FixedFiletime, "GET", "/device/authenticate", string.Empty, Array.Empty<byte>());
        byte[] signature = Convert.FromBase64String(headerBase64)[12..76];
        byte[] signedBuffer = HexToBytes(ExpectedBufferHex);

        Assert.True(original.PublicKey.VerifyData(
            signedBuffer, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void MsaTokenResponse_ParsesSnakeCaseSuccessPayload()
    {
        const string json = """
        { "token_type": "bearer", "expires_in": 3600, "access_token": "ACCESS", "refresh_token": "REFRESH" }
        """;

        var token = JsonSerializer.Deserialize<XboxMsaTokenResponse>(json);

        Assert.NotNull(token);
        Assert.Equal("ACCESS", token!.AccessToken);
        Assert.Equal("REFRESH", token.RefreshToken);
        Assert.Equal(3600, token.ExpiresIn);
        Assert.Null(token.Error);
    }

    [Fact]
    public void MsaTokenResponse_ParsesPendingErrorPayload()
    {
        const string json = """{ "error": "authorization_pending" }""";

        var token = JsonSerializer.Deserialize<XboxMsaTokenResponse>(json);

        Assert.NotNull(token);
        Assert.Null(token!.AccessToken);
        Assert.Equal("authorization_pending", token.Error);
    }

    [Fact]
    public void DeviceCodeResponse_ParsesChallengePayload()
    {
        const string json = """
        { "user_code": "ABCD-EFGH", "device_code": "DEV", "verification_uri": "https://microsoft.com/link", "interval": 5, "expires_in": 900 }
        """;

        var code = JsonSerializer.Deserialize<XboxDeviceCodeResponse>(json);

        Assert.NotNull(code);
        Assert.Equal("ABCD-EFGH", code!.UserCode);
        Assert.Equal("DEV", code.DeviceCode);
        Assert.Equal("https://microsoft.com/link", code.VerificationUri);
        Assert.Equal(5, code.Interval);
        Assert.Equal(900, code.ExpiresIn);
    }

    [Fact]
    public void BuildXblAuthorizationHeader_HasExactFormat()
    {
        Assert.Equal("XBL3.0 x=theUhs;theToken", XboxAuthClient.BuildXblAuthorizationHeader("theUhs", "theToken"));
    }

    [Fact]
    public void CollectFilePathFragments_StripsQueryDeduplicatesAndSkipsExcludedFiles()
    {
        const string guidA = "11111111-2222-3333-4444-555555555555";
        const string guidB = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var files = new List<XboxPackageFile>
        {
            // A normal file (no query).
            new() { FileName = "a.bin", FileSize = 10, RelativeUrl = $"/filestreamingservice/files/{guidA}", CdnRootPaths = new[] { "https://assets1.xboxlive.com" } },
            // Same GUID with a signing query string - the query must be stripped, then de-duped against the first.
            new() { FileName = "a.bin", FileSize = 10, RelativeUrl = $"/filestreamingservice/files/{guidA}?P1=1&P2=2", CdnRootPaths = new[] { "https://assets1.xboxlive.com" } },
            // A distinct second file.
            new() { FileName = "b.bin", FileSize = 10, RelativeUrl = $"/filestreamingservice/files/{guidB}", CdnRootPaths = new[] { "https://assets1.xboxlive.com" } },
            // Excluded extensions are skipped.
            new() { FileName = "skip.phf", FileSize = 10, RelativeUrl = "/filestreamingservice/files/cccccccc-1111-2222-3333-444444444444", CdnRootPaths = new[] { "https://assets1.xboxlive.com" } },
            new() { FileName = "skip.xsp", FileSize = 10, RelativeUrl = "/filestreamingservice/files/dddddddd-1111-2222-3333-444444444444", CdnRootPaths = new[] { "https://assets1.xboxlive.com" } },
            // Files missing required fields are skipped (no CdnRootPaths).
            new() { FileName = "bad.bin", FileSize = 10, RelativeUrl = "/filestreamingservice/files/eeeeeeee-1111-2222-3333-444444444444", CdnRootPaths = null },
        };

        var fragments = XboxAuthClient.CollectFilePathFragments(files, out var cdnHost);

        Assert.Equal("assets1.xboxlive.com", cdnHost);
        Assert.Equal(2, fragments.Count);
        Assert.Contains($"/filestreamingservice/files/{guidA}", fragments);
        Assert.Contains($"/filestreamingservice/files/{guidB}", fragments);
        Assert.DoesNotContain(fragments, f => f.Contains('?'));
    }

    [Fact]
    public void CollectFilePathFragments_EmptyInput_YieldsNoFragmentsAndNullHost()
    {
        var fragments = XboxAuthClient.CollectFilePathFragments(new List<XboxPackageFile>(), out var cdnHost);

        Assert.Empty(fragments);
        Assert.Null(cdnHost);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
