using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Services.StatusCheck;

internal delegate ValueTask<Stream> ContentSocketConnector(
    IPAddress address,
    int port,
    CancellationToken cancellationToken);

/// <summary>Runs one-byte, no-redirect requests pinned to validated public edge addresses.</summary>
internal sealed class DirectContentProbe
{
    private readonly Func<SocketsHttpHandler, HttpMessageHandler> _handlerFactory;
    private readonly ContentSocketConnector _connector;
    private readonly TimeSpan _requestTimeout;

    internal DirectContentProbe(
        Func<SocketsHttpHandler, HttpMessageHandler>? handlerFactory = null,
        ContentSocketConnector? connector = null,
        TimeSpan? requestTimeout = null)
    {
        _handlerFactory = handlerFactory ?? (static handler => handler);
        _connector = connector ?? ConnectSocketAsync;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
    }

    internal async Task<ContentPathEdgeResult> ProbeEdgeAsync(
        ContentPathSample sample,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        var http = await ProbeProtocolAsync(sample, address, Uri.UriSchemeHttp, cancellationToken);
        var https = await ProbeProtocolAsync(sample, address, Uri.UriSchemeHttps, cancellationToken);
        return new ContentPathEdgeResult
        {
            Address = address.ToString(),
            AddressFamily = address.AddressFamily == AddressFamily.InterNetwork ? "ipv4" : "ipv6",
            Http = http,
            Https = https
        };
    }

    internal async Task<ProtocolProbeResult> ProbeProtocolAsync(
        ContentPathSample sample,
        IPAddress address,
        string scheme,
        CancellationToken cancellationToken)
    {
        if (!PublicAddressSafety.IsPublic(address) ||
            (scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps) ||
            !Uri.TryCreate($"{scheme}://{sample.Host}{sample.Target}", UriKind.Absolute, out var uri) ||
            !uri.Host.Equals(sample.Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort)
        {
            return new ProtocolProbeResult { Outcome = "invalidResponse" };
        }

        using var socketsHandler = CreateHandler(address);
        var messageHandler = _handlerFactory(socketsHandler);
        var ownsSeparateHandler = !ReferenceEquals(messageHandler, socketsHandler);
        try
        {
            using var client = new HttpClient(messageHandler, disposeHandler: ownsSeparateHandler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Host = sample.Host;
            request.Headers.Range = new RangeHeaderValue(0, 0);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            request.Headers.UserAgent.ParseAdd(LancacheServerLocator.ProbeUserAgent);

            using var headerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerTimeoutCts.CancelAfter(_requestTimeout);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                headerTimeoutCts.Token);

            if (response.Content != null)
            {
                using var bodyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bodyTimeoutCts.CancelAfter(_requestTimeout);
                await using var body = await response.Content.ReadAsStreamAsync(bodyTimeoutCts.Token);
                var oneByte = new byte[1];
                _ = await body.ReadAsync(oneByte, bodyTimeoutCts.Token);
            }

            return ClassifyResponse(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProtocolProbeResult { Outcome = "timeout" };
        }
        catch (HttpRequestException ex) when (ContainsAuthenticationFailure(ex))
        {
            return new ProtocolProbeResult { Outcome = "tlsCertificateFailure" };
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException)
        {
            return new ProtocolProbeResult { Outcome = "connectFailure" };
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            return new ProtocolProbeResult { Outcome = "invalidResponse" };
        }
    }

    internal static async ValueTask<Stream> ConnectToSelectedAddressAsync(
        IPAddress selectedAddress,
        int port,
        ContentSocketConnector connector,
        CancellationToken cancellationToken)
    {
        if (!PublicAddressSafety.IsPublic(selectedAddress) || port is not (80 or 443))
        {
            throw new InvalidOperationException("Only validated public addresses on standard ports may be connected.");
        }

        return await connector(selectedAddress, port, cancellationToken);
    }

    private SocketsHttpHandler CreateHandler(IPAddress selectedAddress) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        MaxConnectionsPerServer = 1,
        ConnectCallback = (context, cancellationToken) => ConnectToSelectedAddressAsync(
            selectedAddress,
            context.DnsEndPoint.Port,
            _connector,
            cancellationToken)
    };

    private static ProtocolProbeResult ClassifyResponse(HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;
        if (statusCode is 200 or 206)
        {
            return new ProtocolProbeResult { Outcome = "content", StatusCode = statusCode };
        }

        if (statusCode is 301 or 302 or 303 or 307 or 308)
        {
            var redirectScheme = response.Headers.Location?.IsAbsoluteUri == true
                ? response.Headers.Location.Scheme.ToLowerInvariant()
                : null;
            return new ProtocolProbeResult
            {
                Outcome = redirectScheme == Uri.UriSchemeHttps ? "redirectToHttps" : "otherRedirect",
                StatusCode = statusCode,
                RedirectScheme = redirectScheme
            };
        }

        if (statusCode is >= 300 and < 400)
        {
            return new ProtocolProbeResult { Outcome = "otherRedirect", StatusCode = statusCode };
        }

        if (statusCode is 401 or 403 or 405 or 429)
        {
            return new ProtocolProbeResult { Outcome = "denied", StatusCode = statusCode };
        }

        if (statusCode is 404 or 410)
        {
            return new ProtocolProbeResult { Outcome = "notFoundOrStale", StatusCode = statusCode };
        }

        if (statusCode == 416)
        {
            return new ProtocolProbeResult { Outcome = "rangeRejected", StatusCode = statusCode };
        }

        if (statusCode is >= 500 and < 600)
        {
            return new ProtocolProbeResult { Outcome = "serverError", StatusCode = statusCode };
        }

        return new ProtocolProbeResult { Outcome = "invalidResponse", StatusCode = statusCode };
    }

    private static bool ContainsAuthenticationFailure(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<Stream> ConnectSocketAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
