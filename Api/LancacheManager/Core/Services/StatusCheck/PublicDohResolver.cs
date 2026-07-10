using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LancacheManager.Core.Services.StatusCheck;

internal sealed record DohResolutionResult(
    IReadOnlyList<IPAddress> Addresses,
    int TotalAddresses,
    bool TooManyAddresses,
    string? FailureReason);

/// <summary>Conservative global-address gate applied before any publisher connection.</summary>
internal static class PublicAddressSafety
{
    private static readonly (byte[] Prefix, int Bits)[] _blockedIpv4Ranges =
    {
        (new byte[] { 0, 0, 0, 0 }, 8),
        (new byte[] { 10, 0, 0, 0 }, 8),
        (new byte[] { 100, 64, 0, 0 }, 10),
        (new byte[] { 127, 0, 0, 0 }, 8),
        (new byte[] { 169, 254, 0, 0 }, 16),
        (new byte[] { 172, 16, 0, 0 }, 12),
        (new byte[] { 192, 0, 0, 0 }, 24),
        (new byte[] { 192, 0, 2, 0 }, 24),
        (new byte[] { 192, 31, 196, 0 }, 24),
        (new byte[] { 192, 52, 193, 0 }, 24),
        (new byte[] { 192, 88, 99, 0 }, 24),
        (new byte[] { 192, 168, 0, 0 }, 16),
        (new byte[] { 192, 175, 48, 0 }, 24),
        (new byte[] { 198, 18, 0, 0 }, 15),
        (new byte[] { 198, 51, 100, 0 }, 24),
        (new byte[] { 203, 0, 113, 0 }, 24),
        (new byte[] { 224, 0, 0, 0 }, 4),
        (new byte[] { 240, 0, 0, 0 }, 4)
    };

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !_blockedIpv4Ranges.Any(range => IsInPrefix(bytes, range.Prefix, range.Bits));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
        {
            return false;
        }

        var ipv6 = address.GetAddressBytes();

        // Current global unicast space is 2000::/3. Reject special-purpose subranges inside it.
        if ((ipv6[0] & 0xE0) != 0x20 ||
            IsInPrefix(ipv6, new byte[] { 0x20, 0x01, 0x00, 0x00 }, 23) ||
            IsInPrefix(ipv6, new byte[] { 0x20, 0x01, 0x0d, 0xb8 }, 32) ||
            IsInPrefix(ipv6, new byte[] { 0x20, 0x02 }, 16) ||
            IsInPrefix(ipv6, new byte[] { 0x3f, 0xff, 0x00 }, 20))
        {
            return false;
        }

        return true;
    }

    private static bool IsInPrefix(ReadOnlySpan<byte> address, ReadOnlySpan<byte> prefix, int prefixBits)
    {
        var fullBytes = prefixBits / 8;
        var remainingBits = prefixBits % 8;
        if (address.Length < fullBytes || prefix.Length < fullBytes + (remainingBits > 0 ? 1 : 0) ||
            !address[..fullBytes].SequenceEqual(prefix[..fullBytes]))
        {
            return false;
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[fullBytes] & mask) == (prefix[fullBytes] & mask);
    }
}

/// <summary>Queries public JSON DoH controls for current A/AAAA edges without using LAN DNS data.</summary>
internal sealed class PublicDohResolver
{
    private const int MaxCnameDepth = 8;
    private const int MaxUniqueAddresses = 8;
    private const int MaxResponseBytes = 256 * 1024;
    private static readonly (string Name, string BaseUrl)[] _providers =
    {
        ("cloudflare", "https://cloudflare-dns.com/dns-query"),
        ("google", "https://dns.google/resolve")
    };

    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;

    internal PublicDohResolver(HttpClient client, TimeSpan? requestTimeout = null)
    {
        _client = client;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(4);
    }

    internal async Task<DohResolutionResult> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = new HashSet<IPAddress>();
        var validResponses = 0;

