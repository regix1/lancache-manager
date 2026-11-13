using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// An Epic mapping service over the fake Epic of <see cref="EpicSignInMappingTests"/>, with the login storage and
/// caller of <see cref="IntegrationFixture"/>, signed in by the real sign-in, on a <see cref="ForceStoppedOwnerHost"/>.
/// One library read can be held: it waits for <see cref="ReleaseLibraryRead"/> and ignores the request's cancel, as an
/// answer in flight does.
/// </summary>
internal sealed class EpicHost : IDisposable
{
    private readonly HttpClient _http;
    private readonly ForceStoppedOwnerHost _host;
    private readonly IntegrationFixture _fixture = new();
    private readonly EpicSignInMappingTests.Handler _epic;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _hold;
    private TaskCompletionSource? _next;

    public EpicHost(ForceStoppedOwnerHost host)
    {
        _host = host;
        _epic = new EpicSignInMappingTests.Handler { Before = OnRequestAsync };
        _http = new HttpClient(_epic);
        var databases = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"force_stopped_epic_{Guid.NewGuid():N}").Options);
        Service = host.Create<EpicMappingService>(
            new EpicApiDirectClient(_http, NullLogger<EpicApiDirectClient>.Instance),
            _fixture.Epic,
            databases,
            DispatchProxy.Create<IStateService, NullReturningProxy>());
    }

    public EpicMappingService Service { get; }

    public Task HoldNextLibraryRead()
    {
        _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _hold.Task;
    }

    public Task NextLibraryRead()
    {
        _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _next.Task;
    }

    public void ReleaseLibraryRead() => _release.TrySetResult();

    private async Task OnRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get
            || !request.RequestUri!.AbsolutePath.EndsWith("/launcher/api/public/assets/Windows", StringComparison.Ordinal))
        {
            return;
        }

        Interlocked.Exchange(ref _next, null)?.TrySetResult();
        if (Interlocked.Exchange(ref _hold, null) is { } hold)
        {
            hold.TrySetResult();
            await _release.Task;
        }
    }

    public async Task SignInAsync()
    {
        var start = await Service.GetAuthorizationUrl(_fixture.Owner);
        await Service.OnAuthCodeReceivedAsync("code", caller: _fixture.Owner, attemptId: start.AttemptId);
    }

    /// <summary>Signs in and waits until the sign-in's mapping has ended and released the sign-in.</summary>
    public async Task SignInAndWaitForTheMappingAsync()
    {
        var released = _host.Notifications.WhenSent(SignalREvents.IntegrationLoginEnded);
        await SignInAsync();
        await released.WaitAsync(ForceStoppedOwnerHost.Timeout);
    }

    public const string Released = "nothing";

    /// <summary>What of the sign-in's state is still taken: the busy flag, the session lock and the login.</summary>
    public string SignInState()
    {
        object? Field(string name) => typeof(EpicMappingService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Service);
        var taken = new List<string>();
        if (Field("_isProcessingInt") is int busy && busy != 0) taken.Add("the busy flag");
        if (Field("_sessionLock") is SemaphoreSlim { CurrentCount: 0 }) taken.Add("the session lock");
        if (Field("_processingLogin") is not null) taken.Add("the login");
        return taken.Count == 0 ? Released : string.Join(", ", taken);
    }

    public void Dispose()
    {
        ReleaseLibraryRead();
        Service.Dispose();
        _http.Dispose();
        _fixture.Dispose();
    }
}
