using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Models;
using LancacheManager.Models.Responses;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for the pure/static-testable logic behind the Status Check feature: the .env parser,
/// the GitHub repo URL -> raw.githubusercontent.com conversion, the tolerant cache_domains.json
/// parser (all 4 shapes, per the monolithic fork's Go parser design), the domain-file line parser,
/// and the domain/service/summary verdict rules.
/// </summary>
public class StatusCheckTests
{
    // ===== LancacheEnvFileReader.ParseEnvLines =====

    [Fact]
    public void ParseEnvLines_SkipsBlankAndCommentLines()
    {
        var lines = new[]
        {
            "# a comment",
            "",
            "   ",
            "CACHE_DOMAINS_REPO=https://github.com/uklans/cache-domains.git",
            "# NOFETCH=true"
        };

        var result = LancacheEnvFileReader.ParseEnvLines(lines);

        Assert.Single(result);
        Assert.Equal("https://github.com/uklans/cache-domains.git", result["CACHE_DOMAINS_REPO"]);
    }

    [Fact]
    public void ParseEnvLines_TrimsWhitespaceAndQuotes()
    {
        var lines = new[]
        {
            "  NOFETCH = \"true\"  ",
            "CACHE_DOMAINS_BRANCH='develop'"
        };

        var result = LancacheEnvFileReader.ParseEnvLines(lines);

        Assert.Equal("true", result["NOFETCH"]);
        Assert.Equal("develop", result["CACHE_DOMAINS_BRANCH"]);
    }

    [Fact]
    public void ParseEnvLines_DropsInlineCommentsFromUnquotedValues()
    {
        var lines = new[]
        {
            "DISABLE_STEAM=true # temporarily off",
            "DISABLE_RIOT=true\t# tab-separated note",
            "CACHE_DOMAINS_BRANCH=\"master # not a comment\"",
            "PASSWORD=p#ssword"
        };

        var result = LancacheEnvFileReader.ParseEnvLines(lines);

        Assert.Equal("true", result["DISABLE_STEAM"]);
        Assert.Equal("true", result["DISABLE_RIOT"]);
        // Quoted values keep their # intact; an unquoted # NOT preceded by whitespace is data.
        Assert.Equal("master # not a comment", result["CACHE_DOMAINS_BRANCH"]);
        Assert.Equal("p#ssword", result["PASSWORD"]);
    }

    [Fact]
    public void ParseEnvLines_KeyLookupIsCaseInsensitive()
    {
        var result = LancacheEnvFileReader.ParseEnvLines(new[] { "cache_domains_repo=https://example.com/repo.git" });

        Assert.True(result.TryGetValue("CACHE_DOMAINS_REPO", out var value));
        Assert.Equal("https://example.com/repo.git", value);
    }

    [Fact]
    public void ParseEnvLines_IgnoresLinesWithoutEquals()
    {
        var result = LancacheEnvFileReader.ParseEnvLines(new[] { "NOT_A_VALID_LINE", "=NoKey" });

        Assert.Empty(result);
    }

    // ===== CacheDomainsService.TryConvertToRawBaseUrl =====

