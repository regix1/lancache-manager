using System.Net.Http.Headers;
using System.Text.Json;
using LancacheManager.Models;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// Typed <see cref="HttpClient"/> that performs the full manager-side, daemon-free Xbox MSA device-code
/// login + catalog harvest, ported faithfully from the prefill daemon's
/// <c>XboxPrefill/Handlers/XboxAccountManager.cs</c> + <c>XboxApi.cs</c> + <c>ManifestHandler.cs</c>
/// (all PROVEN live against a real account). It is the Xbox analogue of <c>EpicApiDirectClient</c>:
///
///   device-code init (oauth20_connect.srf) -> poll (oauth20_token.srf, pending/slow_down) ->
///   refresh-token grant -> XBL user/authenticate -> XBL device/authenticate (SIGNED, ECDSA POP) ->
///   XSTS authorize for titlehub (user token, unsigned) AND update/packagespc (user+device, signed) ->
///   XBL3.0 header -> titlehub owned-titles -> per title: DisplayCatalog ContentId(s) ->
///   packagespc GetBasePackage (signed) -> /filestreamingservice/files/&lt;GUID&gt; CDN fragments.
///
/// The harvested <see cref="CdnInfo"/> list (ProductId -&gt; title + per-file fragments) is fed UNCHANGED
/// into the existing <c>XboxMappingService.MergeDaemonCatalogAsync</c> + <c>ResolveDownloadsAsync</c>.
/// Endpoints/scopes/client id live in <see cref="XboxAuthConstants"/> (not hardcoded deep here).
///
/// RUNTIME caveat: the live token dance + fragment mint cannot be verified without a real Microsoft
/// account; the byte-sensitive signer is locked by golden unit tests and this port mirrors the
/// proven-live daemon source.
/// </summary>
public class XboxAuthClient
{
    private static readonly string[] _excludedExtensions = { ".phf", ".xsp" };

    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<XboxAuthClient> _logger;

