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

    // Per-call budget. The shared HttpClient still has a 60 s ceiling so a saturated
    // aria2c can recover instead of being killed, but the poller refuses to wait more
    // than this for any single response.
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(4);

    // Number of consecutive timeouts before we move to the slow-poll cadence.
    private const Int32 SlowPollThreshold = 3;

    // Holds the in-flight TellAllAsync. We never issue a parallel call: if a previous
    // poll is still pending we wait on it (subject to RpcTimeout) instead of starting
    // a second one. Reset when a poll completes or faults.
    private Task<IList<DownloadStatusResult>>? _inFlight;

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

        if (_inFlight is { IsCompleted: false })
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

        var timeoutTask = Task.Delay(RpcTimeout, stoppingToken);
        var winner = await Task.WhenAny(taskToWait, timeoutTask);

        if (winner != taskToWait)
        {
            if (startedNewCall)
            {
                logger.LogWarning("Aria2 TellAll RPC did not respond within {Budget}ms — will retry next cycle. The call continues in the background; this is harmless and the next cycle will reuse it.",
                    RpcTimeout.TotalMilliseconds);
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
        foreach (var activeDownload in TorrentRunner.ActiveDownloadClients)
        {
            if (activeDownload.Value.Downloader is Aria2cDownloader aria2Downloader)
            {
                try
                {
                    await aria2Downloader.Update(allDownloads);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Aria2cDownloader.Update failed for {DownloadId}: {Message}",
                        activeDownload.Key, ex.Message);
                }
            }
        }

        return PollOutcome.Updated;
    }

    private enum PollOutcome
    {
        Updated,
        Timeout,
        Faulted
    }
}
