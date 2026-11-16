using System.Net;

namespace LancacheManager.Infrastructure.Services;

/// <summary>Proxy policy used only by the published GitHub depot-map download.</summary>
internal static class GitHubMappingHttpClient
{
    internal const string ClientName = "GitHubMappings";

    internal static SocketsHttpHandler CreateHandler(IConfiguration configuration)
    {
        var address = configuration["GitHubMappings:HttpsProxy"];
        if (string.IsNullOrWhiteSpace(address))
        {
            return new SocketsHttpHandler { UseProxy = false };
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var proxyUri)
            || (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("GitHubMappings:HttpsProxy must be an absolute HTTP or HTTPS proxy URL.");
        }

        return new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new GitHubOnlyProxy(proxyUri)
        };
    }

    private sealed class GitHubOnlyProxy(Uri proxyUri) : IWebProxy
    {
        private static readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "github.com",
            "api.github.com",
            "codeload.github.com",
            "raw.githubusercontent.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com"
        };
        private readonly WebProxy _upstream = new(proxyUri);

        public ICredentials? Credentials
        {
            get => _upstream.Credentials;
            set => _upstream.Credentials = value;
        }

        public bool IsBypassed(Uri host) => host.Scheme != Uri.UriSchemeHttps
            || host.Port != 443
            || !_allowedHosts.Contains(host.IdnHost.TrimEnd('.'));

        public Uri GetProxy(Uri destination) => IsBypassed(destination)
            ? destination
            : _upstream.GetProxy(destination) ?? destination;
    }
}