    [Theory]
    [InlineData("https://github.com/uklans/cache-domains.git", "master", "https://raw.githubusercontent.com/uklans/cache-domains/master/")]
    [InlineData("https://github.com/uklans/cache-domains", "main", "https://raw.githubusercontent.com/uklans/cache-domains/main/")]
    [InlineData("git@github.com:uklans/cache-domains.git", "master", "https://raw.githubusercontent.com/uklans/cache-domains/master/")]
    public void TryConvertToRawBaseUrl_AcceptsRecognizedGitHubForms(string repoUrl, string branch, string expected)
    {
        var result = CacheDomainsService.TryConvertToRawBaseUrl(repoUrl, branch);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://gitlab.com/uklans/cache-domains.git", "master")]
    [InlineData("not a url", "master")]
    [InlineData("", "master")]
    public void TryConvertToRawBaseUrl_RejectsNonGitHubUrls(string repoUrl, string branch)
    {
        var result = CacheDomainsService.TryConvertToRawBaseUrl(repoUrl, branch);

        Assert.Null(result);
    }

    // ===== CacheDomainsService.ParseBool =====

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    public void ParseBool_ParsesKnownValues(string value, bool expected)
    {
        Assert.Equal(expected, EnvValueParsing.ParseBool(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    public void ParseBool_ReturnsNullForUnknownOrAbsentValues(string? value)
    {
        Assert.Null(EnvValueParsing.ParseBool(value));
    }

    // ===== CacheDomainsService.ParseCacheDomainsJson (4 tolerant shapes) =====

    [Fact]
    public void ParseCacheDomainsJson_Format1_CanonicalArray()
    {
        const string json = """
        {
            "cache_domains": [
                { "name": "steam", "description": "Steam CDN", "domain_files": ["steam.txt"] },
                { "name": "riot", "description": "Riot CDN", "domain_files": ["riot.txt", "riot_extra.txt"] }
            ]
        }
        """;

        var result = CacheDomainsService.ParseCacheDomainsJson(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("steam", result[0].Name);
        Assert.Equal("Steam CDN", result[0].Description);
        Assert.Equal(new[] { "steam.txt" }, result[0].DomainFiles);
        Assert.Equal(new[] { "riot.txt", "riot_extra.txt" }, result[1].DomainFiles);
    }

    [Fact]
    public void ParseCacheDomainsJson_Format2_WrappedMap()
    {
        const string json = """
        {
            "cache_domains": {
                "steam": { "description": "Steam CDN", "domain_files": ["steam.txt"] }
            }
        }
        """;

        var result = CacheDomainsService.ParseCacheDomainsJson(json);

        Assert.Single(result);
        Assert.Equal("steam", result[0].Name);
        Assert.Equal(new[] { "steam.txt" }, result[0].DomainFiles);
    }

    [Fact]
    public void ParseCacheDomainsJson_Format3_NoWrapperKey()
    {
        const string json = """
        {
            "steam": { "description": "Steam CDN", "domain_files": ["steam.txt"] }
        }
        """;

        var result = CacheDomainsService.ParseCacheDomainsJson(json);

        Assert.Single(result);
        Assert.Equal("steam", result[0].Name);
    }

    [Fact]
    public void ParseCacheDomainsJson_Format4_BareArray()
    {
        const string json = """
        {
            "steam": ["steam.txt", "steam_extra.txt"]
        }
        """;

        var result = CacheDomainsService.ParseCacheDomainsJson(json);

        Assert.Single(result);
        Assert.Equal("steam", result[0].Name);
        Assert.Equal(new[] { "steam.txt", "steam_extra.txt" }, result[0].DomainFiles);
    }

    [Fact]
    public void ParseCacheDomainsJson_Format1_SkipsEntriesMissingName()
    {
        const string json = """
        {
            "cache_domains": [
                { "description": "no name here", "domain_files": ["x.txt"] },
                { "name": "riot", "domain_files": ["riot.txt"] }
            ]
        }
        """;

        var result = CacheDomainsService.ParseCacheDomainsJson(json);

        Assert.Single(result);
        Assert.Equal("riot", result[0].Name);
    }

    // ===== CacheDomainsService.ParseDomainFile =====

    [Fact]
    public void ParseDomainFile_SkipsBlankAndCommentLines_KeepsWildcardsVerbatim()
    {
        const string content = "steamcontent.com\n# a comment\n\n*.cdn.blizzard.com\r\nriotcdn.net\n";

        var result = CacheDomainsService.ParseDomainFile(content);

        Assert.Equal(new[] { "steamcontent.com", "*.cdn.blizzard.com", "riotcdn.net" }, result);
    }

    [Fact]
    public void ParseDomainFile_StripsTrailingInlineComments()
    {
        const string content = "cdn.example.com # regional\nsteamcontent.com\n";

        var result = CacheDomainsService.ParseDomainFile(content);

        Assert.Equal(new[] { "cdn.example.com", "steamcontent.com" }, result);
    }

    // ===== CacheDomainsService.DedupeDomainsPreservingOrder =====

    [Fact]
    public void DedupeDomainsPreservingOrder_CollapsesCaseInsensitiveDuplicatesAcrossFiles()
    {
        // Simulates two domain_files for one service repeating a host in different case - the
        // dedup must collapse both within-file and cross-file duplicates, keeping first-seen order,
        // so a service's TotalCount reflects the distinct domain count.
        var fileOneDomains = CacheDomainsService.ParseDomainFile("steamcontent.com\nSteamcontent.com\n");
        var fileTwoDomains = CacheDomainsService.ParseDomainFile("STEAMCONTENT.COM\nriotcdn.net\n");

        var merged = new List<string>();
        merged.AddRange(fileOneDomains);
        merged.AddRange(fileTwoDomains);

        var result = CacheDomainsService.DedupeDomainsPreservingOrder(merged);

        Assert.Equal(new[] { "steamcontent.com", "riotcdn.net" }, result);
    }

    // ===== StatusCheckService verdict rules =====

    // Contract amendment v1.4 decision table: heartbeat-verified > expected-IP match >
    // private-unverified / public-mismatched > unresolved.

    [Fact]
    public void BuildDomainStatus_NoResolvedIps_IsUnresolved()
    {
        var status = StatusCheckService.BuildDomainStatus(
            new List<string>(), new List<string> { "10.0.0.5" }, heartbeatVerified: false);

        Assert.Equal("unresolved", status);
    }

    [Fact]
    public void BuildDomainStatus_HeartbeatVerified_IsResolvedRegardlessOfExpectedList()
    {
        // A live X-LanCache-Processed-By answer outranks the (possibly wrong or empty) baseline.
        var withEmptyExpected = StatusCheckService.BuildDomainStatus(
            new List<string> { "172.16.2.98" }, new List<string>(), heartbeatVerified: true);
        var withNonMatchingExpected = StatusCheckService.BuildDomainStatus(
            new List<string> { "172.16.2.98" }, new List<string> { "10.0.0.5" }, heartbeatVerified: true);

        Assert.Equal("resolved", withEmptyExpected);
        Assert.Equal("resolved", withNonMatchingExpected);
    }

    [Fact]
    public void BuildDomainStatus_NoHeartbeatButExpectedIpMatch_IsResolved()
    {
        // DNS is right, cache not answering - resolved (heartbeatVerified=false carries the
        // "cache may be down" nuance; the sweep-level heartbeat result surfaces it).
        var status = StatusCheckService.BuildDomainStatus(
            new List<string> { "10.0.0.5" }, new List<string> { "10.0.0.5" }, heartbeatVerified: false);

        Assert.Equal("resolved", status);
    }

    [Fact]
    public void BuildDomainStatus_NoHeartbeatNoMatch_PublicAnswer_IsMismatched()
    {
        // Traffic is going to the internet - the failure this tool exists to catch.
        var status = StatusCheckService.BuildDomainStatus(
            new List<string> { "1.2.3.4" }, new List<string> { "10.0.0.5" }, heartbeatVerified: false);

        Assert.Equal("mismatched", status);
    }

    [Fact]
    public void BuildDomainStatus_NoHeartbeatNoMatch_PrivateAnswer_IsUnverified()
    {
        // Points into the LAN but nothing answered as lancache - cache down or wrong host.
        var emptyExpected = StatusCheckService.BuildDomainStatus(
            new List<string> { "192.168.1.5" }, new List<string>(), heartbeatVerified: false);
        var mixedPublicPrivate = StatusCheckService.BuildDomainStatus(
            new List<string> { "1.2.3.4", "172.16.2.98" }, new List<string>(), heartbeatVerified: false);

        Assert.Equal("unverified", emptyExpected);
        Assert.Equal("unverified", mixedPublicPrivate);
    }

    [Fact]
    public void BuildDomainStatus_NoExpectedIpsAndPublicAnswer_IsMismatched()
    {
        // v1.4 supersedes v1.3 here: a public answer is a cache bypass even with no baseline.
        var status = StatusCheckService.BuildDomainStatus(
            new List<string> { "1.2.3.4" }, new List<string>(), heartbeatVerified: false);

        Assert.Equal("mismatched", status);
    }

    [Fact]
    public void BuildServiceResultCore_AllResolved_IsResolved()
    {
        var domains = new List<DomainCheckResult>
        {
            new() { Status = "resolved" },
            new() { Status = "resolved" }
        };

        var result = StatusCheckService.BuildServiceResultCore("steam", "Steam CDN", domains);

        Assert.Equal("resolved", result.Status);
        Assert.Equal(2, result.ResolvedCount);
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public void BuildServiceResultCore_SomeResolved_IsPartial()
    {
        var domains = new List<DomainCheckResult>
        {
            new() { Status = "resolved" },
            new() { Status = "unresolved" }
        };

        var result = StatusCheckService.BuildServiceResultCore("steam", "Steam CDN", domains);

        Assert.Equal("partial", result.Status);
        Assert.Equal(1, result.ResolvedCount);
    }

    [Fact]
    public void BuildServiceResultCore_NoneResolved_IsUnresolved()
    {
        var domains = new List<DomainCheckResult>
        {
            new() { Status = "unresolved" },
            new() { Status = "mismatched" }
        };

        var result = StatusCheckService.BuildServiceResultCore("steam", "Steam CDN", domains);

        Assert.Equal("unresolved", result.Status);
        Assert.Equal(0, result.ResolvedCount);
    }

    [Fact]
    public void BuildServiceResultCore_NoDomains_IsUnresolved()
    {
        var result = StatusCheckService.BuildServiceResultCore("steam", "Steam CDN", new List<DomainCheckResult>());

        Assert.Equal("unresolved", result.Status);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void BuildSummaryCore_AggregatesServiceCountsAndDomainCounts()
    {
        var services = new List<ServiceCheckResult>
        {
            new() { Status = "resolved", ResolvedCount = 2, TotalCount = 2 },
            new() { Status = "partial", ResolvedCount = 1, TotalCount = 2 },
            new() { Status = "unresolved", ResolvedCount = 0, TotalCount = 1 }
        };

        var summary = StatusCheckService.BuildSummaryCore(services);

        Assert.Equal(3, summary.TotalServices);
        Assert.Equal(1, summary.ResolvedServices);
        Assert.Equal(1, summary.PartialServices);
        Assert.Equal(1, summary.UnresolvedServices);
        Assert.Equal(0, summary.DisabledServices);
        Assert.Equal(5, summary.TotalDomains);
        Assert.Equal(3, summary.ResolvedDomains);
    }

    // ===== Contract amendment v1.1: disabled services excluded from verdict counts =====

    [Fact]
    public void BuildSummaryCore_DisabledServices_ExcludedFromResolvedPartialUnresolvedCounts()
    {
        var services = new List<ServiceCheckResult>
        {
            new() { Status = "resolved", ResolvedCount = 2, TotalCount = 2 },
            new() { Status = "disabled", ResolvedCount = 0, TotalCount = 5 },
            new() { Status = "disabled", ResolvedCount = 0, TotalCount = 3 }
        };

        var summary = StatusCheckService.BuildSummaryCore(services);

        Assert.Equal(3, summary.TotalServices);
        Assert.Equal(1, summary.ResolvedServices);
        Assert.Equal(0, summary.PartialServices);
        Assert.Equal(0, summary.UnresolvedServices);
        Assert.Equal(2, summary.DisabledServices);
        // Resolved+Partial+Unresolved+Disabled == TotalServices always holds.
        Assert.Equal(summary.TotalServices, summary.ResolvedServices + summary.PartialServices + summary.UnresolvedServices + summary.DisabledServices);
        // TotalDomains still counts a disabled service's listed domain count (contract: totalCount = listed count).
        Assert.Equal(10, summary.TotalDomains);
    }

    // ===== HTTPS-redirect probe: upstream forcing HTTP -> HTTPS bypasses the cache =====

    [Theory]
    [InlineData(301, "https://cdn.example.com/")]
    [InlineData(302, "https://cdn.example.com/path?x=1")]
    [InlineData(307, "https://cdn.example.com/")]
    [InlineData(308, "https://cdn.example.com/")]
    public void GetHttpsRedirectTarget_AbsoluteHttpsLocationOn3xx_IsFlagged(int statusCode, string location)
    {
        var target = LancacheServerLocator.GetHttpsRedirectTarget(statusCode, new Uri(location));

        Assert.Equal(location, target);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(502)]
    public void GetHttpsRedirectTarget_NonRedirectStatus_IsNull(int statusCode)
    {
        // Even with an https Location header present, a non-3xx answer is not a redirect.
        var target = LancacheServerLocator.GetHttpsRedirectTarget(statusCode, new Uri("https://cdn.example.com/"));

        Assert.Null(target);
    }

    [Fact]
    public void GetHttpsRedirectTarget_HttpLocation_IsNull()
    {
        // A same-scheme redirect is ordinary CDN behavior - the client stays on plain HTTP.
        var target = LancacheServerLocator.GetHttpsRedirectTarget(301, new Uri("http://cdn2.example.com/"));

        Assert.Null(target);
    }

    [Fact]
    public void GetHttpsRedirectTarget_RelativeOrMissingLocation_IsNull()
    {
        // A relative Location keeps the request's http scheme; no Location is no redirect target.
        Assert.Null(LancacheServerLocator.GetHttpsRedirectTarget(301, new Uri("/somewhere", UriKind.Relative)));
        Assert.Null(LancacheServerLocator.GetHttpsRedirectTarget(301, null));
    }

    [Fact]
    public void BuildSummaryCore_CountsHttpsRedirectDomains()
    {
        var services = new List<ServiceCheckResult>
        {
            new()
            {
                Status = "resolved", ResolvedCount = 2, TotalCount = 2,
                Domains = new List<DomainCheckResult>
                {
                    new() { Status = "resolved", HttpsRedirect = true },
                    new() { Status = "resolved", HttpsRedirect = false }
                }
            },
            // Null = probe not attempted/undeterminable - must never count as a forced redirect.
            new()
            {
                Status = "resolved", ResolvedCount = 1, TotalCount = 1,
                Domains = new List<DomainCheckResult> { new() { Status = "resolved", HttpsRedirect = null } }
            }
        };

        var summary = StatusCheckService.BuildSummaryCore(services);

        Assert.Equal(1, summary.HttpsRedirectDomains);
    }

    // ===== Contract amendment v1.3: empty expectedIps -> unverified, LANCACHE_IP lists =====

    [Fact]
    public void BuildServiceResultCore_AnyUnverifiedDomain_IsUnverified()
    {
        // Unverified only occurs when expectedIps was empty for the whole sweep, so it can only
        // co-occur with unresolved domains - and the service still counts as unverified.
        var domains = new List<DomainCheckResult>
        {
            new() { Status = "unverified" },
            new() { Status = "unresolved" }
        };

        var result = StatusCheckService.BuildServiceResultCore("arenanet", "Guild Wars 2", domains);

        Assert.Equal("unverified", result.Status);
        Assert.Equal(0, result.ResolvedCount);
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public void BuildSummaryCore_UnverifiedServices_ExcludedFromVerdictCountsAndInvariantHolds()
    {
        var services = new List<ServiceCheckResult>
        {
            new()
            {
                Status = "unverified", ResolvedCount = 0, TotalCount = 2,
                Domains = new List<DomainCheckResult>
                {
                    new() { Status = "unverified" },
                    new() { Status = "unverified" }
                }
            },
            new() { Status = "unresolved", ResolvedCount = 0, TotalCount = 1, Domains = new List<DomainCheckResult> { new() { Status = "unresolved" } } },
            new() { Status = "disabled", ResolvedCount = 0, TotalCount = 3 }
        };

        var summary = StatusCheckService.BuildSummaryCore(services);

        Assert.Equal(3, summary.TotalServices);
        Assert.Equal(0, summary.ResolvedServices);
        Assert.Equal(0, summary.PartialServices);
        Assert.Equal(1, summary.UnresolvedServices);
        Assert.Equal(1, summary.DisabledServices);
        Assert.Equal(1, summary.UnverifiedServices);
        Assert.Equal(2, summary.UnverifiedDomains);
        // Resolved+Partial+Unresolved+Disabled+Unverified == TotalServices always holds.
        Assert.Equal(summary.TotalServices,
            summary.ResolvedServices + summary.PartialServices + summary.UnresolvedServices +
            summary.DisabledServices + summary.UnverifiedServices);
    }

    [Theory]
    [InlineData("1.2.3.4 1.2.3.5", new[] { "1.2.3.4", "1.2.3.5" })]
    [InlineData("1.2.3.4, 1.2.3.5", new[] { "1.2.3.4", "1.2.3.5" })]
    [InlineData("  10.0.0.1\t10.0.0.2 ; 10.0.0.3  ", new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" })]
    [InlineData("172.16.2.98", new[] { "172.16.2.98" })]
    public void SplitAddressList_SplitsWhitespaceAndCommaSeparatedLists(string value, string[] expected)
    {
        // lancache-dns supports round-robin LANCACHE_IP lists; every entry is an expected cache IP.
        Assert.Equal(expected, LancacheServerLocator.SplitAddressList(value));
    }

    // ===== Contract amendment v1.4: heartbeat verification =====

    [Fact]
    public void BuildServiceResultCore_MixedResolvedAndUnverified_IsPartial()
    {
        // Under v1.4 heartbeat-verified (resolved) domains can coexist with unverified ones in a
        // service - any confirmed domain makes the rollup "partial", not "unverified".
        var domains = new List<DomainCheckResult>
        {
            new() { Status = "resolved" },
            new() { Status = "unverified" }
        };

        var result = StatusCheckService.BuildServiceResultCore("steam", "Steam CDN", domains);

        Assert.Equal("partial", result.Status);
        Assert.Equal(1, result.ResolvedCount);
    }

    [Fact]
    public async Task HeartbeatVerdictCache_ProbesEachUniqueIpExactlyOnce()
    {
        var probeCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var cache = new HeartbeatVerdictCache(
            ip =>
            {
                probeCounts.AddOrUpdate(ip, 1, (_, count) => count + 1);
                return Task.FromResult(new HeartbeatResult { Reachable = true, ServedBy = "cache-host", CacheIp = ip });
            },
            ttl: TimeSpan.FromMinutes(5),
            maxConcurrency: 4);

        // 40 concurrent lookups across 2 unique IPs - the sweep shape (many domains, few IPs).
        var tasks = Enumerable.Range(0, 40)
            .Select(i => cache.GetAsync(i % 2 == 0 ? "172.16.2.98" : "172.16.2.99", CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Reachable));
        Assert.Equal(1, probeCounts["172.16.2.98"]);
        Assert.Equal(1, probeCounts["172.16.2.99"]);
    }

    [Fact]
    public async Task HeartbeatVerdictCache_ExpiredEntryIsReprobed()
    {
        var probeCount = 0;
        var cache = new HeartbeatVerdictCache(
            _ =>
            {
                Interlocked.Increment(ref probeCount);
                return Task.FromResult(new HeartbeatResult { Reachable = false });
            },
            ttl: TimeSpan.Zero,
            maxConcurrency: 1);

        await cache.GetAsync("10.0.0.5", CancellationToken.None);
        await cache.GetAsync("10.0.0.5", CancellationToken.None);

        Assert.Equal(2, probeCount);
    }

    // ===== Contract amendment v1.2: CacheDomainsService.DetermineEnvSource tier precedence =====

    [Fact]
    public void DetermineEnvSource_AnyDockerInspectValue_ReturnsDockerInspect()
    {
        var results = new[]
        {
            new EnvValueResult { Value = null, Source = EnvValueSource.EnvFile },
            new EnvValueResult { Value = "master", Source = EnvValueSource.DockerInspect }
        };

        Assert.Equal("dockerInspect", CacheDomainsService.DetermineEnvSource(results));
    }

    [Fact]
    public void DetermineEnvSource_NoDockerInspectButEnvFileValue_ReturnsEnvFile()
    {
        var results = new[]
        {
            new EnvValueResult { Value = null, Source = EnvValueSource.DockerInspect },
            new EnvValueResult { Value = "false", Source = EnvValueSource.EnvFile }
        };

        Assert.Equal("envFile", CacheDomainsService.DetermineEnvSource(results));
    }

    [Fact]
    public void DetermineEnvSource_NoValuesAnywhere_ReturnsDefaults()
    {
        var results = new[]
        {
            new EnvValueResult { Value = null, Source = EnvValueSource.DockerInspect },
            new EnvValueResult { Value = null, Source = EnvValueSource.EnvFile }
        };

        Assert.Equal("defaults", CacheDomainsService.DetermineEnvSource(results));
    }

    // ===== Follow-up: avg latency + cache node aggregation =====

    [Fact]
    public void BuildAvgLatencyMs_AllNull_ReturnsNull()
    {
        var services = new List<ServiceCheckResult>
        {
            new()
            {
                Domains = new List<DomainCheckResult>
                {
                    new() { LatencyMs = null },
                    new() { LatencyMs = null }
                }
            }
        };

        Assert.Null(StatusCheckService.BuildAvgLatencyMs(services));
    }

    [Fact]
    public void BuildAvgLatencyMs_MixedNullAndValues_ComputesMeanOfNonNullAcrossAllServices()
    {
        var services = new List<ServiceCheckResult>
        {
            new()
            {
                Domains = new List<DomainCheckResult>
                {
                    new() { LatencyMs = 10.0 },
                    new() { LatencyMs = null }
                }
            },
            new()
            {
                Domains = new List<DomainCheckResult>
                {
                    new() { LatencyMs = 20.0 }
                }
            }
        };

        Assert.Equal(15.0, StatusCheckService.BuildAvgLatencyMs(services));
    }

    [Fact]
    public void BuildAvgLatencyMs_NoDomainsAnywhere_ReturnsNull()
    {
        var services = new List<ServiceCheckResult>
        {
            new() { Status = "disabled", Domains = new List<DomainCheckResult>() }
        };

        Assert.Null(StatusCheckService.BuildAvgLatencyMs(services));
    }

    [Fact]
    public void BuildCacheNodes_GroupsIpsByServedBy_SortsNodesAndIpsNumerically()
    {
        var verifiedIps = new Dictionary<string, string>
        {
            ["172.16.2.100"] = "cache-2",
            ["172.16.2.99"] = "cache-2",
            ["10.0.0.5"] = "cache-1"
        };

        var nodes = StatusCheckService.BuildCacheNodes(verifiedIps);

        Assert.Equal(2, nodes.Count);
        Assert.Equal("cache-1", nodes[0].ServedBy);
        Assert.Equal(new[] { "10.0.0.5" }, nodes[0].Ips);
        Assert.Equal("cache-2", nodes[1].ServedBy);
        // Numeric IP sort, not lexicographic (.99 before .100).
        Assert.Equal(new[] { "172.16.2.99", "172.16.2.100" }, nodes[1].Ips);
    }

    [Fact]
    public void BuildCacheNodes_NoVerifiedIps_ReturnsEmptyList()
    {
        var nodes = StatusCheckService.BuildCacheNodes(new Dictionary<string, string>());

        Assert.Empty(nodes);
    }

    // ===== DockerContainerMatching (shared by LancacheServerLocator + LancacheEnvironmentSource) =====

    [Theory]
    [InlineData("lancache-dns", true)]
    [InlineData("my_lancachedns_1", true)]
    [InlineData("dns-lancache-server", true)]
    [InlineData("pihole-dns-only", false)]
    [InlineData("monolithic", false)]
    public void DockerContainerMatching_IsDnsContainer_MatchesNamePatterns(string name, bool expected)
    {
        Assert.Equal(expected, DockerContainerMatching.IsDnsContainer(new[] { name }));
    }

    [Theory]
    [InlineData("lancachenet/monolithic:latest", "some-cache", true)]
    [InlineData("nginx:latest", "monolithic", true)]
    [InlineData("nginx:latest", "unrelated-container", false)]
    // A fork keeps "monolithic" in the image name without the lancachenet/ owner prefix.
    [InlineData("regix1/monolithic:latest", "custom-cache-name", true)]
    public void DockerContainerMatching_IsLancacheCacheContainer_MatchesImageOrName(string image, string name, bool expected)
    {
        Assert.Equal(expected, DockerContainerMatching.IsLancacheCacheContainer(image, new[] { name }));
    }

    [Theory]
    [InlineData("lancache-manager", "someimage", true)]
    [InlineData("other-container", "regix1/lancache-manager:latest", true)]
    [InlineData("other-container", "nginx:latest", false)]
    public void DockerContainerMatching_IsManagerContainer_MatchesNameOrImage(string name, string image, bool expected)
    {
        Assert.Equal(expected, DockerContainerMatching.IsManagerContainer(new[] { name }, image));
    }

    // ===== DNS auto-detection: candidate ordering + SSRF gating =====

    [Theory]
    // RFC1918 private ranges are probeable.
    [InlineData("10.0.0.5", true)]
    [InlineData("172.17.0.1", true)]
    [InlineData("192.168.1.1", true)]
    // Loopback is SSRF-safe (reaches only the manager itself) so it is probeable even though it is
    // not RFC1918 - it is the host-networked-manager fallback candidate.
    [InlineData("127.0.0.1", true)]
    // Public / invalid candidates must never be probed.
    [InlineData("1.2.3.4", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("not-an-ip", false)]
    [InlineData("", false)]
    public void IsProbeableCandidateIp_AllowsPrivateAndLoopback_RejectsPublicAndInvalid(string ip, bool expected)
    {
        Assert.Equal(expected, LancacheServerLocator.IsProbeableCandidateIp(ip));
    }

    [Fact]
    public void BuildDnsServerCandidates_OrdersBridgeGatewayCacheHostInternalThenLoopback()
    {
        var result = LancacheServerLocator.BuildDnsServerCandidates(
            mode: StatusCheckResolverModes.Auto,
            dnsBridgeIp: "172.20.0.2",
            gatewayIp: "172.17.0.1",
            knownCacheIps: new[] { "10.0.0.5" },
            hostDockerInternalIps: new[] { "192.168.65.2" });

        // 5-tier order, loopback appended last.
        Assert.Equal(new[] { "172.20.0.2", "172.17.0.1", "10.0.0.5", "192.168.65.2", "127.0.0.1" }, result);
    }

    [Fact]
    public void BuildDnsServerCandidates_GatewayLeadsWhenNoBridgeIp_HostNetworkedCase()
    {
        // The reported all-host-networked box: no dns bridge IP, gateway is the winning candidate.
        var result = LancacheServerLocator.BuildDnsServerCandidates(
            mode: StatusCheckResolverModes.Auto,
            dnsBridgeIp: null,
            gatewayIp: "172.17.0.1",
            knownCacheIps: null,
            hostDockerInternalIps: null);

        Assert.Equal(new[] { "172.17.0.1", "127.0.0.1" }, result);
    }

    [Fact]
    public void BuildDnsServerCandidates_DropsPublicIpsAndDedupesCaseInsensitively()
    {
        var result = LancacheServerLocator.BuildDnsServerCandidates(
            mode: StatusCheckResolverModes.Auto,
            dnsBridgeIp: "1.2.3.4",                                   // public -> dropped
            gatewayIp: "172.17.0.1",
            knownCacheIps: new[] { "172.17.0.1", "10.0.0.5", "8.8.8.8" }, // dup gateway + public dropped
            hostDockerInternalIps: new[] { "10.0.0.5" });            // dup -> collapsed

        // 172.17.0.1 (gateway) then 10.0.0.5 (cache), publics gone, dupes collapsed, loopback last.
        Assert.Equal(new[] { "172.17.0.1", "10.0.0.5", "127.0.0.1" }, result);
    }

    [Fact]
    public void BuildDnsServerCandidates_AllNull_YieldsOnlyLoopback()
    {
        var result = LancacheServerLocator.BuildDnsServerCandidates(
            StatusCheckResolverModes.Auto, null, null, null, null);

        Assert.Equal(new[] { "127.0.0.1" }, result);
    }

    // ===== Resolver mode scoping of the candidate set (auto / bridge / host) =====

    [Fact]
    public void BuildDnsServerCandidates_BridgeMode_KeepsOnlyTheBridgeIp()
    {
        // "bridge" queries ONLY the lancache-dns container's bridge IP - no gateway/known/loopback.
        var result = LancacheServerLocator.BuildDnsServerCandidates(
            mode: StatusCheckResolverModes.Bridge,
            dnsBridgeIp: "172.20.0.2",
            gatewayIp: "172.17.0.1",
            knownCacheIps: new[] { "10.0.0.5" },
            hostDockerInternalIps: new[] { "192.168.65.2" });

        Assert.Equal(new[] { "172.20.0.2" }, result);
    }

    [Fact]
    public void BuildDnsServerCandidates_BridgeMode_NoBridgeIp_YieldsEmpty()
    {
        // Host-networked (no bridge IP) under "bridge" mode has no candidate at all -> falls back to system.
        var result = LancacheServerLocator.BuildDnsServerCandidates(
            mode: StatusCheckResolverModes.Bridge,
            dnsBridgeIp: null,
            gatewayIp: "172.17.0.1",
            knownCacheIps: new[] { "10.0.0.5" },
            hostDockerInternalIps: new[] { "192.168.65.2" });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildDnsServerCandidates_HostMode_ExcludesBridgeIp_IncludesGatewayKnownLoopback()
    {
        // "host" skips the Docker bridge-container IP and probes only host-side candidates.
        var result = LancacheServerLocator.BuildDnsServerCandidates(
            mode: StatusCheckResolverModes.Host,
            dnsBridgeIp: "172.20.0.2",
            gatewayIp: "172.17.0.1",
            knownCacheIps: new[] { "10.0.0.5" },
            hostDockerInternalIps: new[] { "192.168.65.2" });

        Assert.DoesNotContain("172.20.0.2", result);
        Assert.Equal(new[] { "172.17.0.1", "10.0.0.5", "192.168.65.2", "127.0.0.1" }, result);
    }

    // ===== StatusCheckResolverModes validation / normalization =====

    [Theory]
    [InlineData("auto", true)]
    [InlineData("bridge", true)]
    [InlineData("host", true)]
    [InlineData("Auto", false)] // case-sensitive wire values
    [InlineData("system", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StatusCheckResolverModes_IsValid_AcceptsOnlyTheThreeWireValues(string? mode, bool expected)
    {
        Assert.Equal(expected, StatusCheckResolverModes.IsValid(mode));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("bridge", "bridge")]
    [InlineData("host", "host")]
    [InlineData("nonsense", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    public void StatusCheckResolverModes_Normalize_DefaultsUnknownToAuto(string? mode, string expected)
    {
        Assert.Equal(expected, StatusCheckResolverModes.Normalize(mode));
    }

    // ===== Cache-server candidate builder: profile scoping, ordering, loopback exclusion, dedupe =====

    [Fact]
    public void CacheCandidateProfiles_HaveExpectedFlags()
    {
        // H5: Status Check keeps its frozen contract (no host-side cache candidates); prefill opts in.
        var status = LancacheServerLocator.CacheCandidateProfile.StatusCheck;
        Assert.True(status.InspectContainers);
        Assert.True(status.IncludeDnsContainerIp);
        Assert.False(status.IncludeGateway);
        Assert.False(status.IncludeHostDockerInternal);

        var prefill = LancacheServerLocator.CacheCandidateProfile.Prefill;
        Assert.True(prefill.InspectContainers);
        Assert.True(prefill.IncludeDnsContainerIp);
        Assert.True(prefill.IncludeGateway);
        Assert.True(prefill.IncludeHostDockerInternal);
    }

    [Fact]
    public void BuildCacheCandidates_PrefillProfile_OrdersDnsContainerGatewayHostInternalThenFallback()
    {
        var result = LancacheServerLocator.BuildCacheCandidates(
            LancacheServerLocator.CacheCandidateProfile.Prefill,
            configuredDnsIp: "10.0.0.1",
            dnsBridgeIp: "172.20.0.2",
            containerCacheCandidates: new[] { ("10.0.0.9", "container cache") },
            fallbackPeerCandidates: new[] { ("10.0.0.50", "peer") },
            gatewayIp: "172.17.0.1",
            hostDockerInternalIps: new[] { "192.168.65.2" });

        // 1) configured dns, 2) detected dns bridge, 3) named cache container, 4) gateway,
        // 5) host.docker.internal, 6) unnamed peer last.
        Assert.Equal(
            new[] { "10.0.0.1", "172.20.0.2", "10.0.0.9", "172.17.0.1", "192.168.65.2", "10.0.0.50" },
            result.Select(c => c.Ip).ToArray());
    }

    [Fact]
    public void BuildCacheCandidates_StatusCheckProfile_ExcludesGatewayAndHostDockerInternal()
    {
        // H5: even when the caller supplies gateway/host.docker.internal, the Status Check profile
        // drops them - only the dns-container IPs and named/peer cache containers survive.
        var result = LancacheServerLocator.BuildCacheCandidates(
            LancacheServerLocator.CacheCandidateProfile.StatusCheck,
            configuredDnsIp: "10.0.0.1",
            dnsBridgeIp: "172.20.0.2",
            containerCacheCandidates: new[] { ("10.0.0.9", "container cache") },
            fallbackPeerCandidates: new[] { ("10.0.0.50", "peer") },
            gatewayIp: "172.17.0.1",
            hostDockerInternalIps: new[] { "192.168.65.2" });

        Assert.Equal(
            new[] { "10.0.0.1", "172.20.0.2", "10.0.0.9", "10.0.0.50" },
            result.Select(c => c.Ip).ToArray());
        Assert.DoesNotContain("172.17.0.1", result.Select(c => c.Ip));
        Assert.DoesNotContain("192.168.65.2", result.Select(c => c.Ip));
    }

    [Fact]
    public void BuildCacheCandidates_H1_DnsContainerIpGatedByProfileFlag()
    {
        // H1: the monolithic co-location dns IPs are candidates only when IncludeDnsContainerIp is set.
        var profile = new LancacheServerLocator.CacheCandidateProfile(
            InspectContainers: true, IncludeDnsContainerIp: false, IncludeGateway: false, IncludeHostDockerInternal: false);

        var result = LancacheServerLocator.BuildCacheCandidates(
            profile,
            configuredDnsIp: "10.0.0.1",
            dnsBridgeIp: "172.20.0.2",
            containerCacheCandidates: new[] { ("10.0.0.9", "container cache") },
            fallbackPeerCandidates: Array.Empty<(string, string)>(),
            gatewayIp: null,
            hostDockerInternalIps: null);

        Assert.Equal(new[] { "10.0.0.9" }, result.Select(c => c.Ip).ToArray());
    }

    [Fact]
    public void BuildCacheCandidates_NeverIncludesLoopback_InAnySlot()
    {
        // H3: loopback is meaningless as an injected LANCACHE_IP - the private-IP gate must drop it
        // from every candidate source, including the prefill-only gateway/host.docker.internal.
        var result = LancacheServerLocator.BuildCacheCandidates(
            LancacheServerLocator.CacheCandidateProfile.Prefill,
            configuredDnsIp: "127.0.0.1",
            dnsBridgeIp: "127.0.0.2",
            containerCacheCandidates: new[] { ("127.0.0.3", "container") },
            fallbackPeerCandidates: new[] { ("127.0.0.4", "peer") },
            gatewayIp: "127.0.0.1",
            hostDockerInternalIps: new[] { "127.0.0.5" });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildCacheCandidates_DropsPublicIpsAndDedupesCaseInsensitively()
    {
        var result = LancacheServerLocator.BuildCacheCandidates(
            LancacheServerLocator.CacheCandidateProfile.Prefill,
            configuredDnsIp: "8.8.8.8",                                        // public -> dropped
            dnsBridgeIp: "10.0.0.9",                                          // first winner
            containerCacheCandidates: new[] { ("10.0.0.9", "dup container") }, // dup -> collapsed
            fallbackPeerCandidates: new[] { ("1.2.3.4", "public peer") },      // public -> dropped
            gatewayIp: "172.17.0.1",
            hostDockerInternalIps: new[] { "172.17.0.1" });                    // dup gateway -> collapsed

        Assert.Equal(new[] { "10.0.0.9", "172.17.0.1" }, result.Select(c => c.Ip).ToArray());
    }

    // ===== lancache-dns container bridge-IP selection (HostConfig.DNS source) =====

    [Fact]
    public void SelectDnsBridgeIp_HostNetworked_ReturnsNull()
    {
        // H2: host-networked dns has no bridge IP; null is the caller's "switch to host mode" signal.
        Assert.Null(LancacheServerLocator.SelectDnsBridgeIp(isHostNetworked: true, new[] { "172.20.0.2" }));
    }

    [Fact]
    public void SelectDnsBridgeIp_BridgeNetworked_ReturnsFirstNonEmptyIp()
    {
        Assert.Equal(
            "172.20.0.2",
            LancacheServerLocator.SelectDnsBridgeIp(isHostNetworked: false, new[] { "", "172.20.0.2", "172.20.0.3" }));
    }

    [Fact]
    public void SelectDnsBridgeIp_NoUsableIps_ReturnsNull()
    {
        Assert.Null(LancacheServerLocator.SelectDnsBridgeIp(isHostNetworked: false, new[] { "", (string?)null }));
        Assert.Null(LancacheServerLocator.SelectDnsBridgeIp(isHostNetworked: false, Array.Empty<string?>()));
    }

    // ===== LocateAsync / DetectDnsContainerBridgeIpAsync delegation (config tier, no Docker, no network) =====

    [Fact]
    public async Task LocateAsync_ConfigIpLiteral_ReturnsConfigSourceWithThatIp()
    {
        // The overload signature exists and the config tier is flag-agnostic (short-circuits before
        // env/Docker), so this stays a pure test even with includeHostSideCandidates:true.
        var locator = CreateLocator(new PrefillNetworkOptions { LancacheIp = "10.0.0.5" });

        var location = await locator.LocateAsync(includeHostSideCandidates: true, CancellationToken.None);

        Assert.Equal("config", location.Source);
        Assert.Equal(new[] { "10.0.0.5" }, location.CacheIps.ToArray());
    }

    [Fact]
    public async Task DetectDnsContainerBridgeIpAsync_ExplicitConfig_ShortCircuitsToConfiguredIp()
    {
        // Reproduces the old GetLancacheDnsIpAsync explicit-config short-circuit that TryDetect omits.
        var locator = CreateLocator(new PrefillNetworkOptions { LancacheDnsIp = "172.20.0.2" });

        Assert.Equal("172.20.0.2", await locator.DetectDnsContainerBridgeIpAsync(CancellationToken.None));
    }

    private static LancacheServerLocator CreateLocator(PrefillNetworkOptions options) =>
        new(NullLogger<LancacheServerLocator>.Instance,
            new FixedOptionsMonitor(options),
            new NullEnvironmentSource());

    private sealed class FixedOptionsMonitor : IOptionsMonitor<PrefillNetworkOptions>
    {
        public FixedOptionsMonitor(PrefillNetworkOptions value) => CurrentValue = value;
        public PrefillNetworkOptions CurrentValue { get; }
        public PrefillNetworkOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PrefillNetworkOptions, string?> listener) => null;
    }

    private sealed class NullEnvironmentSource : ILancacheEnvironmentSource
    {
        public Task<EnvValueResult> GetValueAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new EnvValueResult { Value = null, Source = EnvValueSource.EnvFile });
    }
}