    public XboxAuthClient(HttpClient httpClient, ILogger<XboxAuthClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", XboxAuthConstants.DefaultUserAgent);
        }
    }

    #region MSA device-code flow

    /// <summary>Requests a device code from MSA (<c>oauth20_connect.srf</c>).</summary>
    internal async Task<XboxDeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = XboxAuthConstants.ClientId,
            ["scope"] = XboxAuthConstants.AuthScope,
            ["response_type"] = "device_code"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, XboxAuthConstants.DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var deviceCode = JsonSerializer.Deserialize<XboxDeviceCodeResponse>(json, _readOptions);
        if (deviceCode?.DeviceCode == null)
        {
            throw new InvalidOperationException("Failed to obtain a device code from Microsoft.");
        }
        return deviceCode;
    }

    /// <summary>
    /// Polls the MSA token endpoint until the user approves the device code. Handles
    /// <c>authorization_pending</c> (keep polling) and <c>slow_down</c> (increase interval); any other
    /// error is fatal. Times out at the device code's <c>expires_in</c> deadline.
    /// </summary>
    internal async Task<XboxMsaTokenResponse> PollForTokenAsync(XboxDeviceCodeResponse deviceCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(deviceCode.DeviceCode))
        {
            throw new InvalidOperationException("Device code is missing; cannot poll for a token.");
        }

        var interval = TimeSpan.FromSeconds(Math.Max(deviceCode.Interval, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn > 0 ? deviceCode.ExpiresIn : 900);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(interval, ct);

            var form = new Dictionary<string, string>
            {
                ["client_id"] = XboxAuthConstants.ClientId,
                ["grant_type"] = XboxAuthConstants.DeviceCodeGrantType,
                ["device_code"] = deviceCode.DeviceCode
            };

            var token = await PostTokenFormAsync(form, ct);
            if (token.AccessToken != null)
            {
                // A device-code access token by itself only keeps Xbox connected for this process.
                // Never report a successful login unless it can be restored after restart.
                if (string.IsNullOrWhiteSpace(token.RefreshToken))
                {
                    throw new InvalidOperationException(
                        "Xbox sign-in did not return a refresh token; persistent login is unavailable.");
                }

                return token;
            }

            // authorization_pending / slow_down => keep polling. Anything else is fatal.
            if (token.Error == "slow_down")
            {
                interval = interval.Add(TimeSpan.FromSeconds(5));
            }
            else if (token.Error != null && token.Error != "authorization_pending")
            {
                throw new InvalidOperationException($"Xbox device-code login failed: {token.Error}");
            }
        }

        throw new TimeoutException("Xbox device-code login timed out. Please try again.");
    }

    /// <summary>
    /// Exchanges a saved MSA refresh token for a fresh access token (and a rotated refresh token).
    /// Throws when the refresh token is expired/invalid so the caller can clear credentials.
    /// </summary>
    internal async Task<XboxMsaTokenResponse> RefreshAccessTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = XboxAuthConstants.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };

        var token = await PostTokenFormAsync(form, ct);
        if (token.AccessToken == null)
        {
            throw new InvalidOperationException($"Xbox token refresh failed: {token.Error ?? "no access token"}");
        }
        return token;
    }

    private async Task<XboxMsaTokenResponse> PostTokenFormAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, XboxAuthConstants.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var response = await _httpClient.SendAsync(request, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var token = JsonSerializer.Deserialize<XboxMsaTokenResponse>(json, _readOptions);
        return token ?? new XboxMsaTokenResponse();
    }

    #endregion

    #region Catalog harvest (XBL/XSTS chain -> titlehub -> packagespc fragments)

    /// <summary>
    /// Resolves the authenticated Xbox account identity (gamertag + xuid) without harvesting the catalog.
    /// </summary>
    internal async Task<XboxHarvestResult> GetAccountIdentityAsync(
        string msaAccessToken, XblRequestSigner signer, CancellationToken ct = default)
    {
        var userToken = await AuthenticateUserAsync(msaAccessToken, ct);
        var titleHubXsts = await AuthorizeXstsAsync(
            userToken, deviceToken: null, XboxAuthConstants.TitleHubRelyingParty, signed: false, signer, ct);
        var titleClaims = titleHubXsts.DisplayClaims?.Xui?.FirstOrDefault();
        var xuid = titleClaims?.Xid;
        if (string.IsNullOrEmpty(xuid))
        {
            throw new InvalidOperationException("No Xbox user id (xuid) returned from XSTS authorization.");
        }

        var titleHubHeader = BuildXblAuthorizationHeader(titleClaims?.Uhs, titleHubXsts.Token);
        var displayName = await TryGetGamertagAsync(xuid, titleHubHeader, ct);

        return new XboxHarvestResult
        {
            CdnInfos = [],
            DisplayName = displayName,
            Xuid = xuid
        };
    }

    /// <summary>
    /// Runs the full XBL/XSTS token chain with the MSA access token + device signer, enumerates the
    /// account's owned titles via titlehub, and resolves each title to its per-file CDN fragments via
    /// DisplayCatalog + the signed GetBasePackage call. Returns the <see cref="CdnInfo"/> list to feed
    /// into <c>MergeDaemonCatalogAsync</c>, plus the gamertag and xuid for the auth-status surface.
    /// </summary>
    internal async Task<XboxHarvestResult> HarvestCatalogAsync(string msaAccessToken, XblRequestSigner signer, CancellationToken ct = default)
    {
        var userToken = await AuthenticateUserAsync(msaAccessToken, ct);
        var deviceToken = await AuthenticateDeviceAsync(signer, ct);

        // Titlehub audience: user token only (unsigned). Update/package audience: device-bearing + signed.
        var titleHubXsts = await AuthorizeXstsAsync(userToken, deviceToken: null, XboxAuthConstants.TitleHubRelyingParty, signed: false, signer, ct);
        var updateXsts = await AuthorizeXstsAsync(userToken, deviceToken, XboxAuthConstants.UpdateRelyingParty, signed: true, signer, ct);

        var titleClaims = titleHubXsts.DisplayClaims?.Xui?.FirstOrDefault();
        var updateClaims = updateXsts.DisplayClaims?.Xui?.FirstOrDefault();

        var xuid = titleClaims?.Xid;
        if (string.IsNullOrEmpty(xuid))
        {
            throw new InvalidOperationException("No Xbox user id (xuid) returned from XSTS authorization.");
        }

        var titleHubHeader = BuildXblAuthorizationHeader(titleClaims?.Uhs, titleHubXsts.Token);
        var updateHeader = BuildXblAuthorizationHeader(updateClaims?.Uhs, updateXsts.Token);

        // Best-effort gamertag for the UI; never blocks the harvest.
        var displayName = await TryGetGamertagAsync(xuid, titleHubHeader, ct);

        var titles = await GetOwnedTitlesAsync(xuid, titleHubHeader, ct);
        _logger.LogInformation("Xbox titlehub returned {Count} prefillable owned title(s)", titles.Count);

        var cdnInfos = new List<CdnInfo>();
        foreach (var title in titles)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(title.ProductId))
            {
                continue;
            }

            try
            {
                var contentIds = await GetContentIdsAsync(title.ProductId, ct);
                if (contentIds.Count == 0)
                {
                    continue;
                }

                var allFiles = new List<XboxPackageFile>();
                foreach (var contentId in contentIds)
                {
                    var package = await GetBasePackageAsync(contentId, updateHeader, signer, ct);
                    if (package.PackageFound && package.PackageFiles != null)
                    {
                        allFiles.AddRange(package.PackageFiles);
                    }
                }

                var fragments = CollectFilePathFragments(allFiles, out var cdnHost);
                if (fragments.Count == 0)
                {
                    continue;
                }

                cdnInfos.Add(new CdnInfo
                {
                    AppId = title.ProductId,
                    Name = string.IsNullOrWhiteSpace(title.Name) ? title.ProductId : title.Name,
                    CdnHost = cdnHost ?? "assets1.xboxlive.com",
                    FilePathFragments = fragments
                });

                // Gentle pacing between titles, mirroring EpicApiDirectClient's catalog loop.
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve Xbox package for product {ProductId} ({Title})", title.ProductId, title.Name);
            }
        }

        _logger.LogInformation("Xbox catalog harvest produced CDN fragments for {Count} title(s)", cdnInfos.Count);
        return new XboxHarvestResult { CdnInfos = cdnInfos, DisplayName = displayName, Xuid = xuid };
    }

    private async Task<string> AuthenticateUserAsync(string accessToken, CancellationToken ct)
    {
        var body = new XboxXblUserAuthRequest
        {
            Properties = new XboxXblUserAuthProperties { RpsTicket = accessToken }
        };
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, XboxAuthConstants.UserAuthUrl);
        request.Headers.Add("x-xbl-contract-version", "1");
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<XboxXblAuthResponse>(json, _readOptions);
        if (result?.Token == null)
        {
            throw new InvalidOperationException("Failed to obtain the Xbox user token.");
        }
        return result.Token;
    }

    private async Task<string> AuthenticateDeviceAsync(XblRequestSigner signer, CancellationToken ct)
    {
        var body = new XboxXblDeviceAuthRequest
        {
            Properties = new XboxXblDeviceAuthProperties
            {
                Id = $"{{{Guid.NewGuid()}}}",
                ProofKey = signer.GetProofKey()
            }
        };
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body);

        var uri = new Uri(XboxAuthConstants.DeviceAuthUrl);
        var signature = signer.Sign("POST", uri.PathAndQuery, string.Empty, bodyBytes);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("x-xbl-contract-version", "1");
        request.Headers.Add("Signature", signature);
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<XboxXblAuthResponse>(json, _readOptions);
        if (result?.Token == null)
        {
            throw new InvalidOperationException("Failed to obtain the Xbox device token.");
        }
        return result.Token;
    }

    private async Task<XboxXstsTokenResponse> AuthorizeXstsAsync(
        string userToken, string? deviceToken, string relyingParty, bool signed, XblRequestSigner signer, CancellationToken ct)
    {
        var body = new XboxXstsAuthRequest
        {
            RelyingParty = relyingParty,
            Properties = new XboxXstsAuthProperties
            {
                UserTokens = new[] { userToken },
                DeviceToken = deviceToken
            }
        };
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body);

        var uri = new Uri(XboxAuthConstants.XstsAuthUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("x-xbl-contract-version", "1");
        if (signed)
        {
            request.Headers.Add("Signature", signer.Sign("POST", uri.PathAndQuery, string.Empty, bodyBytes));
        }
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<XboxXstsTokenResponse>(json, _readOptions);
        if (result?.Token == null)
        {
            throw new InvalidOperationException($"Failed to obtain the XSTS token for {relyingParty}.");
        }
        return result;
    }

    private async Task<List<XboxTitleHubTitle>> GetOwnedTitlesAsync(string xuid, string titleHubHeader, CancellationToken ct)
    {
        var url = $"{XboxAuthConstants.TitleHubBaseUrl}/users/xuid({xuid})/titles/titlehistory/decoration/detail,image,productId,gamepass";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", titleHubHeader);
        request.Headers.Add("x-xbl-contract-version", "2");
        request.Headers.Add("Accept-Language", "en-US");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var titleHub = JsonSerializer.Deserialize<XboxTitleHubResponse>(json, _readOptions);

        // Only MS-Store/Xbox titles (non-null pfn AND productId) are resolvable to a package; de-dup by ProductId.
        return (titleHub?.Titles ?? new List<XboxTitleHubTitle>())
            .Where(t => !string.IsNullOrEmpty(t.Pfn) && !string.IsNullOrEmpty(t.ProductId))
            .GroupBy(t => t.ProductId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<string>> GetContentIdsAsync(string productId, CancellationToken ct)
    {
        var url = $"{XboxAuthConstants.DisplayCatalogBaseUrl}/v7.0/products?bigIds={productId}&market=US&languages=en-US,neutral&fieldsTemplate=details";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // DisplayCatalog is anonymous - no auth header.
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var catalog = JsonSerializer.Deserialize<XboxDisplayCatalogResponse>(json, _readOptions);

        var contentIds = new List<string>();
        foreach (var product in catalog?.Products ?? new List<XboxDisplayCatalogProduct>())
        {
            foreach (var skuAvailability in product.DisplaySkuAvailabilities ?? new List<XboxDisplaySkuAvailability>())
            {
                foreach (var package in skuAvailability.Sku?.Properties?.Packages ?? new List<XboxDisplayCatalogPackage>())
                {
                    if (!string.IsNullOrEmpty(package.ContentId) &&
                        !contentIds.Contains(package.ContentId, StringComparer.OrdinalIgnoreCase))
                    {
                        contentIds.Add(package.ContentId);
                    }
                }
            }
        }

        return contentIds;
    }

    private async Task<XboxGetBasePackageResponse> GetBasePackageAsync(
        string contentId, string updateHeader, XblRequestSigner signer, CancellationToken ct)
    {
        var uri = new Uri($"{XboxAuthConstants.PackageServiceBaseUrl}{contentId}");
        var signature = signer.Sign("GET", uri.PathAndQuery, updateHeader, Array.Empty<byte>());

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Authorization", updateHeader);
        request.Headers.Add("Signature", signature);
        request.Headers.Add("x-xbl-contract-version", "1");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var package = JsonSerializer.Deserialize<XboxGetBasePackageResponse>(json, _readOptions);
        return package ?? new XboxGetBasePackageResponse { PackageFound = false };
    }

    private async Task<string?> TryGetGamertagAsync(string xuid, string titleHubHeader, CancellationToken ct)
    {
        try
        {
            var url = $"{XboxAuthConstants.ProfileBaseUrl}/users/xuid({xuid})/profile/settings?settings=Gamertag";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", titleHubHeader);
            request.Headers.Add("x-xbl-contract-version", "2");
            request.Headers.Add("Accept-Language", "en-US");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var profile = JsonSerializer.Deserialize<XboxProfileResponse>(json, _readOptions);
            var gamertag = profile?.ProfileUsers?
                .FirstOrDefault()?.Settings?
                .FirstOrDefault(s => string.Equals(s.Id, "Gamertag", StringComparison.OrdinalIgnoreCase))?.Value;

            return string.IsNullOrWhiteSpace(gamertag) ? null : gamertag;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch Xbox gamertag for xuid {Xuid} (non-fatal)", xuid);
            return null;
        }
    }

    #endregion

    #region Pure helpers (unit-tested)

    /// <summary>Builds the <c>XBL3.0 x={uhs};{token}</c> authorization header.</summary>
    internal static string BuildXblAuthorizationHeader(string? uhs, string? token)
    {
        return $"XBL3.0 x={uhs};{token}";
    }

    /// <summary>
    /// Reduces a package's files to the stable per-file CDN path fragments the manager matches against
    /// (the query string is stripped, case-insensitively de-duplicated). Joins each file's
    /// <c>CdnRootPaths[0]</c> with its <c>RelativeUrl</c> exactly as the daemon does, takes the URL's
    /// path (query stripped), and outputs the first file's host as <paramref name="cdnHost"/>. Mirrors the
    /// daemon's <c>ManifestHandler.CollectFilePathFragments</c> (minus the slice expansion the manager does
    /// not need). Each fragment is a <c>/filestreamingservice/files/&lt;GUID&gt;</c> or assets1 package path
    /// that <c>XboxMappingService.IsValidFragment</c> validates downstream.
    /// </summary>
    internal static List<string> CollectFilePathFragments(IEnumerable<XboxPackageFile> files, out string? cdnHost)
    {
        cdnHost = null;
        var fragments = new List<string>();

        foreach (var file in files)
        {
            if (ShouldSkipPackageFile(file))
            {
                continue;
            }

            var cdnRoot = file.CdnRootPaths![0].TrimEnd('/');
            var fullUrl = $"{cdnRoot}/{file.RelativeUrl!.TrimStart('/')}";
            if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri))
            {
                continue;
            }

            cdnHost ??= uri.Host;

            var pathAndQuery = uri.PathAndQuery;
            var fragment = pathAndQuery.Contains('?')
                ? pathAndQuery[..pathAndQuery.IndexOf('?')]
                : pathAndQuery;
            fragments.Add(fragment);
        }

        return fragments.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ShouldSkipPackageFile(XboxPackageFile file)
    {
        if (string.IsNullOrEmpty(file.FileName)
            || string.IsNullOrEmpty(file.RelativeUrl)
            || file.CdnRootPaths == null
            || file.CdnRootPaths.Length == 0
            || string.IsNullOrEmpty(file.CdnRootPaths[0]))
        {
            return true;
        }
        return _excludedExtensions.Any(ext => file.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
