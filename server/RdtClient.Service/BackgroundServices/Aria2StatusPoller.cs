using Aria2NET;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RdtClient.Service.Services;
using RdtClient.Service.Services.Downloaders;

namespace RdtClient.Service.BackgroundServices;

/// <summary>
/// Polls aria2c's JSON-RPC for per-download status (bytesDone / bytesTotal / speed)
/// independently of <see cref="TorrentRunner.Tick"/>. Previously this RPC was awaited
/// inline inside Tick — a single stuck call (observed 13-40s under high-throughput
/// downloads, hitting the 60s HttpClient ceiling) blocked the entire Tick body and
/// delayed dequeue, retry, completion, and deletion work. Splitting it out means a
/// slow aria2 only affects the bytes display, never the torrent state machine.
/// </summary>
public class Aria2StatusPoller(ILogger<Aria2StatusPoller> logger, IHttpClientFactory httpClientFactory) : BackgroundService
{
    // Cadence between successful polls. Matches the 1 s SignalR push so the dashboard
    // gets fresh bytes on every push when aria2 is healthy.
    private static readonly TimeSpan HealthyPollInterval = TimeSpan.FromSeconds(1);

    // Cadence after consecutive timeouts. Avoids hammering an unresponsive RPC server.
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromSeconds(5);

    // Per-call budget for the TellAll RPC. The shared HttpClient still has a 60 s
    // ceiling so a saturated aria2c can recover instead of being killed, but the
    // poller refuses to wait more than this for any single response.
    private static readonly TimeSpan TellAllRpcTimeout = TimeSpan.FromSeconds(4);

    // Per-downloader budget for the Update() fanout below. Aria2cDownloader.Update
    // can call Remove() (two RPCs) and then poll for file visibility with
    // Task.Delay(1000 * retryCount) for up to retry=10 — about 45 s on completion.
    // Without this cap, one completing download could stall byte updates for every
    // other active downloader in the same fanout. The budget is permissive enough to
    // let normal updates finish (typically a few ms — an in-memory gid lookup plus an
    // event invocation) while still preventing the worst case.
    private static readonly TimeSpan PerDownloaderUpdateTimeout = TimeSpan.FromSeconds(3);

    // Number of consecutive timeouts before we move to the slow-poll cadence.
    private const Int32 SlowPollThreshold = 3;

    // Holds the in-flight TellAllAsync task so we don't pile up parallel RPCs when
    // aria2 is consistently slow. We ALWAYS reuse this if non-null, even when it has
    // completed — gating on `IsCompleted == false` would discard slow-but-successful
    // results that land between cycles (very common under the 5 s slow-poll cadence).
    // The reference is cleared after a result is consumed OR when the downloader set
    // becomes empty (so a stale gid snapshot can't be applied to a new session).
    private Task<IList<DownloadStatusResult>>? _inFlight;

    // Edge-trigger flag for the "had downloaders → have none" transition. Used to
    // drop _inFlight exactly once on the transition so we don't apply a stale TellAll
    // snapshot when a fresh download session starts.
    private Boolean _previouslyHadDownloaders;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        logger.LogInformation("Aria2StatusPoller started.");

        var consecutiveTimeouts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var hasAria2Downloaders = TorrentRunner.ActiveDownloadClients
                .Any(m => m.Value.Type == Data.Enums.DownloadClient.Aria2c);

            if (hasAria2Downloaders)
            {
                _previouslyHadDownloaders = true;

                var outcome = await PollOnce(stoppingToken);

                if (outcome == PollOutcome.Timeout || outcome == PollOutcome.Faulted)
                {
                    consecutiveTimeouts += 1;
                }
                else
                {
                    consecutiveTimeouts = 0;
                }
            }
            else
            {
                if (_previouslyHadDownloaders)
                {
                    // Drop the pending TellAll. The orphaned task continues in the
                    // background until aria2 finally responds, and is then GC'd.
                    // Applying its result to a future session would cause
                    // Aria2cDownloader.Update to falsely report "Download was not
                    // found in Aria2" for downloads whose gids weren't captured in
                    // the stale snapshot.
                    _inFlight = null;
                    _previouslyHadDownloaders = false;
                }

                consecutiveTimeouts = 0;
            }

