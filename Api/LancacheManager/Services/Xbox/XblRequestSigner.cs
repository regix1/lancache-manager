using System.Security.Cryptography;
using System.Text;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// Signs Xbox Live requests with the device identity's P-256 ECDSA key (ProofOfPossession). Ported
/// faithfully from the Xbox prefill daemon's <c>XboxPrefill/Handlers/XblRequestSigner.cs</c> (which was
/// itself ported from MsixvcPackageDownloader / a validated Python PoC). Uses the built-in
/// <see cref="ECDsa"/> (nistP256), an IEEE-P1363 (r||s, NOT DER) 64-byte signature, and a Windows-filetime
/// timestamp.
///
/// One signer instance == one stable device identity. The key is generated once and persisted (PKCS#8,
/// encrypted) so the minted device token keeps validating across restarts. The signed-buffer layout is
/// byte-sensitive: a single wrong byte fails as a silent HTTP 403, so the layout is locked by golden
/// unit tests (mirrors the daemon's XblRequestSignerTests).
/// </summary>
public sealed class XblRequestSigner : IDisposable
{
    // The signature "policy version" prefix. Always 1.
    private static readonly byte[] _policyVersion = { 0x00, 0x00, 0x00, 0x01 };

    // Offset between the Unix epoch (1970-01-01) and the Windows filetime epoch (1601-01-01) in seconds.
    private const long WindowsFiletimeEpochOffsetSeconds = 11644473600L;

    private readonly ECDsa _ecdsa;
    private bool _disposed;

    private XblRequestSigner(ECDsa ecdsa)
    {
        _ecdsa = ecdsa;
    }

    /// <summary>Creates a signer with a freshly generated P-256 device identity.</summary>
    public static XblRequestSigner CreateNew()
    {
        return new XblRequestSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256));
    }

    /// <summary>Restores a signer from a previously persisted PKCS#8 private key (base64).</summary>
    public static XblRequestSigner FromPkcs8Base64(string pkcs8Base64)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64), out _);
        return new XblRequestSigner(ecdsa);
    }

    /// <summary>Exports the device identity private key as base64-encoded PKCS#8, for persistence.</summary>
    public string ExportPkcs8Base64()
    {
        return Convert.ToBase64String(_ecdsa.ExportPkcs8PrivateKey());
    }

    /// <summary>
    /// The device's proof key (JWK) - the 32-byte big-endian public point coordinates, base64url (no
    /// padding). Embedded in the device/authenticate request body.
    /// </summary>
    internal XboxProofKeyJwk GetProofKey()
    {
        var parameters = _ecdsa.ExportParameters(includePrivateParameters: false);
        return new XboxProofKeyJwk
        {
            X = Base64UrlEncode(parameters.Q.X!),
            Y = Base64UrlEncode(parameters.Q.Y!)
        };
    }

    /// <summary>
    /// Builds the <c>Signature</c> header for a request, per the validated layout:
    /// <code>
    /// ts  = (unixSeconds + 11644473600) * 10_000_000   // Windows filetime
    /// buf = [00 00 00 01] + [00] + int64_BE(ts) + [00]
    ///     + ascii(method) + [00] + ascii(pathAndQuery) + [00] + ascii(auth) + [00] + body + [00]
    /// sig = ECDsa.SignData(buf, SHA256, IeeeP1363)      // 64-byte r||s
    /// header = base64( [00 00 00 01] + int64_BE(ts) + sig )
    /// </code>
    /// </summary>
    /// <param name="method">HTTP method, e.g. GET / POST.</param>
    /// <param name="pathAndQuery">The request path + query, e.g. <c>/device/authenticate</c>.</param>
    /// <param name="authorizationHeader">The Authorization header value, or empty string when none.</param>
    /// <param name="body">The request body bytes (empty for GET).</param>
    public string Sign(string method, string pathAndQuery, string authorizationHeader, byte[] body)
    {
        long ts = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + WindowsFiletimeEpochOffsetSeconds) * 10_000_000L;
        return SignAt(ts, method, pathAndQuery, authorizationHeader, body);
    }

    /// <summary>
    /// Signs a request at an explicit Windows-filetime timestamp. Public <see cref="Sign"/> uses the
    /// current time; this overload exists so the exact, deterministic buffer/header layout can be
    /// golden-tested.
    /// </summary>
    internal string SignAt(long windowsFiletime, string method, string pathAndQuery, string authorizationHeader, byte[] body)
    {
        byte[] tsBytes = Int64BigEndian(windowsFiletime);
        byte[] toSign = BuildSignatureBuffer(windowsFiletime, method, pathAndQuery, authorizationHeader, body);

        byte[] signature = _ecdsa.SignData(toSign, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        using var header = new MemoryStream();
        header.Write(_policyVersion, 0, _policyVersion.Length);
        header.Write(tsBytes, 0, tsBytes.Length);
        header.Write(signature, 0, signature.Length);

        return Convert.ToBase64String(header.ToArray());
    }

    /// <summary>
    /// Builds the exact byte buffer that is hashed+signed, per the validated layout. Exposed for golden
    /// tests (the layout is the part that, if wrong, fails as a silent 403).
    /// </summary>
    internal static byte[] BuildSignatureBuffer(long windowsFiletime, string method, string pathAndQuery, string authorizationHeader, byte[] body)
    {
        byte[] tsBytes = Int64BigEndian(windowsFiletime);
        body ??= Array.Empty<byte>();
        authorizationHeader ??= string.Empty;

        using var buffer = new MemoryStream();
        buffer.Write(_policyVersion, 0, _policyVersion.Length);
        buffer.WriteByte(0x00);
        buffer.Write(tsBytes, 0, tsBytes.Length);
        buffer.WriteByte(0x00);

        WriteAsciiNullTerminated(buffer, method);
        WriteAsciiNullTerminated(buffer, pathAndQuery);
        WriteAsciiNullTerminated(buffer, authorizationHeader);
        buffer.Write(body, 0, body.Length);
        buffer.WriteByte(0x00);

        return buffer.ToArray();
    }

    /// <summary>The device identity's public key (P-256). Exposed so a golden test can verify a produced signature.</summary>
    internal ECDsa PublicKey => _ecdsa;

    private static void WriteAsciiNullTerminated(Stream stream, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0x00);
    }

    private static byte[] Int64BigEndian(long value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }
        return bytes;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _ecdsa.Dispose();
        _disposed = true;
    }
}
