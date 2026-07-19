using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models.Responses;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class StatusCheckContentPathTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 20, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> KnownServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "epicgames", "blizzard", "riot", "wsus"
    };

    [Theory]
    [InlineData("steam", "/depot/123/chunk/abcdef0123456789", "cache1.example.net", "HIT")]
    [InlineData("epicgames", "/builds/Fortnite/CloudDir/ChunksV4/00/file.chunk", "download.example.net", "MISS")]
    [InlineData("blizzard", "/tpr/ow/data/ab/cd/0123456789abcdef", "hotfix.example.net", "MISS")]
    [InlineData("riot", "/channels/public/bundles/client.bundle", "valorant.example.net", "HIT")]
    public void MapRustRecord_RealCandidates_ExtractsSafeEvidence(
        string service,
        string target,
        string host,
        string cacheOutcome)
    {
        var record = RustSample(service, target, host, cacheOutcome: cacheOutcome);

        var mapped = ContentPathRecordFilter.TryMap(record, KnownServices, Now, out var sample);

        Assert.True(mapped);
        Assert.NotNull(sample);
        Assert.Equal(service, sample.Service);
        Assert.Equal(host, sample.Host);
        Assert.Equal(target, sample.Target);
        Assert.Equal(cacheOutcome.ToLowerInvariant(), sample.CacheOutcome);
        Assert.Equal(206, sample.StatusCode);
        Assert.Equal(1024, sample.Bytes);
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 19, 55, 0, TimeSpan.Zero), sample.ObservedAtUtc);
    }

    [Fact]
    public void MapRustRecord_RejectsNonEvidenceAndUnsafeRecords()
    {
        // The security boundary stays in C#: the Rust scan already applied the positive-cache gate
        // and dropped probe lines, but path/SSRF safety, host DNS normalization, the not-future
        // timestamp and the known-service gate must still reject a malformed candidate here.
        var rejected = new[]
        {
            RustSample(method: "HEAD"),
            RustSample(statusCode: 304),
            RustSample(bytes: 0),
            RustSample(cacheOutcome: "BYPASS"),
            RustSample(cacheOutcome: "-"),
            RustSample(service: "unknown"),
            RustSample(target: "/lancache-heartbeat"),
            RustSample(target: "/health"),
            RustSample(target: "/ping"),
            RustSample(userAgent: "lancache-manager-status-check/1.0"),
            RustSample(target: "/depot/file?token=secret"),
            RustSample(target: "/depot/file#fragment"),
            RustSample(target: "/depot/../secret"),
            RustSample(target: "/depot/%2e%2e/secret"),
            RustSample(target: "/depot/%252e%252e/secret"),
            RustSample(target: "/download/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_AbCdEfGhIjKlMnOp"),
            RustSample(target: "//evil.example.net/depot"),
            RustSample(host: "127.0.0.1"),
            RustSample(timestamp: "2126-07-10T19:55:00+00:00")
        };

        foreach (var record in rejected)
        {
            Assert.False(ContentPathRecordFilter.TryMap(record, KnownServices, Now, out _), record.Target);
        }
    }

    [Theory]
    [InlineData(200, 1, "HIT", true)]
    [InlineData(206, 1, "MISS", true)]
    [InlineData(304, 1, "HIT", false)]
    [InlineData(404, 1, "MISS", false)]
    [InlineData(200, 0, "HIT", false)]
    [InlineData(200, 1, "BYPASS", false)]
    public void BuildCacheObservation_RequiresSuccessfulPositiveByteHitOrMiss(
        int statusCode,
        long bytes,
        string cacheOutcome,
        bool expected)
    {
        Assert.Equal(expected, ContentPathRecordFilter.IsPositiveCacheEvidence(statusCode, bytes, cacheOutcome));
    }

    [Fact]
    public void SelectSamples_IsExactDeterministicRecentAndCappedPerService()
    {
        var samples = new[]
        {
            Sample("steam", "a.example.net", "/old", Now.AddHours(-4)),
            Sample("steam", "a.example.net", "/new", Now.AddHours(-1), "hit"),
            Sample("steam", "a.example.net", "/new", Now.AddHours(-2)),
            Sample("steam", "b.example.net", "/b", Now.AddHours(-2)),
            Sample("steam", "c.example.net", "/c", Now.AddHours(-3)),
            Sample("steam", "d.example.net", "/d", Now.AddHours(-5)),
            Sample("steam", "expired.example.net", "/expired", Now.AddDays(-31)),
            Sample("riot", "r.example.net", "/bundle", Now.AddMinutes(-5))
        };

        var selected = ContentPathSampleSelector.Select(samples, Now);

        Assert.Equal(4, selected.Count);
        Assert.Collection(
            selected,
            sample => Assert.Equal(("riot", "r.example.net", "/bundle"), (sample.Service, sample.Host, sample.Target)),
            sample => Assert.Equal(("steam", "a.example.net", "/new"), (sample.Service, sample.Host, sample.Target)),
            sample => Assert.Equal(("steam", "b.example.net", "/b"), (sample.Service, sample.Host, sample.Target)),
            sample => Assert.Equal(("steam", "c.example.net", "/c"), (sample.Service, sample.Host, sample.Target)));
        Assert.Equal("hit", selected.Single(sample => sample.Host == "a.example.net").CacheOutcome);
    }

    [Fact]
    public async Task RustContentScanner_AggregatesRecordsAndAvailabilityAcrossDirectories()
    {
        // available beats unreadable beats logMissing, records concatenate, bytes sum, and any
        // truncation flag survives. The per-directory Rust scan is injected, so no real process runs.
        var scanned = new List<string>();
        var scanner = new RustContentPathScanner((directory, _) =>
        {
            scanned.Add(directory);
            return Task.FromResult(directory switch
            {
                "readable" => new RustContentScanResult
                {
                    Availability = "available",
                    ScannedBytes = 100,
                    Truncated = true,
                    Records = new[] { RustSample(service: "steam") }
                },
                "unreadable" => new RustContentScanResult
                {
                    Availability = "unreadable",
                    ScannedBytes = 5,
                    Records = new[] { RustSample(service: "riot", host: "valorant.example.net") }
                },
                _ => new RustContentScanResult { Availability = "logMissing" }
            });
        });

        var scan = await scanner.ScanAsync(new[] { "readable", "unreadable", "missing" }, CancellationToken.None);

        Assert.Equal(new[] { "readable", "unreadable", "missing" }, scanned);
        Assert.Equal("available", scan.Availability);
        Assert.True(scan.ScanTruncated);
        Assert.Equal(105, scan.ScannedBytes);
        Assert.Equal(2, scan.Records.Count);

        var missingOnly = await new RustContentPathScanner((_, _) =>
                Task.FromResult(new RustContentScanResult { Availability = "logMissing" }))
            .ScanAsync(new[] { "a", "b" }, CancellationToken.None);
        Assert.Equal("logMissing", missingOnly.Availability);
        Assert.Empty(missingOnly.Records);

        var unreadableOnly = await new RustContentPathScanner((_, _) =>
                Task.FromResult(new RustContentScanResult { Availability = "unreadable" }))
            .ScanAsync(new[] { "a" }, CancellationToken.None);
        Assert.Equal("unreadable", unreadableOnly.Availability);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::c000:201")]
    [InlineData("100::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff00::1")]
    public void PublicAddressSafety_RejectsNonGlobalAndSpecialAddresses(string value)
    {
        Assert.False(PublicAddressSafety.IsPublic(IPAddress.Parse(value)), value);
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4009:80b::200e")]
    public void PublicAddressSafety_AcceptsGlobalAddresses(string value)
    {
        Assert.True(PublicAddressSafety.IsPublic(IPAddress.Parse(value)), value);
    }

    [Fact]
    public async Task ResolvePublicEdges_QueriesBothProvidersForAAndAaaaAndDeduplicates()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            var type = GetQueryValue(request.RequestUri!, "type");
            var answer = type == "A"
                ? "{\"name\":\"cdn.example.com.\",\"type\":1,\"data\":\"93.184.216.34\"}"
                : "{\"name\":\"cdn.example.com.\",\"type\":28,\"data\":\"2606:2800:220:1:248:1893:25c8:1946\"}";
            return Task.FromResult(JsonResponse(
                $"{{\"Status\":0,\"Question\":[{{\"name\":\"cdn.example.com.\",\"type\":{(type == "A" ? 1 : 28)}}}],\"Answer\":[{answer}]}}"));
        }));
        var resolver = new PublicDohResolver(client, TimeSpan.FromSeconds(2));

        var result = await resolver.ResolveAsync("cdn.example.com", CancellationToken.None);

        Assert.Equal(4, requests.Count);
        Assert.Equal(2, requests.Count(uri => uri.Host.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, requests.Count(uri => uri.Host.Contains("google", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(requests, uri => GetQueryValue(uri, "type") == "A");
        Assert.Contains(requests, uri => GetQueryValue(uri, "type") == "AAAA");
        Assert.Equal(new[] { "2606:2800:220:1:248:1893:25c8:1946", "93.184.216.34" },
            result.Addresses.Select(address => address.ToString()).Order(StringComparer.Ordinal));
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task ResolvePublicEdges_FollowsBoundedCnameChains()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            var name = GetQueryValue(request.RequestUri!, "name");
            var type = GetQueryValue(request.RequestUri!, "type");
            if (type == "A" && name == "cdn.example.com")
            {
                return Task.FromResult(JsonResponse(
                    "{\"Status\":0,\"Question\":[{\"name\":\"cdn.example.com.\",\"type\":1}],\"Answer\":[{\"name\":\"cdn.example.com.\",\"type\":5,\"data\":\"edge.example.net.\"}]}"));
            }

            if (type == "A" && name == "edge.example.net")
            {
                return Task.FromResult(JsonResponse(
                    "{\"Status\":0,\"Question\":[{\"name\":\"edge.example.net.\",\"type\":1}],\"Answer\":[{\"name\":\"edge.example.net.\",\"type\":1,\"data\":\"93.184.216.34\"}]}"));
            }

            return Task.FromResult(JsonResponse(
                $"{{\"Status\":0,\"Question\":[{{\"name\":\"{name}.\",\"type\":{(type == "A" ? 1 : 28)}}}]}}"));
        }));
        var resolver = new PublicDohResolver(client, TimeSpan.FromSeconds(2));

        var result = await resolver.ResolveAsync("cdn.example.com", CancellationToken.None);

        Assert.Contains(IPAddress.Parse("93.184.216.34"), result.Addresses);
        Assert.Contains(requests, uri => GetQueryValue(uri, "name") == "edge.example.net");
    }

    [Fact]
    public async Task ResolvePublicEdges_NxdomainIsAValidEmptyAnswerNotDohUnavailable()
    {
        // v1.5: Status 3 (NXDOMAIN) is an authoritative "this name does not exist publicly" -
        // the norm for wildcard probe labels - and must classify as noPublicEdges, never as a
        // DoH transport failure.
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var type = GetQueryValue(request.RequestUri!, "type");
            return Task.FromResult(JsonResponse(
                $"{{\"Status\":3,\"Question\":[{{\"name\":\"status-check.example.com.\",\"type\":{(type == "A" ? 1 : 28)}}}]}}"));
        }));
        var resolver = new PublicDohResolver(client, TimeSpan.FromSeconds(2));

        var result = await resolver.ResolveAsync("status-check.example.com", CancellationToken.None);

        Assert.Empty(result.Addresses);
        Assert.Equal("noPublicEdges", result.FailureReason);
    }

    [Fact]
    public async Task ResolvePublicEdges_PrivateOnlyAnswersNeverEscapeSafetyFilter()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var type = GetQueryValue(request.RequestUri!, "type");
            var data = type == "A" ? "10.0.0.5" : "fc00::5";
            return Task.FromResult(JsonResponse(
                $"{{\"Status\":0,\"Question\":[{{\"name\":\"cdn.example.com.\",\"type\":{(type == "A" ? 1 : 28)}}}],\"Answer\":[{{\"name\":\"cdn.example.com.\",\"type\":{(type == "A" ? 1 : 28)},\"data\":\"{data}\"}}]}}"));
        }));
        var resolver = new PublicDohResolver(client, TimeSpan.FromSeconds(2));

        var result = await resolver.ResolveAsync("cdn.example.com", CancellationToken.None);

        Assert.Empty(result.Addresses);
        Assert.Equal("noPublicEdges", result.FailureReason);
    }

    [Fact]
    public async Task ResolvePublicEdges_MoreThanEightUniqueEdgesIsBoundedAndFlagged()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var type = GetQueryValue(request.RequestUri!, "type");
            var answers = type == "A"
                ? string.Join(',', Enumerable.Range(1, 9).Select(index =>
                    $"{{\"name\":\"cdn.example.com.\",\"type\":1,\"data\":\"93.184.216.{index}\"}}"))
                : string.Empty;
            return Task.FromResult(JsonResponse(
                $"{{\"Status\":0,\"Question\":[{{\"name\":\"cdn.example.com.\",\"type\":{(type == "A" ? 1 : 28)}}}],\"Answer\":[{answers}]}}"));
        }));
        var resolver = new PublicDohResolver(client, TimeSpan.FromSeconds(2));

        var result = await resolver.ResolveAsync("cdn.example.com", CancellationToken.None);

        Assert.True(result.TooManyAddresses);
        Assert.Equal(9, result.TotalAddresses);
        Assert.Equal(8, result.Addresses.Count);
        Assert.Equal("tooManyEdges", result.FailureReason);
    }

    [Fact]
    public async Task ProbeProtocol_PreservesLogicalHostPinsHandlerAndReadsAtMostOneByte()
    {
        SocketsHttpHandler? configuredHandler = null;
        RecordedRequest? recorded = null;
        var body = new CountingReadStream(Encoding.UTF8.GetBytes("large-response-body"));
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(body)
        };
        var probe = new DirectContentProbe(
            handlerFactory: handler =>
            {
                configuredHandler = handler;
                return new StubHttpMessageHandler((request, _) =>
                {
                    recorded = RecordedRequest.From(request);
                    return Task.FromResult(response);
                });
            },
            connector: null,
            requestTimeout: TimeSpan.FromSeconds(2));

        var result = await probe.ProbeProtocolAsync(
            Sample("steam", "cdn.example.com", "/depot/1/chunk/abc", Now),
            IPAddress.Parse("93.184.216.34"),
            "https",
            CancellationToken.None);

        Assert.Equal("content", result.Outcome);
        Assert.Equal(206, result.StatusCode);
        Assert.NotNull(recorded);
        Assert.Equal("https", recorded.Scheme);
        Assert.Equal("cdn.example.com", recorded.Host);
        Assert.Equal(443, recorded.Port);
        Assert.Equal("/depot/1/chunk/abc", recorded.PathAndQuery);
        Assert.Equal("cdn.example.com", recorded.HostHeader);
        Assert.Equal("bytes=0-0", recorded.Range);
        Assert.Contains("identity", recorded.AcceptEncoding, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lancache-manager-status-check", recorded.UserAgent, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(configuredHandler);
        Assert.False(configuredHandler.UseProxy);
        Assert.False(configuredHandler.AllowAutoRedirect);
        Assert.False(configuredHandler.UseCookies);
        Assert.Equal(DecompressionMethods.None, configuredHandler.AutomaticDecompression);
        Assert.Null(configuredHandler.Credentials);
        Assert.NotNull(configuredHandler.ConnectCallback);
        Assert.Null(configuredHandler.SslOptions.RemoteCertificateValidationCallback);
        Assert.InRange(body.BytesRead, 0, 1);
        Assert.InRange(body.LargestRequestedRead, 0, 1);
    }

    [Fact]
    public async Task ConnectToSelectedAddress_UsesOnlySelectedIpAndStandardPort()
    {
        IPAddress? connectedAddress = null;
        var connectedPort = 0;
        ContentSocketConnector connector = (address, port, _) =>
        {
            connectedAddress = address;
            connectedPort = port;
            return ValueTask.FromResult<Stream>(new MemoryStream());
        };

        await using var stream = await DirectContentProbe.ConnectToSelectedAddressAsync(
            IPAddress.Parse("93.184.216.34"), 443, connector, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("93.184.216.34"), connectedAddress);
        Assert.Equal(443, connectedPort);
    }

    [Fact]
    public async Task ProbeProtocol_RejectsPrivateAddressBeforeCreatingRequest()
    {
        var invoked = false;
        var probe = new DirectContentProbe(
            handlerFactory: handler =>
            {
                invoked = true;
                return handler;
            },
            connector: null,
            requestTimeout: TimeSpan.FromSeconds(2));

        var result = await probe.ProbeProtocolAsync(
            Sample("steam", "cdn.example.com", "/content", Now),
            IPAddress.Parse("10.0.0.5"),
            "http",
            CancellationToken.None);

        Assert.False(invoked);
        Assert.Equal("invalidResponse", result.Outcome);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task ProbeProtocol_DoesNotFollowRedirectAndReturnsBoundedMetadata()
    {
        var sends = 0;
        var probe = new DirectContentProbe(
            handlerFactory: _ => new StubHttpMessageHandler((_, _) =>
            {
                sends++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently)
                {
                    Headers = { Location = new Uri("https://cdn.example.com/content") }
                });
            }),
            connector: null,
            requestTimeout: TimeSpan.FromSeconds(2));

        var result = await probe.ProbeProtocolAsync(
            Sample("steam", "cdn.example.com", "/content", Now),
            IPAddress.Parse("93.184.216.34"),
            "http",
            CancellationToken.None);

        Assert.Equal(1, sends);
        Assert.Equal("redirectToHttps", result.Outcome);
        Assert.Equal(301, result.StatusCode);
        Assert.Equal("https", result.RedirectScheme);
    }

    [Fact]
    public async Task ProbeProtocol_CertificateAndTimeoutFailuresAreTypedWithoutRawText()
    {
        var certificateProbe = new DirectContentProbe(
            handlerFactory: _ => new StubHttpMessageHandler((_, _) =>
                throw new HttpRequestException("secret.example/path", new AuthenticationException("private detail"))),
            connector: null,
            requestTimeout: TimeSpan.FromSeconds(2));
        var timeoutProbe = new DirectContentProbe(
            handlerFactory: _ => new StubHttpMessageHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            }),
            connector: null,
            requestTimeout: TimeSpan.FromMilliseconds(20));

        var certificate = await certificateProbe.ProbeProtocolAsync(
            Sample("steam", "cdn.example.com", "/content", Now),
            IPAddress.Parse("93.184.216.34"),
            "https",
            CancellationToken.None);
        var timeout = await timeoutProbe.ProbeProtocolAsync(
            Sample("steam", "cdn.example.com", "/content", Now),
            IPAddress.Parse("93.184.216.34"),
            "https",
            CancellationToken.None);
        var json = JsonSerializer.Serialize(new[] { certificate, timeout });

        Assert.Equal("tlsCertificateFailure", certificate.Outcome);
        Assert.Equal("timeout", timeout.Outcome);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private detail", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeProtocol_OperationCancellationRemainsTerminal()
    {
        var probe = new DirectContentProbe(
            handlerFactory: _ => new StubHttpMessageHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            }),
            connector: null,
            requestTimeout: TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeProtocolAsync(
            Sample("steam", "cdn.example.com", "/content", Now),
            IPAddress.Parse("93.184.216.34"),
            "https",
            cts.Token));
    }

    [Theory]
    [InlineData("content", "content", "bothUsable")]
    [InlineData("content", "tlsCertificateFailure", "httpUsable")]
    [InlineData("denied", "content", "httpsOnlyCandidate")]
    [InlineData("redirectToHttps", "content", "httpsOnlyCandidate")]
    [InlineData("connectFailure", "content", "httpsOnlyCandidate")]
    [InlineData("serverError", "content", "httpsOnlyCandidate")]
    // v1.5 transport tier: no content either way (a CDN 403/404s a root-path probe), so the
    // verdict reads whether each protocol ANSWERED at all.
    [InlineData("denied", "denied", "bothUsable")]
    [InlineData("notFoundOrStale", "notFoundOrStale", "bothUsable")]
    [InlineData("denied", "timeout", "httpUsable")]
    [InlineData("timeout", "denied", "httpsOnlyCandidate")]
    [InlineData("redirectToHttps", "denied", "httpsOnlyCandidate")]
    public void Consensus_UnanimousTwoEdgeOutcomes_ReturnExpectedStatus(
        string httpOutcome,
        string httpsOutcome,
        string expected)
    {
        var result = ContentPathConsensus.Classify(new[]
        {
            Edge("93.184.216.34", httpOutcome, httpsOutcome),
            Edge("93.184.216.35", httpOutcome, httpsOutcome)
        });

        Assert.Equal(expected, result.Status);
        Assert.Equal(2, result.ConsensusEdges);
    }

    [Fact]
    public void Consensus_SingleEdgeDecidesButDisagreementRemainsInconclusive()
    {
        // v1.5: many hosts publish exactly one public address - the only probed edge decides,
        // with ConsensusEdges=1 keeping the sample size visible. Real disagreement between
        // edges still refuses a verdict.
        var one = ContentPathConsensus.Classify(new[]
        {
            Edge("93.184.216.34", "content", "content")
        });
        var none = ContentPathConsensus.Classify(
            Array.Empty<ContentPathEdgeResult>(),
            resolutionFailureReason: "noPublicEdges");
        var disagreement = ContentPathConsensus.Classify(new[]
        {
            Edge("93.184.216.34", "content", "content"),
            Edge("93.184.216.35", "content", "tlsCertificateFailure")
        });

        Assert.Equal("bothUsable", one.Status);
        Assert.Equal(1, one.ConsensusEdges);
        Assert.Equal("inconclusive", none.Status);
        Assert.Equal("noPublicEdges", none.Reason);
        Assert.Equal("inconclusive", disagreement.Status);
        Assert.Equal("edgeDisagreement", disagreement.Reason);
        Assert.Equal(0, disagreement.ConsensusEdges);
    }

    [Fact]
    public void Consensus_NeitherProtocolAnsweringStaysNonDefinitive()
    {
        var result = ContentPathConsensus.Classify(new[]
        {
            Edge("93.184.216.34", "timeout", "tlsCertificateFailure"),
            Edge("93.184.216.35", "timeout", "tlsCertificateFailure")
        });

        Assert.Equal("inconclusive", result.Status);
        Assert.Equal("nonDefinitiveEdges", result.Reason);
        Assert.Equal(0, result.ConsensusEdges);
    }

    [Fact]
    public void Consensus_UnreachableAddressFamilyDoesNotPoisonTheReachableOne()
    {
        // v1.5: IPv6 edges probed from an IPv4-only Docker bridge fail to CONNECT on both
        // protocols. That says nothing about the publisher - the family is discarded instead of
        // reading as edge disagreement, so IPv4-only deployments still reach verdicts.
        var ipv6Unreachable = ContentPathConsensus.Classify(new[]
        {
            Edge("93.184.216.34", "content", "content"),
            Edge("93.184.216.35", "content", "content"),
            EdgeV6("2606:2800:220:1::1", "connectFailure", "connectFailure")
        });
        // Any non-connect outcome in a family is real publisher signal - it still participates
        // (and here still produces a genuine disagreement).
        var ipv6RealSignal = ContentPathConsensus.Classify(new[]
        {
            Edge("93.184.216.34", "content", "content"),
            EdgeV6("2606:2800:220:1::1", "connectFailure", "tlsCertificateFailure")
        });

        Assert.Equal("bothUsable", ipv6Unreachable.Status);
        Assert.Equal(2, ipv6Unreachable.ConsensusEdges);
        Assert.Equal("inconclusive", ipv6RealSignal.Status);
        Assert.Equal("edgeDisagreement", ipv6RealSignal.Reason);
    }

    [Fact]
    public async Task ProbeHost_ReusesTheSharedPipelineAgainstTheRootPath()
    {
        var addresses = new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.35") };
        string? probedTarget = null;
        var service = new ContentPathCheckService(
            () => Array.Empty<ResolvedDatasource>(),
            (_, _) => Task.FromResult(LogMissingScan()),
            (_, _) => Task.FromResult(new DohResolutionResult(addresses, addresses.Length, false, null)),
            (sample, address, _) =>
            {
                probedTarget = sample.Target;
                return Task.FromResult(Edge(address.ToString(), "redirectToHttps", "content"));
            },
            new FixedTimeProvider(Now),
            NullLogger<ContentPathCheckService>.Instance);

        var result = await service.ProbeHostAsync("cdn.example.com", CancellationToken.None);

        Assert.Equal("/", probedTarget);
        Assert.Equal("httpsOnlyCandidate", result.ProtocolStatus);
        Assert.Equal("httpsOnlyRedirect", result.ProtocolReason);
        Assert.Equal(2, result.ConsensusEdges);
        Assert.Equal(2, result.TotalPublicEdges);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public async Task ContentPathCheck_ExactCacheEvidenceRemainsBesideCurrentProtocolStatus()
    {
        var addresses = new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.35") };
        var scan = AvailableScan(RustSample("steam", "/depot/123/chunk/abcdef0123456789", "cache1.example.net"));
        var service = new ContentPathCheckService(
            () => new[] { new ResolvedDatasource { Enabled = true, LogPath = TempDir() } },
            (_, _) => Task.FromResult(scan),
            (_, _) => Task.FromResult(new DohResolutionResult(addresses, addresses.Length, false, null)),
            (sample, address, _) => Task.FromResult(Edge(address.ToString(), "denied", "content")),
            new FixedTimeProvider(Now),
            NullLogger<ContentPathCheckService>.Instance);

        var report = await service.CheckAsync(
            new[] { new CacheDomainService { Name = "steam" } },
            CancellationToken.None);

        var result = Assert.Single(report.Paths);
        Assert.Equal("available", report.Availability);
        Assert.Equal("steam", result.Service);
        Assert.Equal("cache1.example.net", result.Host);
        Assert.Equal("/depot/123/chunk/abcdef0123456789", result.PathDisplay);
        Assert.Equal("hit", result.CacheEvidence?.Outcome);
        Assert.Equal("httpsOnlyCandidate", result.ProtocolStatus);
        Assert.Equal(2, result.ConsensusEdges);
        Assert.Equal(2, result.TotalPublicEdges);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public async Task ContentPathCheck_PerServiceHttpDetailedRecordsFlowThrough()
    {
        // Records the Rust scan produced from a bare-metal per-service (http-detailed) source carry
        // no format marker at the C# boundary - a candidate is a candidate. Two such records for
        // different services must both survive the filters and be probed.
        var addresses = new[] { IPAddress.Parse("93.184.216.34") };
        var scan = AvailableScan(
            RustSample("steam", "/depot/42/chunk/aabbccddeeff0011", "cache.steamcontent.com"),
            RustSample("riot", "/channels/public/bundles/x.bundle", "lol.dyn.riotcdn.net", cacheOutcome: "MISS"));
        var service = new ContentPathCheckService(
            () => new[] { new ResolvedDatasource { Enabled = true, LogPath = TempDir() } },
            (_, _) => Task.FromResult(scan),
            (_, _) => Task.FromResult(new DohResolutionResult(addresses, addresses.Length, false, null)),
            (sample, address, _) => Task.FromResult(Edge(address.ToString(), "content", "content")),
            new FixedTimeProvider(Now),
            NullLogger<ContentPathCheckService>.Instance);

        var report = await service.CheckAsync(
            new[] { new CacheDomainService { Name = "steam" }, new CacheDomainService { Name = "riot" } },
            CancellationToken.None);

        Assert.Equal("available", report.Availability);
        Assert.Equal(2, report.Paths.Count);
        Assert.Contains(report.Paths, path => path.Service == "steam" && path.Host == "cache.steamcontent.com");
        Assert.Contains(report.Paths, path => path.Service == "riot" && path.Host == "lol.dyn.riotcdn.net");
    }

    [Fact]
    public async Task ContentPathCheck_NoRealCandidateMakesNoNetworkCalls()
    {
        var resolveCalls = 0;
        var probeCalls = 0;
        var service = new ContentPathCheckService(
            () => new[] { new ResolvedDatasource { Enabled = true, LogPath = TempDir() } },
            (_, _) => Task.FromResult(AvailableScan()),
            (_, _) =>
            {
                resolveCalls++;
                return Task.FromResult(new DohResolutionResult(Array.Empty<IPAddress>(), 0, false, "noPublicEdges"));
            },
            (_, _, _) =>
            {
                probeCalls++;
                throw new InvalidOperationException();
            },
            new FixedTimeProvider(Now),
            NullLogger<ContentPathCheckService>.Instance);

        var report = await service.CheckAsync(
            new[] { new CacheDomainService { Name = "steam" } },
            CancellationToken.None);

        // A readable log with zero recognizable samples is the typed "noSamples"
        // state, never "available" with nothing behind it - and still no network.
        Assert.Equal("noSamples", report.Availability);
        Assert.Empty(report.Paths);
        Assert.Equal(0, resolveCalls);
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public async Task ContentPathCheck_InternalScanTimeoutIsFailSoftUnreadableNotCancellation()
    {
        // A stalled Rust child that overruns the internal content-scan deadline surfaces as a
        // TimeoutException, NOT an OperationCanceledException. The caller did not cancel, so the
        // check must fold it into the fail-soft "unreadable" state and never let it propagate.
        var probeCalls = 0;
        var service = new ContentPathCheckService(
            () => new[] { new ResolvedDatasource { Enabled = true, LogPath = TempDir() } },
            (_, _) => throw new TimeoutException("scan-content exceeded the content-scan deadline"),
            (_, _) => Task.FromResult(new DohResolutionResult(Array.Empty<IPAddress>(), 0, false, null)),
            (_, _, _) =>
            {
                probeCalls++;
                throw new InvalidOperationException();
            },
            new FixedTimeProvider(Now),
            NullLogger<ContentPathCheckService>.Instance);

        var report = await service.CheckAsync(
            new[] { new CacheDomainService { Name = "steam" } },
            CancellationToken.None);

        Assert.Equal("unreadable", report.Availability);
        Assert.Empty(report.Paths);
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public async Task ContentPathCheck_UserCancellationDuringScanRemainsTerminal()
    {
        // User cancellation of the sweep is terminal: an OperationCanceledException tied to the
        // caller token propagates, never masquerading as the fail-soft "unreadable" state that an
        // internal timeout produces.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new ContentPathCheckService(
            () => new[] { new ResolvedDatasource { Enabled = true, LogPath = TempDir() } },
            (_, token) => throw new OperationCanceledException(token),
            (_, _) => Task.FromResult(new DohResolutionResult(Array.Empty<IPAddress>(), 0, false, null)),
            (_, _, _) => throw new InvalidOperationException(),
            new FixedTimeProvider(Now),
            NullLogger<ContentPathCheckService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(
            new[] { new CacheDomainService { Name = "steam" } },
            cts.Token));
    }

    [Fact]
    public void ContentReport_OldJsonGetsSafeDefaultAndNewJsonContainsNoSensitiveFields()
    {
        var old = JsonSerializer.Deserialize<StatusCheckResult>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(old);
        Assert.NotNull(old.ContentReport);
        Assert.Equal("logMissing", old.ContentReport.Availability);
        Assert.Empty(old.ContentReport.Paths);

        old.ContentReport = new StatusCheckContentReport
        {
            Availability = "available",
            Paths = new List<ContentPathCheckResult>
            {
                new()
                {
                    Service = "steam",
                    Host = "cdn.example.com",
                    PathDisplay = "/depot/1/chunk/abc",
                    CacheEvidence = new CacheTraversalEvidence { Outcome = "miss", StatusCode = 206, Bytes = 1024 }
                }
            }
        };
        var json = JsonSerializer.Serialize(old, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"contentReport\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("clientIp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userAgent", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("referer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeartbeatSuccess_Requires204AndNonEmptyServedByHeader()
    {
        using var valid = new HttpResponseMessage(HttpStatusCode.NoContent);
        valid.Headers.TryAddWithoutValidation("X-LanCache-Processed-By", "lancache-a");
        using var wrongStatus = new HttpResponseMessage(HttpStatusCode.OK);
        wrongStatus.Headers.TryAddWithoutValidation("X-LanCache-Processed-By", "lancache-a");
        using var blankHeader = new HttpResponseMessage(HttpStatusCode.NoContent);
        blankHeader.Headers.TryAddWithoutValidation("X-LanCache-Processed-By", "   ");

        Assert.True(LancacheServerLocator.IsHeartbeatSuccess(valid, out var servedBy));
        Assert.Equal("lancache-a", servedBy);
        Assert.False(LancacheServerLocator.IsHeartbeatSuccess(wrongStatus, out _));
        Assert.False(LancacheServerLocator.IsHeartbeatSuccess(blankHeader, out _));
    }

    private static RustContentSample RustSample(
        string service = "steam",
        string target = "/depot/123/chunk/abcdef0123456789",
        string host = "cache1.example.net",
        string method = "GET",
        int statusCode = 206,
        long bytes = 1024,
        string cacheOutcome = "HIT",
        string userAgent = "Valve/Steam",
        string? timestamp = null) => new()
    {
        Service = service,
        Target = target,
        Host = host,
        Method = method,
        StatusCode = statusCode,
        Bytes = bytes,
        CacheStatus = cacheOutcome,
        UserAgent = userAgent,
        Timestamp = timestamp ?? "2026-07-10T19:55:00+00:00"
    };

    private static ContentPathRawScan AvailableScan(params RustContentSample[] records) =>
        new("available", false, records.Length == 0 ? 0 : 128, records);

    private static ContentPathRawScan LogMissingScan() =>
        new("logMissing", false, 0, Array.Empty<RustContentSample>());

    private static string TempDir() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private static ContentPathSample Sample(
        string service,
        string host,
        string target,
        DateTimeOffset observedAt,
        string cacheOutcome = "miss") =>
        new(service, host, target, observedAt, cacheOutcome, 206, 1024);

    private static ContentPathEdgeResult Edge(string address, string httpOutcome, string httpsOutcome) => new()
    {
        Address = address,
        AddressFamily = "ipv4",
        Http = new ProtocolProbeResult { Outcome = httpOutcome, StatusCode = httpOutcome == "content" ? 206 : 403 },
        Https = new ProtocolProbeResult { Outcome = httpsOutcome, StatusCode = httpsOutcome == "content" ? 206 : null }
    };

    private static ContentPathEdgeResult EdgeV6(string address, string httpOutcome, string httpsOutcome)
    {
        var edge = Edge(address, httpOutcome, httpsOutcome);
        edge.AddressFamily = "ipv6";
        return edge;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/dns-json")
    };

    private static string GetQueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == key)
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return string.Empty;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed record RecordedRequest(
        string Scheme,
        string Host,
        int Port,
        string PathAndQuery,
        string? HostHeader,
        string Range,
        string AcceptEncoding,
        string UserAgent)
    {
        internal static RecordedRequest From(HttpRequestMessage request) => new(
            request.RequestUri!.Scheme,
            request.RequestUri.Host,
            request.RequestUri.Port,
            request.RequestUri.PathAndQuery,
            request.Headers.Host,
            request.Headers.Range?.ToString() ?? string.Empty,
            string.Join(",", request.Headers.AcceptEncoding.Select(value => value.ToString())),
            request.Headers.UserAgent.ToString());
    }

    private sealed class CountingReadStream(byte[] data) : MemoryStream(data)
    {
        private int _bytesRead;

        internal int BytesRead => _bytesRead;
        internal int LargestRequestedRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRequestedRead = Math.Max(LargestRequestedRead, count);
            var read = base.Read(buffer, offset, count);
            _bytesRead = Math.Max(_bytesRead, checked((int)base.Position));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            LargestRequestedRead = Math.Max(LargestRequestedRead, buffer.Length);
            var read = await base.ReadAsync(buffer, cancellationToken);
            _bytesRead = Math.Max(_bytesRead, checked((int)base.Position));
            return read;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
