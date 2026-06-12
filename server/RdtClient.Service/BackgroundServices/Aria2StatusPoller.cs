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
    //
    // Bumped from 4 s -> 10 s after observing that aria2's RPC routinely takes
    // 6–8 s under high-throughput downloads (130 MB/s 4K WebDL pull). A 4 s budget
    // was timing out just before the response arrived, then we'd wait HealthyPoll
    // (1 s) before retrying — net 5+ s of stale dashboard data per cycle. With 10 s
    // we typically catch the response on the first try and the dashboard only
    // sees stale numbers for the actual response latency, not budget + retry.
    private static readonly TimeSpan TellAllRpcTimeout = TimeSpan.FromSeconds(10);

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

    // Holds the in-flight TellAllAsync plus when it was issued. The timestamp is the
    // discriminator for stale-snapshot handling: an Update can apply this snapshot to
    // a downloader only when the snapshot was *started* after that downloader's
    // current gid was observed (so missing-from-snapshot is meaningfully "aria2 lost
    // the download" rather than "snapshot is from before this gid registered").
    private InFlight? _inFlight;

    // First time we observed (DownloadId, Gid) together. Keyed by (id, gid) rather
    // than just id so a downloader that retries and gets a new aria2 gid doesn't
    // inherit the old gid's observation timestamp. Cleaned up at end of each cycle
    // for downloaders / gids no longer present in the active set.
    private readonly ConcurrentDictionary<(Guid DownloadId, String Gid), DateTime> _firstObservedGidAt = new();

    // Per-downloader in-flight guard for Update(). The per-call budget only bounds
    // how long the poller WAITS for an Update; SafeUpdate itself keeps running after
    // we abandon waiting. Without this guard a subsequent cycle would issue a
    // parallel Update on the same Aria2cDownloader, causing duplicate Remove() RPCs
    // and duplicate complete/error event emissions during the worst case (a download
    // in the file-visibility retry loop). Released via ContinueWith on the actual
    // SafeUpdate completion, NOT on the WhenAny race.
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
            var aria2Downloaders = TorrentRunner.ActiveDownloadClients
                .Where(m => m.Value.Type == Data.Enums.DownloadClient.Aria2c)
                // Skip clients whose terminal event already fired (Finished) and
                // downloaders that finalized (self-removed their gid from aria2 on
                // complete/error/cancel). A finished entry stays in
                // ActiveDownloadClients until the next TorrentRunner.Tick processes
                // it — a multi-second window in which the gid is already purged from
                // aria2 by our own Remove(), so evaluating a fresh snapshot here
                // would emit a spurious "Download was not found in Aria2".
                .Where(m => !m.Value.Finished)
                .Select(m => (DownloadId: m.Key, Downloader: m.Value.Downloader as Aria2cDownloader))
                .Where(x => x.Downloader is not null && !x.Downloader!.IsFinalized)
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

                // Drop the pending TellAll. The snapshot's StartedAt won't match
                // anything in a future session and the orphan task continues to GC.
                _inFlight = null;
            }

            // Prune _firstObservedGidAt of entries whose (DownloadId, Gid) no longer
            // exists in the active set — covers both deleted downloads and gid
            // rotations (Aria2cDownloader retries that produce a new gid).
            var currentKeys = new HashSet<(Guid, String)>();
            foreach (var (id, downloader) in aria2Downloaders)
            {
                var gid = downloader?.Gid;
                if (gid != null)
                {
                    currentKeys.Add((id, gid));
                }
            }

            foreach (var key in _firstObservedGidAt.Keys)
            {
                if (!currentKeys.Contains(key))
                {
                    _firstObservedGidAt.TryRemove(key, out _);
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
        // Record the first time we observe each (DownloadId, Gid) pair. Do this
        // BEFORE waiting on TellAll so even brand-new downloaders get a timestamp
        // immediately; if TellAll was issued before this moment, the snapshot is
        // older than this gid and we'll skip applying it.
        foreach (var (downloadId, downloader) in aria2Downloaders)
        {
            var gid = downloader!.Gid;
            if (gid != null)
            {
                _firstObservedGidAt.GetOrAdd((downloadId, gid), _ => DateTime.UtcNow);
            }
        }

        InFlight inFlight;
        var startedNewCall = false;

        if (_inFlight is not null)
        {
            inFlight = _inFlight;
        }
        else
        {
            var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(60);

            var aria2 = new Aria2NetClient(Settings.Get.DownloadClient.Aria2cUrl, Settings.Get.DownloadClient.Aria2cSecret, httpClient, 1);

            inFlight = new InFlight(aria2.TellAllAsync(), DateTime.UtcNow);
            _inFlight = inFlight;
            startedNewCall = true;
        }

        var timeoutTask = Task.Delay(TellAllRpcTimeout, stoppingToken);
        var winner = await Task.WhenAny(inFlight.Task, timeoutTask);

        if (winner != inFlight.Task)
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
            allDownloads = await inFlight.Task;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aria2 TellAll RPC failed: {Message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_inFlight, inFlight))
            {
                _inFlight = null;
            }
        }

        if (allDownloads == null)
        {
            return PollOutcome.Faulted;
        }

        var snapshotGids = new HashSet<String>(allDownloads.Where(d => d.Gid is not null).Select(d => d.Gid!));

        // Fan out to every aria2 downloader. For each, decide whether THIS snapshot
        // is safe to apply by comparing the snapshot's start time to when we first
        // observed the downloader's current gid. The legitimate "aria2 lost the
        // download" detection still fires when the snapshot is post-gid; only when
        // it's pre-gid do we skip.
        var updateTasks = new List<Task>();

        foreach (var (downloadId, downloaderRef) in aria2Downloaders)
        {
            var downloader = downloaderRef!;
            var gid = downloader.Gid;

            if (gid == null)
            {
                // Downloader hasn't received its aria2 gid yet — nothing to correlate.
                continue;
            }

            if (snapshotGids.Contains(gid))
            {
                // Present in the snapshot — always safe to apply.
                updateTasks.Add(RunUpdateWithBudget(downloader, downloadId, allDownloads));
                continue;
            }

            // Missing from snapshot. Apply only if the snapshot was issued AFTER we
            // observed this gid — otherwise the snapshot is from before the gid
            // registered with aria2 and missing-ness is meaningless.
            if (_firstObservedGidAt.TryGetValue((downloadId, gid), out var observedAt)
                && inFlight.StartedAt >= observedAt)
            {
                updateTasks.Add(RunUpdateWithBudget(downloader, downloadId, allDownloads));
            }
            // else: snapshot pre-dates the gid, or we have no observation timestamp
            // for this (id, gid) pair (the cleanup pruned it for a stale gid before
            // we re-observed). Skip; next cycle will have fresh state.
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
        // this cycle's Update for the same downloader. Released via ContinueWith on
        // the actual SafeUpdate task — NOT on the WhenAny race — so subsequent
        // cycles wait for the previous Update to truly finish before issuing fresh.
        if (!_updateInFlight.TryAdd(downloadId, 0))
        {
            logger.LogDebug("Aria2cDownloader.Update for {DownloadId} is still in flight from a previous cycle — skipping this cycle's update.", downloadId);
            return;
        }

        var safeUpdateTask = SafeUpdate(aria2Downloader, downloadId, allDownloads);

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

    private sealed record InFlight(Task<IList<DownloadStatusResult>> Task, DateTime StartedAt);

    private enum PollOutcome
    {
        Updated,
        Timeout,
        Faulted
    }
}