        foreach (var provider in _providers)
        {
            foreach (var query in new[] { (Name: "A", Type: 1), (Name: "AAAA", Type: 28) })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ResolveTypeAsync(provider.BaseUrl, host, query.Name, query.Type, cancellationToken);
                if (result.ValidResponse)
                {
                    validResponses++;
                }

                foreach (var address in result.Addresses)
                {
                    addresses.Add(address);
                }
            }
        }

        var ordered = addresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)
            .ToList();
        var tooMany = ordered.Count > MaxUniqueAddresses;
        if (tooMany)
        {
            ordered = ordered.Take(MaxUniqueAddresses).ToList();
        }

        var failureReason = ordered.Count > 0
            ? tooMany ? "tooManyEdges" : null
            : validResponses > 0 ? "noPublicEdges" : "dohUnavailable";
        return new DohResolutionResult(ordered, addresses.Count, tooMany, failureReason);
    }

    private async Task<(bool ValidResponse, IReadOnlyList<IPAddress> Addresses)> ResolveTypeAsync(
        string baseUrl,
        string originalHost,
        string queryName,
        int queryType,
        CancellationToken cancellationToken)
    {
        var currentName = NormalizeName(originalHost);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var depth = 0; depth <= MaxCnameDepth; depth++)
        {
            if (!visited.Add(currentName))
            {
                return (false, Array.Empty<IPAddress>());
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_requestTimeout);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{baseUrl}?name={Uri.EscapeDataString(currentName)}&type={queryName}");
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);
                if (!response.IsSuccessStatusCode || response.Content == null)
                {
                    return (false, Array.Empty<IPAddress>());
                }

                var bytes = await ReadBoundedAsync(response.Content, timeoutCts.Token);
                using var document = JsonDocument.Parse(bytes);
                if (!TryReadValidatedAnswer(document.RootElement, currentName, queryType, out var records))
                {
                    return (false, Array.Empty<IPAddress>());
                }

                var addresses = FollowAnswerGraph(currentName, queryType, records, out var terminalName);
                if (addresses.Count > 0 || terminalName == null)
                {
                    return (true, addresses);
                }

                currentName = terminalName;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                return (false, Array.Empty<IPAddress>());
            }
        }

        return (false, Array.Empty<IPAddress>());
    }

    private static bool TryReadValidatedAnswer(
        JsonElement root,
        string expectedName,
        int expectedType,
        out List<DohRecord> records)
    {
        records = new List<DohRecord>();
        // Status 0 = NOERROR; Status 3 = NXDOMAIN, which is a VALID authoritative answer that the
        // name does not exist publicly (the norm for wildcard probe labels) - treating it as an
        // invalid response used to misreport it as "DoH unavailable".
        if (!root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.Number ||
            status.GetInt32() is not (0 or 3) ||
            !root.TryGetProperty("Question", out var questions) || questions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var matchingQuestion = questions.EnumerateArray().Any(question =>
            question.TryGetProperty("name", out var name) &&
            question.TryGetProperty("type", out var type) &&
            NormalizeName(name.GetString() ?? string.Empty).Equals(expectedName, StringComparison.OrdinalIgnoreCase) &&
            type.ValueKind == JsonValueKind.Number && type.GetInt32() == expectedType);
        if (!matchingQuestion)
        {
            return false;
        }

        if (!root.TryGetProperty("Answer", out var answers))
        {
            return true;
        }

        if (answers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("name", out var name) || !answer.TryGetProperty("type", out var type) ||
                !answer.TryGetProperty("data", out var data) || type.ValueKind != JsonValueKind.Number ||
                data.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            records.Add(new DohRecord(
                NormalizeName(name.GetString() ?? string.Empty),
                type.GetInt32(),
                data.GetString() ?? string.Empty));
        }

        return true;
    }

    private static IReadOnlyList<IPAddress> FollowAnswerGraph(
        string initialName,
        int queryType,
        IReadOnlyList<DohRecord> records,
        out string? terminalName)
    {
        var current = initialName;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var depth = 0; depth <= MaxCnameDepth && visited.Add(current); depth++)
        {
            var addresses = records
                .Where(record => record.Type == queryType && record.Name.Equals(current, StringComparison.OrdinalIgnoreCase))
                .Select(record => IPAddress.TryParse(record.Data, out var address) ? address : null)
                .Where(static address => address != null)
                .Select(static address => address!)
                .Where(address =>
                    (queryType == 1 && address.AddressFamily == AddressFamily.InterNetwork) ||
                    (queryType == 28 && address.AddressFamily == AddressFamily.InterNetworkV6))
                .Where(PublicAddressSafety.IsPublic)
                .Distinct()
                .ToList();
            if (addresses.Count > 0)
            {
                terminalName = null;
                return addresses;
            }

            var cname = records.FirstOrDefault(record =>
                record.Type == 5 && record.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (cname == null)
            {
                terminalName = current.Equals(initialName, StringComparison.OrdinalIgnoreCase) ? null : current;
                return Array.Empty<IPAddress>();
            }

            current = NormalizeName(cname.Data);
        }

        terminalName = visited.Contains(current) ? null : current;
        return Array.Empty<IPAddress>();
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new JsonException("DoH response exceeded the bounded size.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static string NormalizeName(string value) => value.Trim().TrimEnd('.').ToLowerInvariant();

    private sealed record DohRecord(string Name, int Type, string Data);
}
