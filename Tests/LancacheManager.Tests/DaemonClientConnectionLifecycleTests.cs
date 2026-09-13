using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Services.SteamPrefill;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class DaemonClientConnectionLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteClose_FailsPendingCallAndReconnectsCleanlyAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var closeServer = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            await ReadRequestAsync(stream, timeout.Token);
        }, timeout.Token);

        await Assert.ThrowsAsync<IOException>(() => client.SendCommandAsync(
            "blocked",
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: timeout.Token));
        await closeServer;
        await WaitUntilAsync(() => !IsConnected(client), timeout.Token);

        var responseWritten = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closeReconnectedServer = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var responseServer = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.Id, success: true, request.Type, null, timeout.Token);
            responseWritten.TrySetResult();
            await closeReconnectedServer.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        try
        {
            var response = await client.SendCommandAsync(
                "after-reconnect",
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: timeout.Token);
            await responseWritten.Task.WaitAsync(timeout.Token);

            Assert.True(response.Success);
            Assert.Equal("after-reconnect", response.Message);
            Assert.True(IsConnected(client));
        }
        finally
        {
            closeReconnectedServer.TrySetResult();
        }

        await responseServer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCalls_CorrelateResponsesThatArriveOutOfOrderAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var first = await ReadRequestAsync(stream, timeout.Token);
            var second = await ReadRequestAsync(stream, timeout.Token);

            await WriteResponseAsync(stream, second.Id, success: true, second.Type, null, timeout.Token);
            await WriteResponseAsync(stream, first.Id, success: true, first.Type, null, timeout.Token);
        }, timeout.Token);

        var firstCall = client.SendCommandAsync("first", timeout: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        var secondCall = client.SendCommandAsync("second", timeout: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        var responses = await Task.WhenAll(firstCall, secondCall);

        Assert.Equal("first", responses[0].Message);
        Assert.Equal("second", responses[1].Message);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelPrefillAsync_RejectedResponseThrowsAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("cancel-prefill", request.Type);
            await WriteResponseAsync(stream, request.Id, success: false, null, "still running", timeout.Token);
        }, timeout.Token);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CancelPrefillAsync(timeout.Token));

        Assert.Contains("still running", exception.Message, StringComparison.Ordinal);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefillAsync_LoginRejectionPreservesTypedResult(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("prefill", request.Type);
            await WriteResponseAsync(stream, request.Id, false, null, "Login expired", timeout.Token, requiresLogin: true);
        }, timeout.Token);
        var result = await client.PrefillAsync(cancellationToken: timeout.Token);
        Assert.False(result.Success);
        Assert.True(result.RequiresLogin);
        Assert.Equal("Steam is no longer signed in. Sign in again, then retry the prefill.", result.ErrorMessage);
        Assert.Equal("errors.steam.signInLost", result.StageKey);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefillAsync_UsesRunIdForCommandAndAcceptsLegacyAck(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runId = Guid.NewGuid();
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal(runId.ToString(), request.Id);
            await WriteResponseAsync(stream, request.Id, true, "Prefill started", null, timeout.Token);
        }, timeout.Token);

        var result = await client.PrefillAsync(cancellationToken: timeout.Token, runId: runId);
        Assert.True(result.Success);
        await server;
    }

    [Theory]
    [InlineData("get-owned-games", "auth-lost", "errors.steam.signInLost", true)]
    [InlineData("get-selected-apps-status", "auth-lost", "errors.steam.signInLost", true)]
    [InlineData("check-cache-status", "auth-lost", "errors.steam.signInLost", true)]
    [InlineData("get-owned-games", "game-details-unavailable", "errors.steam.gameDetailsUnavailable", false)]
    [InlineData("get-selected-apps-status", "game-details-unavailable", "errors.steam.gameDetailsUnavailable", false)]
    [InlineData("check-cache-status", "game-details-unavailable", "errors.steam.gameDetailsUnavailable", false)]
    [InlineData("get-owned-games", null, "errors.prefill.requestFailed", false)]
    [InlineData("get-selected-apps-status", null, "errors.prefill.requestFailed", false)]
    [InlineData("check-cache-status", null, "errors.prefill.requestFailed", false)]
    public async Task Queries_ClassifyFailureAndHideRemoteException(
        string command, string? errorCode, string stageKey, bool requiresLogin)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal(command, request.Type);
            await WriteResponseAsync(stream, request.Id, false, null, "Remote.JobFailure: stack details",
                timeout.Token, requiresLogin, errorCode);
        }, timeout.Token);

        var failure = await Assert.ThrowsAsync<DaemonCommandException>(() => QueryAsync(client, command, timeout.Token));
        Assert.Equal(stageKey, failure.StageKey);
        Assert.Equal(requiresLogin, failure.RequiresLogin);
        Assert.DoesNotContain("Remote.JobFailure", failure.Message, StringComparison.Ordinal);
        await server;
    }

    [Theory]
    [InlineData("get-owned-games", null)]
    [InlineData("get-selected-apps-status", null)]
    [InlineData("check-cache-status", null)]
    [InlineData("get-owned-games", "{}")]
    [InlineData("get-selected-apps-status", "{}")]
    [InlineData("check-cache-status", "{\"apps\":null}")]
    public async Task Queries_RejectMissingRequiredBody(string command, string? body)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                result: body is null ? null : JsonSerializer.Deserialize<JsonElement>(body));
        }, timeout.Token);

        var failure = await Assert.ThrowsAsync<DaemonCommandException>(() => QueryAsync(client, command, timeout.Token));
        Assert.Equal("errors.prefill.requestFailed", failure.StageKey);
        await server;
    }

    [Theory]
    [InlineData("get-owned-games", "[]")]
    [InlineData("get-selected-apps-status", "{\"apps\":[]}")]
    [InlineData("check-cache-status", "{\"apps\":[]}")]
    public async Task Queries_AcceptExplicitEmptyResults(string command, string body)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                result: JsonSerializer.Deserialize<JsonElement>(body));
        }, timeout.Token);

        await QueryAsync(client, command, timeout.Token);
        await server;
    }

    private static Task QueryAsync(IDaemonClient client, string command, CancellationToken cancellationToken)
        => command switch
        {
            "get-owned-games" => client.GetOwnedGamesAsync(cancellationToken),
            "get-selected-apps-status" => client.GetSelectedAppsStatusAsync(cancellationToken: cancellationToken),
            "check-cache-status" => client.CheckCacheStatusAsync(
                [new CachedDepotInput { AppId = 10, DepotId = 11, ManifestId = 12 }], cancellationToken),
            _ => throw new ArgumentException("Unknown command", nameof(command))
        };

    [Theory]
    [InlineData("get-owned-games")]
    [InlineData("get-selected-apps-status")]
    [InlineData("check-cache-status")]
    public async Task Queries_CallerCancellationIsNotFailure(string command)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QueryAsync(client, command, cancellation.Token));
    }

    [Theory]
    [InlineData("get-owned-games")]
    [InlineData("get-selected-apps-status")]
    [InlineData("check-cache-status")]
    public async Task Queries_TransportLossIsSafeFailure(string command)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            await ReadRequestAsync(stream, timeout.Token);
        }, timeout.Token);
        var failure = await Assert.ThrowsAsync<DaemonCommandException>(() => QueryAsync(client, command, timeout.Token));
        Assert.Equal("errors.prefill.requestFailed", failure.StageKey);
        Assert.Equal("The prefill daemon could not complete the request. Try again.", failure.Message);
        await server;
    }

    [Fact]
    public void ProgressFields_AcceptOldAndCurrentDaemonFrames()
    {
        var old = JsonSerializer.Deserialize<SocketPrefillProgress>("{\"state\":\"completed\"}")!;
        Assert.Null(old.OperationId);
        Assert.Null(old.ErrorCode);
        Assert.Null(old.RequiresLogin);

        var runId = Guid.NewGuid().ToString();
        var current = JsonSerializer.Deserialize<SocketPrefillProgress>(JsonSerializer.Serialize(new
        {
            state = "error",
            operationId = runId,
            errorCode = "auth-lost",
            requiresLogin = true,
            currentAppId = 0
        }))!;
        Assert.Equal(runId, current.OperationId);
        Assert.Equal("auth-lost", current.ErrorCode);
        Assert.True(current.RequiresLogin);
        Assert.Equal("0", current.CurrentAppId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentProtocolAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var id = Guid.NewGuid();
        var instance = Guid.NewGuid().ToString();
        var options = new DaemonRunOptions { AppIds = ["10"], MaxConcurrency = 3 };
        var snapshot = new DaemonRunSnapshot
        {
            OperationId = id.ToString(),
            DaemonInstanceId = instance,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Sequence = 1
        };
        var page = new DaemonOperationPage(snapshot, options, true, 1,
            [new DaemonRunItem { AppId = "10", Depots = [new DepotManifestProgressInfo
                { DepotId = 20, ManifestId = 30, TotalBytes = 40 }] }], null);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var statusRequest = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("status", statusRequest.Type);
            await WriteResponseAsync(stream, statusRequest.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(RunClient.Capabilities(instance), json));
            var start = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("prefill", start.Type);
            Assert.Equal(id.ToString(), start.Id);
            await WriteResponseAsync(stream, start.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new PrefillResult
                { Success = true, RunId = id, DaemonInstanceId = instance, State = "started" }, json));
            var query = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("get-operation", query.Type);
            await WriteResponseAsync(stream, query.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(page, json));
            var cancel = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("cancel-prefill", cancel.Type);
            await WriteResponseAsync(stream, cancel.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(snapshot with { State = "cancelling" }, json));
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);
        try
        {
            Assert.True((await client.GetStatusAsync(timeout.Token))!.SupportsConcurrentPrefill);
            Assert.Equal(id, (await client.PrefillAsync(id, instance, options, cancellationToken: timeout.Token)).RunId);
            var result = await client.GetOperationAsync(id, instance, cancellationToken: timeout.Token);
            Assert.Equal(30UL, Assert.Single(Assert.Single(result.Items).Depots!).ManifestId);
            Assert.Equal("cancelling", (await client.CancelPrefillAsync(id, instance, timeout.Token)).State);
        }
        finally { release.TrySetResult(); }
        await server;
    }

    [Theory]
    [InlineData("auth-lost", "errors.steam.signInLost")]
    [InlineData("game-details-unavailable", "errors.steam.gameDetailsUnavailable")]
    [InlineData(null, "errors.prefill.requestFailed")]
    public async Task PrefillAsync_FailedResultUsesSafeClassification(string? code, string stageKey)
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(new
                {
                    success = false,
                    errorMessage = "Remote.JobFailure: stack details",
                    errorCode = code,
                    requiresLogin = code == "auth-lost"
                }));
        }, timeout.Token);

        var result = await client.PrefillAsync(cancellationToken: timeout.Token);
        Assert.False(result.Success);
        Assert.Equal(stageKey, result.StageKey);
        Assert.Equal(code == "auth-lost", result.RequiresLogin);
        Assert.DoesNotContain("Remote.JobFailure", result.ErrorMessage!, StringComparison.Ordinal);
        await server;
    }

    [Fact]
    public async Task PrefillDispatchIsConfirmedBeforeItsResponse()
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            read.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            await WriteResponseAsync(stream, request.Id, true, null, null, timeout.Token);
        }, timeout.Token);
        var pending = client.PrefillAsync(cancellationToken: timeout.Token,
            onCommandDispatched: () => dispatched.TrySetResult());
        try
        {
            await Task.WhenAll(read.Task, dispatched.Task).WaitAsync(timeout.Token);
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.True((await pending).Success);
        await server;
    }

    [Fact]
    public async Task PendingProgressHandlerDoesNotBlockCommandResponses()
    {
        using var endpoint = LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnProgressUpdate += async _ =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            finished.SetResult();
        };
        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var first = await ReadRequestAsync(stream, timeout.Token);
            await WriteFrameAsync(stream, "{\"type\":\"progress\",\"data\":{\"state\":\"downloading\"}}", timeout.Token);
            await WriteResponseAsync(stream, first.Id, true, null, null, timeout.Token);
            var second = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, second.Id, true, null, null, timeout.Token);
            await release.Task.WaitAsync(timeout.Token);
        }, timeout.Token);
        try
        {
            Assert.True((await client.SendCommandAsync("first", cancellationToken: timeout.Token)).Success);
            await entered.Task.WaitAsync(timeout.Token);
            Assert.True((await client.SendCommandAsync("second", cancellationToken: timeout.Token)).Success);
            Assert.False(finished.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await finished.Task.WaitAsync(timeout.Token);
        await server;
    }

    private static bool IsConnected(IDaemonClient client)
        => client switch
        {
            // Both real transports report connectivity through their shared base; anything else here
            // is a fake with no transport to be connected to.
            DaemonClientBase transportClient => transportClient.IsConnected,
            _ => false
        };

    private static async Task<DaemonRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        Assert.InRange(length, 1, 10 * 1024 * 1024);

        var requestBuffer = new byte[length];
        await ReadExactlyAsync(stream, requestBuffer, cancellationToken);
        using var request = JsonDocument.Parse(requestBuffer);
        return new DaemonRequest(
            request.RootElement.GetProperty("id").GetString()!,
            request.RootElement.GetProperty("type").GetString()!);
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        string requestId,
        bool success,
        string? message,
        string? error,
        CancellationToken cancellationToken,
        bool requiresLogin = false,
        string? errorCode = null,
        JsonElement? result = null)
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            id = requestId,
            success,
            message,
            error,
            errorCode,
            data = result,
            requiresLogin,
            completedAt = DateTime.UtcNow
        });
        await WriteFrameAsync(stream, responseJson, cancellationToken);
    }

    private static async Task WriteFrameAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        var responseBytes = Encoding.UTF8.GetBytes(json);

        await stream.WriteAsync(BitConverter.GetBytes(responseBytes.Length), cancellationToken);
        await stream.WriteAsync(responseBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before a complete frame was received.");
            }

            totalRead += read;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed record DaemonRequest(string Id, string Type);

    private sealed class LoopbackEndpoint : IDisposable
    {
        private readonly Socket _listener;
        private readonly string? _socketPath;

        private LoopbackEndpoint(Socket listener, string? socketPath)
        {
            _listener = listener;
            _socketPath = socketPath;
        }

        public static LoopbackEndpoint Create(bool useTcp)
        {
            if (useTcp)
            {
                var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(4);
                return new LoopbackEndpoint(listener, null);
            }

            var socketPath = Path.Combine(Path.GetTempPath(), $"lcm_{Guid.NewGuid():N}.sock");
            var unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            unixListener.Bind(new UnixDomainSocketEndPoint(socketPath));
            unixListener.Listen(4);
            return new LoopbackEndpoint(unixListener, socketPath);
        }

        public IDaemonClient CreateClient()
        {
            if (_socketPath != null)
            {
                return new SocketDaemonClient(
                    _socketPath,
                    sharedSecret: null,
                    logger: NullLogger<SocketDaemonClient>.Instance);
            }

            var endpoint = (IPEndPoint)_listener.LocalEndPoint!;
            return new TcpDaemonClient(
                IPAddress.Loopback.ToString(),
                endpoint.Port,
                sharedSecret: null,
                logger: NullLogger<TcpDaemonClient>.Instance);
        }

        public ValueTask<Socket> AcceptAsync(CancellationToken cancellationToken)
            => _listener.AcceptAsync(cancellationToken);

        public void Dispose()
        {
            _listener.Dispose();
            if (_socketPath != null)
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