            var delay = consecutiveTimeouts >= SlowPollThreshold ? SlowPollInterval : HealthyPollInterval;
            await Task.Delay(delay, stoppingToken);
        }

        logger.LogInformation("Aria2StatusPoller stopped.");
    }

    private async Task<PollOutcome> PollOnce(CancellationToken stoppingToken)
    {
        Task<IList<DownloadStatusResult>> taskToWait;
        var startedNewCall = false;

        if (_inFlight is not null)
        {
            // Reuse even when IsCompleted. A completed task's await returns
            // immediately so this is free in the happy path, and it ensures we don't
            // throw away a slow-but-successful response that arrived after the
            // previous cycle's timeout but before this cycle's start.
            taskToWait = _inFlight;
        }
        else
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(60);

            var aria2 = new Aria2NetClient(Settings.Get.DownloadClient.Aria2cUrl, Settings.Get.DownloadClient.Aria2cSecret, httpClient, 1);

            taskToWait = aria2.TellAllAsync();
            _inFlight = taskToWait;
            startedNewCall = true;
        }

        var timeoutTask = Task.Delay(TellAllRpcTimeout, stoppingToken);
        var winner = await Task.WhenAny(taskToWait, timeoutTask);

        if (winner != taskToWait)
        {
            if (startedNewCall)
            {
                logger.LogWarning("Aria2 TellAll RPC did not respond within {Budget}ms — will retry next cycle. The call continues in the background; this is harmless and the next cycle will reuse it.",
                    TellAllRpcTimeout.TotalMilliseconds);
            }

            return PollOutcome.Timeout;
        }

        IList<DownloadStatusResult>? allDownloads = null;

        try
        {
            allDownloads = await taskToWait;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aria2 TellAll RPC failed: {Message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_inFlight, taskToWait))
            {
                _inFlight = null;
            }
        }

        if (allDownloads == null)
        {
            return PollOutcome.Faulted;
        }

        // Fan out to every active aria2 downloader so their in-memory BytesDone /
        // BytesTotal / Speed reflect the latest RPC snapshot. TorrentRunner.Tick and
        // TorrentDtoMapper read these via TorrentRunner.GetStats.
        //
        // Each per-downloader Update runs in its own Task with a per-call budget so
        // one stuck Update (e.g. a completing download spinning in the file-visibility
        // retry loop in Aria2cDownloader.Update) doesn't stall byte updates for every
        // other active downloader.
        var updateTasks = new List<Task>();

        foreach (var activeDownload in TorrentRunner.ActiveDownloadClients)
        {
            if (activeDownload.Value.Downloader is Aria2cDownloader aria2Downloader)
            {
                updateTasks.Add(RunUpdateWithBudget(aria2Downloader, activeDownload.Key, allDownloads));
            }
        }

        if (updateTasks.Count > 0)
        {
            await Task.WhenAll(updateTasks);
        }

        return PollOutcome.Updated;
    }

    private async Task RunUpdateWithBudget(Aria2cDownloader aria2Downloader, Guid downloadId, IList<DownloadStatusResult> allDownloads)
    {
        var updateTask = SafeUpdate(aria2Downloader, downloadId, allDownloads);
        var timeoutTask = Task.Delay(PerDownloaderUpdateTimeout);

        var winner = await Task.WhenAny(updateTask, timeoutTask);

        if (winner != updateTask)
        {
            logger.LogWarning("Aria2cDownloader.Update for {DownloadId} exceeded {Budget}ms — skipping this cycle. The call continues in the background.",
                downloadId, PerDownloaderUpdateTimeout.TotalMilliseconds);
        }
    }

    private async Task SafeUpdate(Aria2cDownloader aria2Downloader, Guid downloadId, IList<DownloadStatusResult> allDownloads)
    {
        try
        {
            await aria2Downloader.Update(allDownloads);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aria2cDownloader.Update failed for {DownloadId}: {Message}", downloadId, ex.Message);
        }
    }

    private enum PollOutcome
    {
        Updated,
        Timeout,
        Faulted
    }
}
