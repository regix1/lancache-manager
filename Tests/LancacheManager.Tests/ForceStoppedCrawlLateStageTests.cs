using System.Net;
using LancacheManager.Middleware;
using LancacheManager.Models;
using SteamKit2;
using SteamKit2.Internal;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops a full depot crawl while it sits in a step that does not see the stop, and then
/// calls a local-file import as the newer depot run. The stopped crawl must not write its stage or its app
/// and batch counts into what the depot progress answer reports for the newer run. The crawl is held either
/// in its count of the stored mappings or inside the send of its Steam change-number request, which Steam
/// then answers. Both orders run: the import is called only after the stopped crawl ended, or first while
/// the stopped crawl is still held (it then gets the conflict answer) and again after the crawl ended.
/// </summary>
public sealed class ForceStoppedCrawlLateStageTests
{
    private const string HeldInTheTableCount = "held in its count of the table";
    private const string HeldInTheChangeNumberRequest = "held in its change-number request";
    private const string WakesBeforeTheNewerRun = "stopped crawl wakes before the import starts";
    private const string ImportCalledWhileTheStoppedCrawlIsHeld = "import called while the stopped crawl is held";

    [Theory]
    [InlineData(HeldInTheTableCount, WakesBeforeTheNewerRun)]
    [InlineData(HeldInTheTableCount, ImportCalledWhileTheStoppedCrawlIsHeld)]
    [InlineData(HeldInTheChangeNumberRequest, WakesBeforeTheNewerRun)]
    [InlineData(HeldInTheChangeNumberRequest, ImportCalledWhileTheStoppedCrawlIsHeld)]
    public async Task AForceStoppedCrawlThatWakesLate_LeavesThePolledPhaseToTheNewerRun(string held, string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;
        // The file the newer local-file import reads.
        host.WriteDepotFile();
        var request = new HeldChangeNumberRequest();
        try
        {
            Task StartTheImport() => host.Service.ImportFromLocalFileAsync(
                CancellationToken.None, new RunNotice(NotificationMode.All, RunTrigger.Manual),
                new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously));

            Task heldAt;
            if (held == HeldInTheTableCount)
            {
                // A crawl that gets as far as its app list is answered at once.
                host.SteamApi.Download(1).Fail();
                host.CountGate.Arm();
                heldAt = host.CountGate.Entered.Task;
            }
            else
            {
                host.SteamApi.Download(1).Answer.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"applist":{"apps":[{"appid":10,"name":"Ten"},{"appid":20,"name":"Twenty"},{"appid":30,"name":"Thirty"}]}}""")
                });
                ((SteamClient)CacheScanGateHarness.GetField(host.Service, "_steamClient")!).DebugNetworkListener = request;
                heldAt = request.Sent.Task;
            }

            async Task WakeTheStoppedCrawlAsync()
            {
                if (held == HeldInTheTableCount)
                {
                    host.CountGate.Open();
                    return;
                }

                // Steam answers the request, and the send returns.
                var answer = new ClientMsgProtobuf<CMsgClientPICSChangesSinceResponse>(EMsg.ClientPICSChangesSinceResponse)
                {
                    TargetJobID = await request.Sent.Task
                };
                answer.Body.current_change_number = 200;
                ((SteamApps)CacheScanGateHarness.GetField(host.Service, "_steamApps")!)
                    .HandleMsg(new PacketClientMsgProtobuf(EMsg.ClientPICSChangesSinceResponse, answer.Serialize()));
                request.Release();
            }

            var stoppedCrawl = await host.StartFullCrawlAsync()
                ?? throw new InvalidOperationException("The full crawl did not start");
            await Task.WhenAny(heldAt, stoppedCrawl).WaitAsync(timeout);
            if (!heldAt.IsCompleted)
            {
                throw new InvalidOperationException($"The full crawl ended without being {held}");
            }

            await host.ForceStopAsync(host.RunningCrawlId);

            if (order == ImportCalledWhileTheStoppedCrawlIsHeld)
            {
                // Called while the stopped crawl's code still runs, the import gets the conflict answer and
                // writes nothing into the depot progress.
                var before = host.Service.GetProgress();
                Exception? refusal = null;
                try
                {
                    await StartTheImport().WaitAsync(timeout);
                }
                catch (Exception exception) when (exception is not TimeoutException)
                {
                    refusal = exception;
                }

                Assert.True(refusal is ConflictException { StageKey: "errors.depot.mappingInProgress" },
                    $"A local-file import called while the force-stopped crawl was {held} answered " +
                    (refusal is null ? "by running" : $"{refusal.GetType().Name}: {refusal.Message}") +
                    $" (the depot progress went from {before.Status} to {host.Service.GetProgress().Status}); until the " +
                    "stopped crawl's code has ended it must get the conflict answer (errors.depot.mappingInProgress)");
            }

            // The import called once the stopped crawl has ended runs, and a poll while it runs reads its phase.
            await WakeTheStoppedCrawlAsync();
            await stoppedCrawl.WaitAsync(timeout);
            await host.NoCrawlShown.WaitAsync(timeout);
            host.ReadGate.Arm();
            var import = StartTheImport();
            await Task.WhenAny(host.ReadGate.Entered.Task, import).WaitAsync(timeout);
            var during = host.Service.GetProgress();
            host.ReadGate.Open();
            await import.WaitAsync(timeout);
            Assert.True(during.Status == DepotScanPhase.Importing,
                $"While the local-file import ran after the force-stopped crawl ({held}) ended, a poll of the depot " +
                $"progress reads the phase {during.Status}; the import sets {DepotScanPhase.Importing}");
        }
        finally
        {
            request.Release();
        }
    }

    /// <summary>
    /// Holds the crawl inside the send of its first PICS change-number request, a step that does not see
    /// a cancel, until <see cref="Release"/>. <see cref="Sent"/> carries the request's job, which Steam's
    /// answer names.
    /// </summary>
    private sealed class HeldChangeNumberRequest : IDebugNetworkListener
    {
        private readonly ManualResetEventSlim _release = new();

        public TaskCompletionSource<JobID> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.Set();

        public void OnIncomingNetworkMessage(EMsg msgType, byte[] message)
        {
        }

        public void OnOutgoingNetworkMessage(EMsg msgType, byte[] message)
        {
            if (msgType != EMsg.ClientPICSChangesSinceRequest || Sent.Task.IsCompleted) return;
            Sent.TrySetResult(new PacketClientMsgProtobuf(msgType, message).SourceJobID);
            _release.Wait(ForceStoppedCrawlHost.Timeout);
        }
    }
}
