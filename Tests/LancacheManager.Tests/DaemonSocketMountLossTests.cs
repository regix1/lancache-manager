using System.Diagnostics;
using LancacheManager.Core.Services.SteamPrefill;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// What happens when the daemon's socket directory disappears under a still-running container,
/// which is what deleting or remounting the data directory does.
///
/// The connect deliberately waits up to 30 seconds for a socket file to appear, because a freshly
/// created container publishes its socket a moment after it starts. That wait is dead time when the
/// directory holding the socket is gone: nothing can ever publish into a path that does not exist,
/// and the manager pays it once per stranded container per service before its recovery removes the
/// container. These tests pin the difference between the two cases.
/// </summary>
public sealed class DaemonSocketMountLossTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    [Fact]
    public async Task ConnectAsync_SocketDirectoryMissing_FailsImmediatelyInsteadOfWaitingOutTheTimeout()
    {
        // No directory at all: the bind mount is gone, exactly the deleted-data-folder case.
        var socketPath = Path.Combine(NewTempRoot(), "sessions", "abc123", "responses", "daemon.sock");
        using var client = new SocketDaemonClient(socketPath, sharedSecret: null, logger: NullLogger<SocketDaemonClient>.Instance);

        var elapsed = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<FileNotFoundException>(
            () => client.ConnectAsync(CancellationToken.None));
        elapsed.Stop();

        // Well under the 30s publish wait: the point of the change is that this no longer blocks startup.
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"expected an immediate failure, took {elapsed.Elapsed.TotalSeconds:F1}s");

        // The path travels as FileName so the caller can name the socket without parsing the message.
        Assert.Equal(socketPath, failure.FileName);
        Assert.Contains("no longer exists", failure.Message);
    }

    [Fact]
    public async Task ConnectAsync_DirectoryPresentButSocketNotPublishedYet_StillWaitsForTheDaemon()
    {
        // The directory exists and no socket is in it: a container that is still starting. This one
        // must keep waiting, so a slow daemon is not mistaken for a lost mount.
        var responsesDir = Path.Combine(NewTempRoot(), "sessions", "abc123", "responses");
        Directory.CreateDirectory(responsesDir);
        var socketPath = Path.Combine(responsesDir, "daemon.sock");
        using var client = new SocketDaemonClient(socketPath, sharedSecret: null, logger: NullLogger<SocketDaemonClient>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cts.Token));
        elapsed.Stop();

        // Cancelled while still waiting rather than having given up on its own.
        Assert.True(
            elapsed.Elapsed > TimeSpan.FromSeconds(1),
            $"expected the publish wait to still apply, gave up after {elapsed.Elapsed.TotalSeconds:F1}s");
    }

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lcm-socket-mount-{Guid.NewGuid():N}");
        _tempRoots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _tempRoots.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A temp tree the OS still holds open is not this test's concern.
            }
        }
    }
}
