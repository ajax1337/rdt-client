using System.Collections.Concurrent;
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

    // Per-downloader budget for the Update() fanout. Aria2cDownloader.Update can call
    // Remove() (two RPCs) and then poll for file visibility with Task.Delay(1000 *
    // retryCount) for up to retry=10 — about 45 s on completion. Without this cap, one
    // completing download could stall byte updates for every other active downloader
    // in the same fanout. The cap is permissive enough to let normal updates finish
    // (typically a few ms — an in-memory gid lookup plus an event invocation) while
    // still preventing the worst case.
    private static readonly TimeSpan PerDownloaderUpdateTimeout = TimeSpan.FromSeconds(3);

    // Number of consecutive timeouts before we move to the slow-poll cadence.
    private const Int32 SlowPollThreshold = 3;

    // Holds the in-flight TellAllAsync task so we don't pile up parallel RPCs when
    // aria2 is consistently slow. We ALWAYS reuse this if non-null, even when it has
    // completed — gating on `IsCompleted == false` would discard slow-but-successful
    // results that land between cycles (very common under the 5 s slow-poll cadence).
    private Task<IList<DownloadStatusResult>>? _inFlight;

    // Per-downloader record of whether we have ever seen this downloader's gid in a
    // successful snapshot. Used to filter the fanout: a snapshot that doesn't include
    // a never-seen-before downloader's gid is treated as "the downloader hadn't
    // registered yet when this snapshot was taken" rather than "aria2 has lost the
    // download". Prevents the spurious "Download was not found in Aria2" event when
    // a TellAll hangs across an A-completes-B-starts transition.
    private readonly ConcurrentDictionary<Guid, Byte> _seenInSnapshot = new();

    // Per-downloader in-flight guard for Update(). The per-call budget (above) only
    // bounds how long the poller WAITS for an Update; SafeUpdate itself keeps running
    // in the background after we abandon it. Without this guard, a subsequent cycle
    // would issue a parallel Update on the same Aria2cDownloader, causing duplicate
    // Remove() RPCs and duplicate complete/error event emissions during the worst
    // case (a download in the file-visibility retry loop).
    private readonly ConcurrentDictionary<Guid, Byte> _updateInFlight = new();

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
            // Snapshot the active aria2 downloader set into a list so the rest of the
            // cycle sees a consistent view (ActiveDownloadClients may mutate concurrently).
            var aria2Downloaders = TorrentRunner.ActiveDownloadClients
                .Where(m => m.Value.Type == Data.Enums.DownloadClient.Aria2c)
                .Select(m => (DownloadId: m.Key, Downloader: m.Value.Downloader as Aria2cDownloader))
                .Where(x => x.Downloader is not null)
                .ToList();

            if (aria2Downloaders.Count > 0)
            {
                var outcome = await PollOnce(aria2Downloaders, stoppingToken);

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
                consecutiveTimeouts = 0;
                // Drop any pending TellAll — its result would be applied to a future
                // session against gids it never knew about. (The orphaned task continues
                // until aria2 finally answers, then is GC'd.)
                _inFlight = null;
            }

            // Prune the seen-in-snapshot map so we don't leak entries for deleted
            // downloads. Keep only entries whose downloadId is still in the active set.
            // O(n) per cycle but n is tiny (a handful of active downloads).
            var activeIds = new HashSet<Guid>(aria2Downloaders.Select(d => d.DownloadId));
            foreach (var key in _seenInSnapshot.Keys)
            {
                if (!activeIds.Contains(key))
                {
                    _seenInSnapshot.TryRemove(key, out _);
                }
            }

            var delay = consecutiveTimeouts >= SlowPollThreshold ? SlowPollInterval : HealthyPollInterval;
            await Task.Delay(delay, stoppingToken);
        }

        logger.LogInformation("Aria2StatusPoller stopped.");
    }

    private async Task<PollOutcome> PollOnce(
        IReadOnlyList<(Guid DownloadId, Aria2cDownloader? Downloader)> aria2Downloaders,
        CancellationToken stoppingToken)
    {
        Task<IList<DownloadStatusResult>> taskToWait;
        var startedNewCall = false;

        if (_inFlight is not null)
        {
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
                logger.LogWarning("Aria2 TellAll RPC did not respond within {Budget}ms — will retry next cycle. The call continues in the background; the next cycle will reuse it.",
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

        // Build a gid set so we can do O(1) presence checks per downloader.
        var snapshotGids = new HashSet<String>(allDownloads.Where(d => d.Gid is not null).Select(d => d.Gid!));

        // Fan out to every aria2 downloader, but only when applying the snapshot is
        // safe (see _seenInSnapshot comment). Each per-downloader Update runs in its
        // own Task under a per-call budget AND a per-downloader in-flight guard so
        // neither one slow Update stalls the rest, nor parallel Updates pile up on
        // the same Aria2cDownloader.
        var updateTasks = new List<Task>();

        foreach (var (downloadId, downloaderRef) in aria2Downloaders)
        {
            var downloader = downloaderRef!;
            var gid = downloader.Gid;

            if (gid == null)
            {
                // Downloader hasn't received its aria2 gid yet — there's nothing to
                // correlate the snapshot against. Skip until the next cycle.
                continue;
            }

            var presentInSnapshot = snapshotGids.Contains(gid);
            var seenBefore = _seenInSnapshot.ContainsKey(downloadId);

            if (presentInSnapshot)
            {
                // Record so future "missing" snapshots are interpreted as legitimate
                // aria2-lost-the-download events rather than stale-snapshot artifacts.
                _seenInSnapshot[downloadId] = 0;
            }
            else if (!seenBefore)
            {
                // Never seen this downloader's gid in any snapshot. Either the
                // snapshot was taken before the gid registered with aria2, or this
                // downloader started after the snapshot was taken (e.g. A-finishes-
                // B-starts during a slow TellAll). Skip — emitting "not found in
                // Aria2" here would be spurious.
                continue;
            }

            // Either present-now or seen-before. The seen-before path lets us surface
            // the legitimate "aria2 lost the download" case (Update will emit "not
            // found in Aria2" when its gid isn't in the snapshot list).
            updateTasks.Add(RunUpdateWithBudget(downloader, downloadId, allDownloads));
        }

        if (updateTasks.Count > 0)
        {
            await Task.WhenAll(updateTasks);
        }

        return PollOutcome.Updated;
    }

    private async Task RunUpdateWithBudget(Aria2cDownloader aria2Downloader, Guid downloadId, IList<DownloadStatusResult> allDownloads)
    {
        // Per-downloader in-flight guard. If a previous cycle's Update is still
        // running (e.g. stuck in the file-visibility retry loop on completion), skip
        // this cycle's Update for the same downloader. The previous one will clear
        // the guard when it actually completes; the next cycle then issues fresh.
        if (!_updateInFlight.TryAdd(downloadId, 0))
        {
            logger.LogDebug("Aria2cDownloader.Update for {DownloadId} is still in flight from a previous cycle — skipping this cycle's update.", downloadId);
            return;
        }

        var safeUpdateTask = SafeUpdate(aria2Downloader, downloadId, allDownloads);

        // Schedule the guard release for whenever SafeUpdate actually finishes, not
        // when we stop waiting on it. Crucial — without this the guard would be
        // released at PerDownloaderUpdateTimeout and the next cycle could launch a
        // parallel Update before the previous one finished.
        _ = safeUpdateTask.ContinueWith(prev => _updateInFlight.TryRemove(downloadId, out _), TaskScheduler.Default);

        var timeoutTask = Task.Delay(PerDownloaderUpdateTimeout);
        var winner = await Task.WhenAny(safeUpdateTask, timeoutTask);

        if (winner != safeUpdateTask)
        {
            logger.LogWarning("Aria2cDownloader.Update for {DownloadId} exceeded {Budget}ms — leaving it running in the background; the per-downloader in-flight guard will keep subsequent cycles from issuing parallel Updates until it finishes.",
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
