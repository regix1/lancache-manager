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
        var apiKey = _apiKeyService.GetOrCreateApiKey();

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
    /// Decrypts a sensitive string value
    /// Handles plaintext, v1 (without API key), and v2 (with API key) encryption
    /// Automatically migrates from older formats to v2 on next save
    /// Returns null if decryption fails (data will be cleared, user must re-authenticate)
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

        // Case 3: Plaintext (no prefix) - oldest legacy format
        _logger.LogDebug("Migrating plaintext sensitive data to encrypted format");
        return ciphertext;
    }
}
