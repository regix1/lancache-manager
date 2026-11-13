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

/// <summary>
/// Takes every request and answers only when the test sets <see cref="Answer"/>, or when the request
/// itself is canceled. <see cref="Entered"/> completes once the first request arrives.
/// </summary>
internal sealed class HeldHandler : HttpMessageHandler
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<HttpResponseMessage> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        return await Answer.Task.WaitAsync(cancellationToken);
    }
}
