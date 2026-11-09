namespace LancacheManager.Tests;

/// <summary>Hands out one HttpClient per requested name, all backed by the same handler.</summary>
internal sealed class SingleHandlerHttpClients : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public SingleHandlerHttpClients(HttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
