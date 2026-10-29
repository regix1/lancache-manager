using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Docker.DotNet.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;

namespace LancacheManager.Tests;

/// <summary>
/// Drives the production Docker gateway against a TCP endpoint that answers the first request with a
/// version, so the gateway connects, and then accepts every connection and never answers, which is how a
/// Docker daemon waiting on a container lock looks to its client.
/// </summary>
public sealed class DockerPrefillContainerGatewayTests : IAsyncLifetime
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _heldConnections = new();
    private readonly DockerPrefillContainerGateway _gateway = new();

    // Set by an image pull test: later connections get this answer instead of being held.
    private Func<NetworkStream, Task>? _laterAnswer;

    private Uri DockerUri => new($"tcp://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public async Task InitializeAsync()
    {
        _listener.Start();
        _ = AnswerFirstThenHoldAsync();
        await _gateway.ConnectAsync(DockerUri, CancellationToken.None);
    }

    // Ends with the listener's own exception when DisposeAsync stops it.
    private async Task AnswerFirstThenHoldAsync()
    {
        using (var first = await _listener.AcceptTcpClientAsync())
        {
            var stream = first.GetStream();
            await ReadRequestAsync(stream);
            const string body = "{\"Version\":\"test-engine\"}";
            var answer = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n" +
                $"Content-Length: {body.Length}\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
        }

        while (true)
        {
            var connection = await _listener.AcceptTcpClientAsync();
            lock (_heldConnections) _heldConnections.Add(connection);
            if (_laterAnswer is { } laterAnswer)
            {
                _ = AnswerAsync(connection.GetStream(), laterAnswer);
            }
        }
    }

    private static async Task AnswerAsync(NetworkStream stream, Func<NetworkStream, Task> answer)
    {
        await ReadRequestAsync(stream);
        await answer(stream);
    }

    private static async Task ReadRequestAsync(NetworkStream stream)
    {
        var request = new StringBuilder();
        var buffer = new byte[1024];
        // Docker.DotNet ends its last header line with a bare line feed on Linux, so the end is "\n\r\n" there.
        while (!request.ToString().Contains("\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }

            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }

    // Starts a streamed pull answer, one JSON progress message per chunk, as Docker sends a pull.
    private static Task WritePullHeadersAsync(NetworkStream stream)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n")).AsTask();

    private static Task WritePullMessageAsync(NetworkStream stream, string status)
    {
        var message = $"{{\"status\":\"{status}\"}}\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"{message.Length:x}\r\n{message}\r\n")).AsTask();
    }

    // Starts an answer that stays open after its headers: chunked as Docker answers a container's logs, or
    // raw until close as it answers an exec start, which the client takes over as a hijacked stream.
    private static Task WriteStreamHeadersAsync(NetworkStream stream, bool chunked)
    {
        var framing = chunked ? "Transfer-Encoding: chunked\r\n" : string.Empty;
        return stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/vnd.docker.raw-stream\r\n{framing}\r\n")).AsTask();
    }

    // One stdout frame of a container's output: a stream byte, a four-byte big-endian size, then the text.
    private static Task WriteOutputFrameAsync(NetworkStream stream, string text, bool chunked)
    {
        var textBytes = Encoding.ASCII.GetBytes(text);
        var frame = new byte[8 + textBytes.Length];
        frame[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), textBytes.Length);
        textBytes.CopyTo(frame, 8);
        if (!chunked)
        {
            return stream.WriteAsync(frame).AsTask();
        }

        var chunk = Encoding.ASCII.GetBytes($"{frame.Length:x}\r\n").Concat(frame).Concat("\r\n"u8.ToArray()).ToArray();
        return stream.WriteAsync(chunk).AsTask();
    }

    // Each short call gives up within the gateway's limit instead of Docker.DotNet's 100-second default.
    // The log and exec calls have the same limit on the step that opens them.
    [Fact]
    public async Task ShortCalls_DockerNeverAnswers_ThrowTimeoutWithinTheLimit()
    {
        var calls = new (string Name, Task Call)[]
        {
            ("list", _gateway.ListContainersAsync(new ContainersListParameters { All = true }, CancellationToken.None)),
            ("create", _gateway.CreateContainerAsync(new CreateContainerParameters { Name = "held-container", Image = "held-image" }, CancellationToken.None)),
            ("start", _gateway.StartContainerAsync("held-container", null, CancellationToken.None)),
            ("container inspect", _gateway.InspectContainerAsync("held-container", CancellationToken.None)),
            ("image inspect", _gateway.InspectImageAsync("held-image", CancellationToken.None)),
            ("stop", _gateway.StopContainerAsync("held-container", new ContainerStopParameters(), CancellationToken.None)),
            ("kill", _gateway.KillContainerAsync("held-container", new ContainerKillParameters(), CancellationToken.None)),
            ("remove", _gateway.RemoveContainerAsync("held-container", new ContainerRemoveParameters(), CancellationToken.None)),
            ("volume remove", _gateway.RemoveVolumeAsync("held-volume", false, CancellationToken.None)),
            ("log open", _gateway.GetContainerLogsAsync("held-container", false, new ContainerLogsParameters { ShowStdout = true, ShowStderr = true }, CancellationToken.None)),
            ("exec create", _gateway.ExecCreateContainerAsync("held-container", new ContainerExecCreateParameters { Cmd = new[] { "true" } }, CancellationToken.None)),
            ("exec start", _gateway.StartAndAttachContainerExecAsync("held-exec", false, CancellationToken.None)),
            ("exec inspect", _gateway.InspectContainerExecAsync("held-exec", CancellationToken.None))
        };

        await Task.WhenAny(Task.WhenAll(calls.Select(call => call.Call)), Task.Delay(TimeSpan.FromSeconds(15)));

        var waiting = calls.Where(call => !call.Call.IsCompleted).Select(call => call.Name).ToList();
        Assert.True(waiting.Count == 0, $"Still waiting on Docker after 15 seconds: {string.Join(", ", waiting)}.");
        foreach (var (_, call) in calls)
        {
            await Assert.ThrowsAsync<DockerTimeoutException>(() => call);
        }
    }

    // A log stream that has opened stays open: only the open step has the limit, so a container that writes
    // nothing for longer than the limit still delivers its next line.
    [Fact]
    public async Task OpenedLogStream_DockerQuietPastTheLimit_StaysReadable()
    {
        _laterAnswer = async stream =>
        {
            await WriteStreamHeadersAsync(stream, chunked: true);
            await Task.Delay(TimeSpan.FromSeconds(11));
            await WriteOutputFrameAsync(stream, "late log line", chunked: true);
        };

        using var logs = await _gateway.GetContainerLogsAsync(
            "held-container", false, new ContainerLogsParameters { ShowStdout = true, ShowStderr = true }, CancellationToken.None);
        var waited = Stopwatch.StartNew();
        var buffer = new byte[64];
        var read = await logs.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);

        Assert.True(waited.Elapsed > TimeSpan.FromSeconds(10), "The line arrived before the limit had passed.");
        Assert.Equal("late log line", Encoding.ASCII.GetString(buffer, 0, read.Count));
    }

    // An exec stream that has opened stays open the same way: its output is read, not limited, after the start.
    [Fact]
    public async Task OpenedExecStream_DockerQuietPastTheLimit_StaysReadable()
    {
        _laterAnswer = async stream =>
        {
            await WriteStreamHeadersAsync(stream, chunked: false);
            await Task.Delay(TimeSpan.FromSeconds(11));
            await WriteOutputFrameAsync(stream, "late exec output", chunked: false);
        };

        using var exec = await _gateway.StartAndAttachContainerExecAsync("held-exec", false, CancellationToken.None);
        var waited = Stopwatch.StartNew();
        var buffer = new byte[64];
        var read = await exec.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);

        Assert.True(waited.Elapsed > TimeSpan.FromSeconds(10), "The output arrived before the limit had passed.");
        Assert.Equal("late exec output", Encoding.ASCII.GetString(buffer, 0, read.Count));
    }

    // A Docker that never answers the version check leaves the gateway unconnected, so no later call runs on
    // a client nothing checked.
    [Fact]
    public async Task Connect_DockerNeverAnswers_ThrowsTimeoutAndStaysUnavailable()
    {
        using var gateway = new DockerPrefillContainerGateway();
        var connect = gateway.ConnectAsync(DockerUri, CancellationToken.None);

        Assert.True(await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(15))) == connect);
        await Assert.ThrowsAsync<DockerTimeoutException>(() => connect);
        Assert.False(gateway.IsAvailable);
    }

    // A cancel of the caller's own token stays a cancel, so a request the person abandoned is not
    // reported as a Docker that did not answer.
    [Fact]
    public async Task ShortCall_CallerCancels_ThrowsCanceled()
    {
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var error = await Record.ExceptionAsync(() =>
            _gateway.ListContainersAsync(new ContainersListParameters { All = true }, caller.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    // An image pull streams for as long as the download takes, so it waits past the short-call limit.
    [Fact]
    public async Task ImagePull_DockerSlow_KeepsWaitingPastTheLimit()
    {
        using var caller = new CancellationTokenSource();
        var pull = _gateway.CreateImageAsync(
            new ImagesCreateParameters { FromImage = "held-image", Tag = "latest" }, null, new Progress<JSONMessage>(), caller.Token);

        await Task.Delay(TimeSpan.FromSeconds(11));

        Assert.False(pull.IsCompleted);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pull);
    }

    // A pull whose progress stops ends at the silence limit instead of holding its caller for good.
    [Fact]
    public async Task ImagePull_ProgressStops_EndsAtTheSilenceLimit()
    {
        _laterAnswer = async stream =>
        {
            await WritePullHeadersAsync(stream);
            await WritePullMessageAsync(stream, "Pulling from held-image");
            await WritePullMessageAsync(stream, "Downloading");
        };
        var pull = _gateway.CreateImageAsync(
            new ImagesCreateParameters { FromImage = "held-image", Tag = "latest" }, null, new Progress<JSONMessage>(), CancellationToken.None);

        Assert.True(await Task.WhenAny(pull, Task.Delay(TimeSpan.FromSeconds(90))) == pull, "The pull was still waiting after 90 seconds.");
        await Assert.ThrowsAsync<DockerTimeoutException>(() => pull);
    }

    // A pull that keeps reporting progress runs past the silence limit and finishes, because each
    // message restarts the limit.
    [Fact]
    public async Task ImagePull_ProgressKeepsComing_Finishes()
    {
        _laterAnswer = async stream =>
        {
            await WritePullHeadersAsync(stream);
            for (var message = 0; message < 4; message++)
            {
                await WritePullMessageAsync(stream, "Downloading");
                await Task.Delay(TimeSpan.FromSeconds(25));
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
        };
        var started = DateTime.UtcNow;

        await _gateway.CreateImageAsync(
            new ImagesCreateParameters { FromImage = "held-image", Tag = "latest" }, null, new Progress<JSONMessage>(), CancellationToken.None);

        Assert.True(DateTime.UtcNow - started > TimeSpan.FromSeconds(60));
    }

    public Task DisposeAsync()
    {
        _gateway.Dispose();
        _listener.Stop();
        lock (_heldConnections)
        {
            foreach (var connection in _heldConnections)
            {
                connection.Dispose();
            }
        }
        return Task.CompletedTask;
    }
}
