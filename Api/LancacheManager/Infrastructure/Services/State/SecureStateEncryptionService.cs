using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Provides encryption/decryption for sensitive data in state.json
/// Uses ASP.NET Core Data Protection API with machine-specific keys + API key
/// </summary>
public class SecureStateEncryptionService
{
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly ApiKeyService _apiKeyService;
    private readonly ILogger<SecureStateEncryptionService> _logger;

    // Prefix to identify encrypted values (helps with migration from plaintext)
    private const string EncryptedPrefix = "ENC:";
    private const string EncryptedPrefixV2 = "ENC2:"; // New prefix for API-key-protected encryption

    public SecureStateEncryptionService(
        IDataProtectionProvider dataProtectionProvider,
        ApiKeyService apiKeyService,
        ILogger<SecureStateEncryptionService> logger)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current protector using the API key as part of the purpose
    /// </summary>
    private IDataProtector GetProtector()
    {
        var apiKey = _apiKeyService.GetApiKey();

        // Use API key as part of the encryption purpose
        // This means stealing encryption keys alone won't work - attacker needs API key too
        return _dataProtectionProvider.CreateProtector($"LancacheManager.SteamAuth.v2.{apiKey}");
    }

    /// <summary>
    /// Gets the legacy protector (v1) without API key for migration purposes
    /// </summary>
    private IDataProtector GetLegacyProtector()
    {
        return _dataProtectionProvider.CreateProtector("LancacheManager.SteamAuth.v1");
    }

    /// <summary>
    /// Encrypts a sensitive string value using API key as part of encryption
    /// </summary>
    public string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return null;
        }

        try
        {
            var protector = GetProtector();
            var encrypted = protector.Protect(plaintext);
            return EncryptedPrefixV2 + encrypted; // Use v2 prefix for API-key-protected encryption
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt sensitive data");
            throw;
        }
    }

    /// <summary>
    /// True when a stored secret is a v1 (ENC:) value. It is genuinely encrypted, just under the
    /// older scheme, so it is worth rewriting with the current key. Callers check this after loading
    /// a credentials file and save it back out. A v2 value does not match, because "ENC2:" does not
    /// start with "ENC:".
    /// </summary>
    public bool NeedsReEncryption(string? storedValue)
    {
        return !string.IsNullOrEmpty(storedValue) && storedValue.StartsWith(EncryptedPrefix);
    }

    /// <summary>
    /// True when a stored secret carries no encryption prefix at all, meaning it is sitting on disk
    /// in the clear. That is a different situation from a v1 value: it has to be assumed exposed, so
    /// callers throw the whole credentials file away rather than rewrite it. <see cref="Decrypt"/>
    /// already refuses to return these; this is for callers that keep going when only some of their
    /// secrets fail and so have to notice the unencrypted one for themselves.
    /// </summary>
    public bool IsUnencrypted(string? storedValue)
    {
        return !string.IsNullOrEmpty(storedValue)
            && !storedValue.StartsWith(EncryptedPrefixV2)
            && !storedValue.StartsWith(EncryptedPrefix);
    }

    /// <summary>
    /// Decrypts a sensitive string value
    /// Handles v1 (without API key) and v2 (with API key) encryption
    /// Returns null if decryption fails, or if the value was never encrypted at all
    /// (data will be cleared, user must re-authenticate)
    /// </summary>
    public string? Decrypt(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        // Case 1: New v2 encryption with API key (ENC2: prefix)
        if (ciphertext.StartsWith(EncryptedPrefixV2))
        {
            try
            {
                var encryptedData = ciphertext.Substring(EncryptedPrefixV2.Length);
                var protector = GetProtector();
                return protector.Unprotect(encryptedData);
            }
            catch (Exception ex)
            {
                // Expected after API key regeneration - silently clear data, user will re-authenticate
                _logger.LogDebug("Unable to decrypt sensitive data (likely due to API key change) - clearing data. Error: {Error}", ex.Message);
                return null;
            }
        }

        // Case 2: Legacy v1 encryption without API key (ENC: prefix)
        if (ciphertext.StartsWith(EncryptedPrefix))
        {
            try
            {
                var encryptedData = ciphertext.Substring(EncryptedPrefix.Length);
                var legacyProtector = GetLegacyProtector();
                var plaintext = legacyProtector.Unprotect(encryptedData);

                _logger.LogDebug("Migrating v1 encrypted data to v2 format with API key protection");
                return plaintext;
            }
            catch (Exception ex)
            {
                // Unable to decrypt legacy data - silently clear it
                _logger.LogDebug("Unable to decrypt legacy v1 sensitive data - clearing data. Error: {Error}", ex.Message);
                return null;
            }
        }

        // Case 3: Plaintext (no prefix) - oldest legacy format. A secret that has been sitting on
        // disk in the clear has to be assumed exposed, so it is thrown away rather than handed back
        // or re-wrapped: signing in again replaces it with a fresh secret. Returning null puts the
        // caller on the same path it already takes for a secret it cannot decrypt - the credentials
        // file is deleted and the user is signed out.
        _logger.LogWarning(
            "Stored credentials were not encrypted - discarding them, you will need to sign in again");
        return null;
    }
}
