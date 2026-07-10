using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Services.StatusCheck;

internal sealed record ProtocolConsensus(string Status, string? Reason, int ConsensusEdges);

/// <summary>Pure exact-path consensus; it never promotes a result to a service-wide verdict.</summary>
internal static class ContentPathConsensus
{
    /// <summary>Collapses a probe outcome to what it proves about the protocol's transport:
    /// "content" (returned bytes), "answered" (a plain HTTP-level response arrived - 403/404 on
    /// a root path still proves the protocol is served), "pushes" (redirects the client to
    /// HTTPS), or "unreachable" (no usable response). Verdicts compare these classes, so a 403
    /// on one edge and a 404 on another agree instead of reading as edge disagreement.</summary>
    private static string ClassOf(string outcome) => outcome switch
    {
        "content" => "content",
        "redirectToHttps" => "pushes",
        "otherRedirect" or "denied" or "notFoundOrStale" or "rangeRejected" or "serverError" => "answered",
        _ => "unreachable"
    };

    internal static ProtocolConsensus Classify(
        IReadOnlyList<ContentPathEdgeResult> edges,
        string? resolutionFailureReason = null)
    {
        // An address family whose every edge failed to CONNECT on both protocols says nothing
        // about the publisher - it is an unreachable network path from the probe host (the
        // canonical case: AAAA edges probed from an IPv4-only Docker bridge). Those edges must
        // not poison consensus for the family that actually carried traffic. Only connect
        // failures qualify; TLS failures, timeouts, and HTTP outcomes are real publisher signal.
        var usableEdges = edges;
        var reachable = edges
            .GroupBy(edge => edge.AddressFamily, StringComparer.Ordinal)
            .Where(family => family.Any(edge =>
                edge.Http.Outcome != "connectFailure" || edge.Https.Outcome != "connectFailure"))
            .SelectMany(family => family)
            .ToList();
        if (reachable.Count > 0 && reachable.Count < edges.Count)
        {
            usableEdges = reachable;
        }

        // v1.5: a single usable edge still decides - many hosts legitimately publish one
        // public address, and refusing to read the only edge we probed produced a wall of
        // "inconclusive" rows. Unanimity across whatever usable edges exist is the bar; the
        // ConsensusEdges/TotalPublicEdges pair keeps the sample size visible to the UI.
        if (usableEdges.Count < 1)
        {
            return new ProtocolConsensus(
                "inconclusive",
                resolutionFailureReason ?? "insufficientEdges",
                0);
        }

        var httpClasses = usableEdges.Select(edge => ClassOf(edge.Http.Outcome)).Distinct(StringComparer.Ordinal).ToList();
        var httpsClasses = usableEdges.Select(edge => ClassOf(edge.Https.Outcome)).Distinct(StringComparer.Ordinal).ToList();
        if (httpClasses.Count != 1 || httpsClasses.Count != 1)
        {
            return new ProtocolConsensus("inconclusive", "edgeDisagreement", 0);
        }

        var http = httpClasses[0];
        var https = httpsClasses[0];

        // Content on HTTPS with anything less on HTTP is the classic HTTPS-only signal and
        // outranks the transport-level reading below.
        if (https == "content")
        {
            if (http == "content")
            {
                return new ProtocolConsensus("bothUsable", "bothUsable", usableEdges.Count);
            }

            var contentReason = http switch
            {
                "pushes" => "httpsOnlyRedirect",
                "answered" => "httpsOnlyDenied",
                _ => "httpsOnlyTransport"
            };
            return new ProtocolConsensus("httpsOnlyCandidate", contentReason, usableEdges.Count);
        }

        if (http == "content")
        {
            return new ProtocolConsensus("httpUsable", "httpUsable", usableEdges.Count);
        }

        // v1.5 transport tier: no content either way (normal for a root-path domain probe -
        // CDNs 403/404 "/"), so classify on whether each protocol ANSWERED at all. A push to
        // HTTPS counts as answering for the HTTPS side but not as plain-HTTP service.
        var httpAnswered = http == "answered";
        var httpsAnswered = https is "answered" or "pushes";
        if (httpAnswered && httpsAnswered)
        {
            return new ProtocolConsensus("bothUsable", "bothUsable", usableEdges.Count);
        }

        if (httpAnswered)
        {
            return new ProtocolConsensus("httpUsable", "httpUsable", usableEdges.Count);
        }

        if (httpsAnswered)
        {
            return new ProtocolConsensus(
                "httpsOnlyCandidate",
                http == "pushes" ? "httpsOnlyRedirect" : "httpsOnlyTransport",
                usableEdges.Count);
        }

        return new ProtocolConsensus("inconclusive", "nonDefinitiveEdges", 0);
    }
}
