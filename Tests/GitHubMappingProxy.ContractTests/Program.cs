global using Microsoft.Extensions.Configuration;
using System.Net;
using LancacheManager.Infrastructure.Services;

var configuration = new ConfigurationBuilder().AddInMemoryCollection(
    new Dictionary<string, string?> { ["GitHubMappings:HttpsProxy"] = "http://proxy.example.test:7890" }).Build();
var allowed = new[] { "github.com", "api.github.com", "raw.githubusercontent.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "codeload.github.com" };
var direct = new[] { "xz.pphimalayanrt.com", "st.dl.eccdnx.com", "dl.steam.clngaa.com", "dl1.steam.clngaa.com", "cache1.steamcontent.com", "api.steampowered.com", "store.steampowered.com", "lancache-cache", "steam-daemon-persistent", "127.0.0.1", "github.com.attacker.invalid", "notgithub.com", "example.com" };
var passed = 0;

using (var handler = GitHubMappingHttpClient.CreateHandler(new ConfigurationBuilder().Build()))
{
    Check(!handler.UseProxy, "unconfigured client must be direct");
}

foreach (var host in allowed)
{
    await VerifyTransportAsync(new Uri("https://" + host + "/"), "proxy.example.test", 7890);
}
foreach (var host in direct)
{
    await VerifyTransportAsync(new Uri("https://" + host + "/"), host, 443);
}
await VerifyTransportAsync(new Uri("https://GITHUB.COM./"), "proxy.example.test", 7890);
await VerifyTransportAsync(new Uri("http://github.com/"), "github.com", 80);
await VerifyTransportAsync(new Uri("https://github.com:8443/"), "github.com", 8443);
await VerifyTransportAsync(new Uri("http://xz.pphimalayanrt.com/"), "xz.pphimalayanrt.com", 80);
await VerifyTransportAsync(new Uri("http://lancache-cache/"), "lancache-cache", 80);

using (var handler = GitHubMappingHttpClient.CreateHandler(configuration))
{
    var proxy = handler.Proxy ?? throw new InvalidOperationException("Configured proxy missing");
    Check(!proxy.IsBypassed(new Uri("https://release-assets.githubusercontent.com/file")), "GitHub release redirect must proxy");
    Check(proxy.IsBypassed(new Uri("https://cache1.steamcontent.com/file")), "redirect outside allowlist must bypass");
}

await VerifyRedirectAsync("https://release-assets.githubusercontent.com/file", "proxy.example.test", 7890);
await VerifyRedirectAsync("https://cache1.steamcontent.com/file", "cache1.steamcontent.com", 443);

Console.WriteLine($"Passed: {passed}; no external network connections used.");

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}

async Task VerifyTransportAsync(Uri target, string expectedHost, int expectedPort)
{
    DnsEndPoint? observed = null;
    using var handler = GitHubMappingHttpClient.CreateHandler(configuration);
    handler.ConnectCallback = (context, _) =>
    {
        observed = context.DnsEndPoint;
        return ValueTask.FromException<Stream>(new IOException("Routing-only probe; connection deliberately not opened"));
    };
    using var client = new HttpClient(handler);
    try
    {
        using var response = await client.GetAsync(target);
        throw new InvalidOperationException("Probe unexpectedly connected");
    }
    catch (HttpRequestException)
    {
        Check(observed?.Host == expectedHost && observed.Port == expectedPort,
            $"Unexpected first connection for {target.Host}: {observed}; expected {expectedHost}:{expectedPort}");
    }
}


async Task VerifyRedirectAsync(string location, string expectedHost, int expectedPort)
{
    var connections = new List<DnsEndPoint>();
    using var handler = GitHubMappingHttpClient.CreateHandler(configuration);
    handler.ConnectCallback = (context, _) =>
    {
        connections.Add(context.DnsEndPoint);
        if (connections.Count == 1)
        {
            var response = "HTTP/1.1 302 Found\r\nLocation: " + location
                + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            return ValueTask.FromResult<Stream>(new RedirectResponseStream(response));
        }
        return ValueTask.FromException<Stream>(new IOException("Redirect destination observed; connection deliberately not opened"));
    };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    try { using var response = await client.GetAsync("http://github.com/redirect"); }
    catch (HttpRequestException) { }
    Check(connections.Count >= 2 && connections[0].Host == "github.com"
        && connections[^1].Host == expectedHost && connections[^1].Port == expectedPort,
        "Automatic redirect selected the wrong transport for " + location);
}

sealed class RedirectResponseStream(string response) : Stream
{
    private readonly MemoryStream _response = new(System.Text.Encoding.ASCII.GetBytes(response));
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => _response.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _response.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) { }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _response.Dispose();
        base.Dispose(disposing);
    }
}
