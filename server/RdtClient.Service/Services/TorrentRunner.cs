using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services.Downloaders;

namespace RdtClient.Service.Services;

public class TorrentRunner(
    ILogger<TorrentRunner> logger,
    Torrents torrents,
    Downloads downloads,
    RemoteService remoteService,
    IHttpClientFactory httpClientFactory,
    IRateLimitCoordinator coordinator)
{
    public static readonly ConcurrentDictionary<Guid, DownloadClient> ActiveDownloadClients = new();
    public static readonly ConcurrentDictionary<Guid, UnpackClient> ActiveUnpackClients = new();

    // Last positive BytesTotal we observed for each download. Once a download finishes
    // and is removed from ActiveDownloadClients, we still need its size to compute a
    // byte-weighted overall progress for the torrent — without this cache, finished
    // downloads contributed 0 / 0 to the math and a sibling that was mid-flight could
    // briefly drive the bar to 100%. Grows with throughput but each entry is 24 bytes
    // (Guid + Int64), and a download row is permanent until the user deletes the
    // torrent; the same caller is responsible for evicting on delete.
    private static readonly ConcurrentDictionary<Guid, Int64> KnownDownloadSize = new();

    private DateTimeOffset? _lastNextAllowedAt;

    public static Boolean IsPausedForLowDiskSpace { get; set; }

    public static (Int64 Speed, Int64 BytesTotal, Int64 BytesDone) GetStats(Guid downloadId)
    {
        if (ActiveDownloadClients.TryGetValue(downloadId, out var downloadClient))
        {
            if (downloadClient.BytesTotal > 0)
            {
                // Capture the live size so we can still report it after the download
                // is removed from the active dictionary on completion.
                KnownDownloadSize[downloadId] = downloadClient.BytesTotal;
            }

            return (downloadClient.Speed, downloadClient.BytesTotal, downloadClient.BytesDone);
        }

        if (ActiveUnpackClients.TryGetValue(downloadId, out var unpackClient))
        {
            return (0, 100, unpackClient.Progess);
        }

        if (KnownDownloadSize.TryGetValue(downloadId, out var size))
        {
            // No active downloader but we know the size — the download is finished.
            // Report it as fully done so byte-weighted aggregation in TorrentDtoMapper
            // counts it correctly toward the torrent's overall progress.
            return (0, size, size);
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// Drop the cached size for a download. Call from the delete path so the cache
    /// doesn't accumulate entries for torrents the user has removed.
    /// </summary>
    public static void ForgetDownloadSize(Guid downloadId)
    {
        KnownDownloadSize.TryRemove(downloadId, out _);
    }

    private static Boolean IsDeletedFromProvider(Torrent torrent)
    {
        return String.Equals(torrent.RdStatusRaw, "deleted", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A download row is eligible to (re)start when it is queued, not yet started,
    /// and not terminally completed. Error is deliberately NOT part of this gate:
    /// the retry path (Reset + UpdateError write-back) leaves the transient error on
    /// the row so the UI can show "Retrying (N/M): error" while the row waits to
    /// restart. Requiring Error == null here wedged those rows forever — Error set,
    /// DownloadStarted/DownloadFinished null, never picked up again (observed in
    /// production after a spurious aria2 not-found error). Every PERMANENT failure
    /// path sets Completed alongside Error, so Completed == null is the real gate;
    /// the transient error itself is cleared when the download actually starts.
    /// </summary>
    public static Boolean CanStartDownload(Download download)
    {
        return download.Completed == null && download.DownloadQueued != null && download.DownloadStarted == null;
    }

    public async Task Initialize()
    {
        Log("Initializing TorrentRunner");

        var settingsCopy = JsonSerializer.Deserialize<DbSettings>(JsonSerializer.Serialize(Settings.Get));

        if (settingsCopy != null)
        {
            settingsCopy.Provider.ApiKey = "*****";
            settingsCopy.DownloadClient.Aria2cSecret = "*****";
            settingsCopy.DownloadClient.DownloadStationPassword = "*****";

            Log(JsonSerializer.Serialize(settingsCopy));
        }

        // When starting up reset any pending downloads or unpackings so that they are restarted.
        var allTorrents = await torrents.Get();

        allTorrents = allTorrents.Where(m => m.Completed == null).ToList();

        Log($"Found {allTorrents.Count} not completed torrents");

        foreach (var torrent in allTorrents)
        {
            foreach (var download in torrent.Downloads)
            {
                if (download.DownloadQueued != null && download.DownloadStarted != null && download.DownloadFinished == null && download.Error == null)
                {
                    Log("Resetting download status", download, torrent);

                    await downloads.UpdateDownloadStarted(download.DownloadId, null);
                }

                if (download.UnpackingQueued != null && download.UnpackingStarted != null && download.UnpackingFinished == null && download.Error == null)
                {
                    Log("Resetting unpack status", download, torrent);

                    await downloads.UpdateUnpackingStarted(download.DownloadId, null);
                }
            }
        }

        Log("TorrentRunner Initialized");
    }

    public async Task Tick()
    {
        if (String.IsNullOrWhiteSpace(Settings.Get.Provider.ApiKey))
        {
            Log($"No RealDebridApiKey set in settings");

            return;
        }

        var settingDownloadLimit = Settings.Get.General.DownloadLimit;

        if (settingDownloadLimit < 1)
        {
            settingDownloadLimit = 1;
        }

        var settingUnpackLimit = Settings.Get.General.UnpackLimit;

        if (settingUnpackLimit < 0)
        {
            settingUnpackLimit = 0;
        }

        var settingDownloadPath = Settings.Get.DownloadClient.DownloadPath;

        if (String.IsNullOrWhiteSpace(settingDownloadPath))
        {
            logger.LogError("No DownloadPath set in settings");

            return;
        }

        var sw = new Stopwatch();
        sw.Start();

        var currentNextAllowedAt = coordinator.GetMaxNextAllowedAt();

        if (currentNextAllowedAt != _lastNextAllowedAt)
        {
            if (currentNextAllowedAt == null || currentNextAllowedAt <= DateTimeOffset.UtcNow)
            {
                if (_lastNextAllowedAt > DateTimeOffset.UtcNow)
                {
                    Log("Rate-limit cooldown expired, resuming dequeuing");

                    await remoteService.UpdateRateLimitStatus(new()
                    {
                        NextDequeueTime = null,
                        SecondsRemaining = 0
                    });
                }
            }

            _lastNextAllowedAt = currentNextAllowedAt;
        }

        if (!ActiveDownloadClients.IsEmpty || !ActiveUnpackClients.IsEmpty)
        {
            Log($"TorrentRunner Tick Start, {ActiveDownloadClients.Count} active downloads, {ActiveUnpackClients.Count} active unpacks");
        }

        // Aria2 status polling lives in Aria2StatusPoller (a dedicated BackgroundService)
        // rather than inline in this method. Previously a single slow TellAllAsync RPC
        // (observed: 13-40 s under high-throughput downloads) blocked the entire Tick
        // body, which in turn delayed dequeue, retry, completion, and deletion work and
        // made the dashboard appear frozen. The poller has its own loop, its own
        // per-call timeout, and back-off on consecutive failures; Tick now just reads
        // the live BytesDone/BytesTotal/Speed that the poller wrote into
        // ActiveDownloadClients.

        if (ActiveDownloadClients.Any(m => m.Value.Type == Data.Enums.DownloadClient.DownloadStation))
        {
            Log("Updating DownloadStation status");

            foreach (var activeDownload in ActiveDownloadClients)
            {
                if (activeDownload.Value.Downloader is DownloadStationDownloader downloadStationDownloader)
                {
                    await downloadStationDownloader.Update();
                }
            }
        }

        // Check if any torrents are finished downloading to the host, remove them from the active download list.
        var completedActiveDownloads = ActiveDownloadClients.Where(m => m.Value.Finished).ToList();

        if (completedActiveDownloads.Count > 0)
        {
            Log($"Processing {completedActiveDownloads.Count} completed downloads");

            foreach (var (downloadId, downloadClient) in completedActiveDownloads)
            {
                var download = await downloads.GetById(downloadId);

                if (download == null)
                {
                    ActiveDownloadClients.TryRemove(downloadId, out _);

                    Log($"Download with ID {downloadId} not found! Removed from download queue");

                    continue;
                }

                Log("Processing download", download, download.Torrent);

                if (!String.IsNullOrWhiteSpace(downloadClient.Error))
                {
                    // Retry the download if an error is encountered.
                    LogError($"Download reported an error: {downloadClient.Error}", download, download.Torrent);

                    Log($"Download retry count {download.RetryCount}/{download.Torrent!.DownloadRetryAttempts}, torrent retry count {download.Torrent.RetryCount}/{download.Torrent.TorrentRetryAttempts}",
                        download,
                        download.Torrent);

                    if (download.RetryCount < download.Torrent.DownloadRetryAttempts)
                    {
                        Log($"Retrying download", download, download.Torrent);

                        await downloads.Reset(downloadId);
                        await downloads.UpdateRetryCount(downloadId, download.RetryCount + 1);
                        // Surface the transient error to the UI immediately. Reset() clears
                        // Error along with the rest of the run state, so we write it back here.
                        // The combination (Error set, Completed null, RetryCount > 0) signals
                        // "actively retrying" to the status pipe — distinct from the final
                        // failure state below (Error set + Completed set). Without this, the
                        // UI saw a healthy-looking in-flight download until all retries
                        // exhausted, which could be a couple of minutes of misleading "0%".
                        await downloads.UpdateError(downloadId, downloadClient.Error);
                    }
                    else
                    {
                        Log($"Not retrying download", download, download.Torrent);

                        await downloads.UpdateError(downloadId, downloadClient.Error);
                        await downloads.UpdateCompleted(downloadId, DateTimeOffset.UtcNow);
                    }
                }
                else
                {
                    Log($"Download finished successfully", download, download.Torrent);

                    // Clear any leftover transient retry error from a previous failed attempt.
                    await downloads.UpdateError(downloadId, null);
                    await downloads.UpdateDownloadFinished(downloadId, DateTimeOffset.UtcNow);
                    await downloads.UpdateUnpackingQueued(downloadId, DateTimeOffset.UtcNow);
                }

                ActiveDownloadClients.TryRemove(downloadId, out _);

                Log($"Removed from ActiveDownloadClients", download, download.Torrent);
            }
        }

        // Check if any torrents are finished unpacking, remove them from the active unpack list.
        var completedUnpacks = ActiveUnpackClients.Where(m => m.Value.Finished).ToList();

        if (completedUnpacks.Count > 0)
        {
            Log($"Processing {completedUnpacks.Count} completed unpacks");

            foreach (var (downloadId, unpackClient) in completedUnpacks)
            {
                var download = await downloads.GetById(downloadId);

                if (download == null)
                {
                    ActiveUnpackClients.TryRemove(downloadId, out _);

                    Log($"Download with ID {downloadId} not found! Removed from unpack queue");

                    continue;
                }

                if (unpackClient.Error != null)
                {
                    Log($"Unpack reported an error: {unpackClient.Error}", download, download.Torrent);

                    await downloads.UpdateError(downloadId, unpackClient.Error);
                }
                else
                {
                    Log($"Unpack finished successfully", download, download.Torrent);

                    await downloads.UpdateUnpackingFinished(downloadId, DateTimeOffset.UtcNow);
                }

                await downloads.UpdateCompleted(downloadId, DateTimeOffset.UtcNow);

                ActiveUnpackClients.TryRemove(downloadId, out _);

                Log($"Removed from ActiveUnpackClients", download, download.Torrent);
            }
        }

        var allTorrents = await torrents.Get();
        var downloadsById = allTorrents.SelectMany(m => m.Downloads).ToDictionary(m => m.DownloadId, m => m);

        // Check for deleted torrents that are stuck in the ActiveDownloads or ActiveUnpacks
        foreach (var activeDownload in ActiveDownloadClients)
        {
            if (!downloadsById.ContainsKey(activeDownload.Key))
            {
                await activeDownload.Value.Cancel();
                ActiveDownloadClients.TryRemove(activeDownload.Key, out _);

                break;
            }
        }

        foreach (var activeUnpacks in ActiveUnpackClients)
        {
            if (!downloadsById.ContainsKey(activeUnpacks.Key))
            {
                activeUnpacks.Value.Cancel();
                ActiveUnpackClients.TryRemove(activeUnpacks.Key, out _);

                break;
            }
        }

        // Process torrent retries
        foreach (var torrent in allTorrents.Where(m => m.Retry != null))
        {
            try
            {
                Log($"Retrying torrent {torrent.RetryCount}/{torrent.TorrentRetryAttempts}", torrent);

                if (torrent.RetryCount > torrent.TorrentRetryAttempts)
                {
                    await torrents.UpdateRetry(torrent.TorrentId, null, torrent.RetryCount);
                    Log($"Torrent reach max retry count");

                    continue;
                }

                await torrents.RetryTorrent(torrent.TorrentId, torrent.RetryCount);
            }
            catch (Exception ex)
            {
                await torrents.UpdateRetry(torrent.TorrentId, null, torrent.RetryCount);
                await torrents.UpdateError(torrent.TorrentId, ex.Message);
            }
        }

        // Process torrent errors
        foreach (var torrent in allTorrents.Where(m => m.Error != null && m.DeleteOnError > 0))
        {
            if (torrent.Completed == null)
            {
                continue;
            }

            if (torrent.Completed.Value.AddMinutes(torrent.DeleteOnError) > DateTime.UtcNow)
            {
                continue;
            }

            Log($"Removing torrent because it has been {torrent.DeleteOnError} minutes in the error state", torrent);

            await torrents.Delete(torrent.TorrentId, true, true, true);
        }

        // Process torrent lifetime
        foreach (var torrent in allTorrents.Where(m => m.Downloads.Count == 0 && m.Completed == null && m.Lifetime > 0))
        {
            if (torrent.Added.AddMinutes(torrent.Lifetime) > DateTime.UtcNow)
            {
                continue;
            }

            Log($"Torrent has reached its {torrent.Lifetime} minutes lifetime, marking as error", torrent);

            await torrents.UpdateRetry(torrent.TorrentId, null, torrent.TorrentRetryAttempts);
            await torrents.UpdateComplete(torrent.TorrentId, $"Torrent lifetime of {torrent.Lifetime} minutes reached", DateTimeOffset.UtcNow, false);
        }

        // Process torrents in DebridQueue
        var torrentsToAddToProvider = allTorrents.Where(m => m.RdId == null && m.RdAdded == null && m.FileOrMagnet != null && m.RdStatus == TorrentStatus.Queued)
                                                 .ToList();

        if (torrentsToAddToProvider.Count != 0)
        {
            var nextAllowedAt = coordinator.GetMaxNextAllowedAt();

            if (nextAllowedAt > DateTimeOffset.UtcNow)
            {
                logger.LogDebug($"Dequeuing torrents is paused until {nextAllowedAt}, {nextAllowedAt - DateTimeOffset.Now} remaining");
            }
            else
            {
                var downloadingTorrentsCount = allTorrents.Count(m => m.RdStatus is not (TorrentStatus.Queued or TorrentStatus.Finished or TorrentStatus.Error));

                var maxParallelDownloads = Settings.Get.Provider.MaxParallelDownloads;

                logger.LogDebug("Currently downloading {downloadingTorrentCount}/{maxParallelDownloads} torrents, {queuedCount} queued.",
                                downloadingTorrentsCount,
                                maxParallelDownloads,
                                torrentsToAddToProvider.Count);

                var dequeueCount = maxParallelDownloads == 0 ? torrentsToAddToProvider.Count : maxParallelDownloads - downloadingTorrentsCount;

                foreach (var torrent in torrentsToAddToProvider.Take(dequeueCount))
                {
                    try
                    {
                        await torrents.DequeueFromDebridQueue(torrent);
                    }
                    catch (RateLimitException ex)
                    {
                        await SetRateLimit(ex.RetryAfter, ex.Message);

                        break;
                    }
                    catch (Exception ex)
                    {
                        await torrents.UpdateComplete(torrent.TorrentId, $"Could not add to provider: {ex.Message}", DateTimeOffset.Now, true);
                        logger.LogWarning(ex, "Could not dequeue torrent {torrentId}", torrent.TorrentId);
                    }
                }
            }
        }

        allTorrents = await torrents.Get();

        var completeTorrents = allTorrents.Where(m => m.Completed != null);
        var torrentsToDelete = completeTorrents.Where(m => DateTimeOffset.UtcNow >= m.Completed?.AddMinutes(m.FinishedActionDelay) && m.Error == null);

        // Parse the Categories setting once per tick; the inner loop just does case-insensitive
        // name lookups against the resulting list. Avoids re-parsing the same JSON N times.
        var categoriesForTick = CategoryParser.Parse(Settings.Get.General.Categories);

        foreach (var torrent in torrentsToDelete)
        {
            // Per-category override: General:Categories[*] can independently opt the torrent's
            // category into removing from the rdt-client dashboard, the debrid provider, and/or
            // the local files on disk. If any flag is set we bypass the FinishedAction enum
            // (which only encodes 4 fixed combinations) and call Delete() with the exact flags.
            // The resolver also applies the Symlink-keeps-provider safety; see its tests.
            var category = String.IsNullOrEmpty(torrent.Category)
                ? null
                : categoriesForTick.FirstOrDefault(c => String.Equals(c.Name, torrent.Category, StringComparison.OrdinalIgnoreCase));
            var decision = CategoryAutoRemoveResolver.Resolve(category, torrent.DownloadClient);

            if (decision.HasValue)
            {
                var d = decision.Value;

                if (d.SymlinkSuppressedProvider)
                {
                    Log($"Symlink client: skipping provider removal for category '{torrent.Category}' to keep the symlink target alive", torrent);
                }

                Log($"Per-category cleanup for '{torrent.Category}': dashboard={d.RemoveDashboard}, provider={d.RemoveProvider}, localFiles={d.RemoveLocalFiles}", torrent);
                await torrents.Delete(torrent.TorrentId, d.RemoveDashboard, d.RemoveProvider, d.RemoveLocalFiles);

                continue;
            }

            if (torrent.DownloadClient == Data.Enums.DownloadClient.Symlink)
            {
                switch (torrent.FinishedAction)
                {
                    case TorrentFinishedAction.RemoveAllTorrents:
                        Log($"Force setting FinishedAction to RemoveClient as download client is Symlink and FinishedAction is RemoveAllTorrents", torrent);
                        torrent.FinishedAction = TorrentFinishedAction.RemoveClient;

                        break;
                    case TorrentFinishedAction.RemoveRealDebrid:
                        Log($"Force setting FinishedAction to TorrentFinishedAction.None as download client is Symlink and FinishedAction is RemoveRealDebrid", torrent);
                        torrent.FinishedAction = TorrentFinishedAction.None;

                        break;
                }
            }

            switch (torrent.FinishedAction)
            {
                case TorrentFinishedAction.RemoveAllTorrents:
                    Log($"Removing torrents from debrid provider and RDT-Client, no files", torrent);
                    await torrents.Delete(torrent.TorrentId, true, true, false);

                    break;
                case TorrentFinishedAction.RemoveRealDebrid:
                    Log($"Removing torrents from debrid provider, no files", torrent);
                    await torrents.Delete(torrent.TorrentId, false, true, false);

                    break;
                case TorrentFinishedAction.RemoveClient:
                    Log($"Removing torrents from client, no files", torrent);
                    await torrents.Delete(torrent.TorrentId, true, false, false);

                    break;
                case TorrentFinishedAction.None:
                    Log($"Not removing torrents or files", torrent);

                    break;
                default:
                    Log($"Invalid torrent FinishedAction {torrent.FinishedAction}", torrent);

                    break;
            }
        }

        var incompleteTorrents = allTorrents.Where(m => m.Completed == null).ToList();

        if (incompleteTorrents.Count > 0)
        {
            Log($"Processing {allTorrents.Count} torrents");
        }

        foreach (var torrent in incompleteTorrents)
        {
            try
            {
                // Check if there are any downloads that are queued and can be started.
                var queuedDownloads = torrent.Downloads
                                             .Where(CanStartDownload)
                                             .OrderBy(m => m.DownloadQueued)
                                             .ToList();

                Log($"Currently {queuedDownloads.Count} queued downloads and {ActiveDownloadClients.Count} total active downloads", torrent);

                foreach (var download in queuedDownloads)
                {
                    Log($"Processing to download", download, torrent);

                    if (ActiveDownloadClients.Count >= settingDownloadLimit && torrent.DownloadClient != Data.Enums.DownloadClient.Symlink)
                    {
                        Log($"Not starting download because there are already the max number of downloads active", download, torrent);

                        return;
                    }

                    if (IsPausedForLowDiskSpace && torrent.DownloadClient == Data.Enums.DownloadClient.Bezzad)
                    {
                        logger.LogInformation($"Not starting Bezzad download because of low disk space {download.ToLog()} {torrent.ToLog()}");

                        return;
                    }

                    if (ActiveDownloadClients.ContainsKey(download.DownloadId))
                    {
                        Log($"Not starting download because this download is already active", download, torrent);

                        return;
                    }

                    try
                    {
                        if (download.Link == null)
                        {
                            Log($"Unrestricting links", download, torrent);

                            var downloadLink = await torrents.UnrestrictLink(download.DownloadId);
                            download.Link = downloadLink;

                            if (download.FileName == null)
                            {
                                var fileName = await torrents.RetrieveFileName(download.DownloadId);
                                download.FileName = fileName;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Cannot unrestrict link: {ex.Message}", ex.Message);

                        await downloads.UpdateError(download.DownloadId, ex.Message);
                        await downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);
                        download.Error = ex.Message;
                        download.Completed = DateTimeOffset.UtcNow;

                        return;
                    }

                    Log($"Marking download as started", download, torrent);

                    if (download.Error != null)
                    {
                        // Transient-retry row: the retry path wrote the previous attempt's
                        // error back after Reset() so the UI could surface it while queued.
                        // The retry is actually starting now, so the row goes back to a
                        // clean in-flight state.
                        await downloads.UpdateError(download.DownloadId, null);
                        download.Error = null;
                    }

                    download.DownloadStarted = DateTime.UtcNow;
                    await downloads.UpdateDownloadStarted(download.DownloadId, download.DownloadStarted);

                    var downloadPath = settingDownloadPath;

                    if (!String.IsNullOrWhiteSpace(torrent.Category))
                    {
                        downloadPath = Path.Combine(downloadPath, torrent.Category);
                    }

                    Log($"Setting download path to {downloadPath}", download, torrent);

                    // Start the download process
                    var downloadClient = new DownloadClient(download, torrent, downloadPath, torrent.Category);

                    if (ActiveDownloadClients.TryAdd(download.DownloadId, downloadClient))
                    {
                        Log($"Starting download", download, torrent);

                        try
                        {
                            var remoteId = await downloadClient.Start();

                            if (String.IsNullOrWhiteSpace(remoteId))
                            {
                                throw new($"No remote ID received from download client");
                            }

                            Log($"Received ID {remoteId}", download, torrent);

                            if (download.RemoteId != remoteId)
                            {
                                await downloads.UpdateRemoteId(download.DownloadId, remoteId);
                            }

                            if (IsPausedForLowDiskSpace && downloadClient.Type == Data.Enums.DownloadClient.Bezzad)
                            {
                                logger.LogInformation($"Pausing new Bezzad download due to low disk space {download.ToLog()} {torrent.ToLog()}");
                                await downloadClient.Pause();
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError($"Unable to start download: {ex.Message}", download, torrent);

                            continue;
                        }

                        Log($"Started download", download, torrent);
                    }
                }

                // Check if there are any unpacks that are queued and can be started.
                var queuedUnpacks = torrent.Downloads
                                           .Where(m => m.Completed == null && m.UnpackingQueued != null && m.UnpackingStarted == null && m.Error == null)
                                           .OrderBy(m => m.DownloadQueued)
                                           .ToList();

                foreach (var download in queuedUnpacks)
                {
                    Log($"Starting unpack", download, torrent);

                    if (download.Link == null)
                    {
                        Log($"No download link found", download, torrent);

                        await downloads.UpdateError(download.DownloadId, "Download Link cannot be null");
                        await downloads.UpdateCompleted(download.DownloadId, DateTimeOffset.UtcNow);

                        continue;
                    }

                    // Check if the unpacking process is even needed
                    var uri = new Uri(download.Link);

                    var extension = Path.GetExtension(download.FileName);

                    if ((extension != ".rar" && extension != ".zip") ||
                        torrent.DownloadClient == Data.Enums.DownloadClient.Symlink ||
                        settingUnpackLimit == 0)
                    {
                        Log($"No need to unpack, setting it as unpacked", download, torrent);

                        download.UnpackingStarted = DateTimeOffset.UtcNow;
                        download.UnpackingFinished = DateTimeOffset.UtcNow;
                        download.Completed = DateTimeOffset.UtcNow;

                        await downloads.UpdateUnpackingStarted(download.DownloadId, download.UnpackingStarted);
                        await downloads.UpdateUnpackingFinished(download.DownloadId, download.UnpackingFinished);
                        await downloads.UpdateCompleted(download.DownloadId, download.Completed);

                        continue;
                    }

                    // Check if we have reached the download limit, if so queue the download, but don't start it.
                    if (ActiveUnpackClients.Count >= settingUnpackLimit)
                    {
                        Log($"Not starting unpack because there are already the max number of unpacks active", download, torrent);

                        continue;
                    }

                    if (ActiveUnpackClients.ContainsKey(download.DownloadId))
                    {
                        Log($"Not starting unpack because this download is already active", download, torrent);

                        continue;
                    }

                    download.UnpackingStarted = DateTimeOffset.UtcNow;
                    await downloads.UpdateUnpackingStarted(download.DownloadId, download.UnpackingStarted);

                    var downloadPath = settingDownloadPath;

                    if (!String.IsNullOrWhiteSpace(torrent.Category))
                    {
                        downloadPath = Path.Combine(downloadPath, torrent.Category);
                    }

                    Log($"Setting unpack path to {downloadPath}", download, torrent);

                    // Start the unpacking process
                    var unpackClient = new UnpackClient(download, downloadPath);

                    if (ActiveUnpackClients.TryAdd(download.DownloadId, unpackClient))
                    {
                        Log($"Starting unpack", download, torrent);

                        unpackClient.Start();
                    }
                }

                Log("Processing", torrent);

                // If torrent is erroring out on the debrid side.
                if (torrent.RdStatus == TorrentStatus.Error || IsDeletedFromProvider(torrent))
                {
                    Log($"Torrent reported an error: {torrent.RdStatusRaw}", torrent);
                    Log($"Torrent retry count {torrent.RetryCount}/{torrent.TorrentRetryAttempts}", torrent);

                    Log($"Received provider error: {torrent.RdStatusRaw}, not processing further", torrent);

                    await torrents.UpdateComplete(torrent.TorrentId, $"Debrid error: {torrent.RdStatusRaw}.", DateTimeOffset.UtcNow, true);

                    continue;
                }

                // Debrid provider is waiting for file selection, select which files to download.
                if ((torrent.RdStatus == TorrentStatus.WaitingForFileSelection || torrent.RdStatus == TorrentStatus.Finished) &&
                    torrent.FilesSelected == null &&
                    torrent.Downloads.Count == 0)
                {
                    Log($"Selecting files", torrent);

                    await torrents.SelectFiles(torrent.TorrentId);

                    await torrents.UpdateFilesSelected(torrent.TorrentId, DateTime.UtcNow);
                }

                // Debrid provider finished downloading the torrent, process the file to host.
                if (torrent.RdStatus == TorrentStatus.Finished)
                {
                    // The files are selected but there are no downloads yet, check if debrid provider has generated links yet.
                    if (torrent.Downloads.Count == 0 && torrent.FilesSelected != null)
                    {
                        Log($"Creating downloads", torrent);

                        if (torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadAll)
                        {
                            await torrents.CreateDownloads(torrent.TorrentId);
                        }
                    }
                }

                // Check if torrent is complete, or if we don't want to download any files to the host.
                if (torrent.Downloads.Count > 0 ||
                    (torrent.RdStatus == TorrentStatus.Finished && torrent.HostDownloadAction == TorrentHostDownloadAction.DownloadNone))
                {
                    var completeCount = torrent.Downloads.Count(m => m.Completed != null);

                    var completePerc = 0;

                    var totalDownloadBytes = torrent.Downloads.Sum(m => GetStats(m.DownloadId).BytesTotal);
                    var totalDoneBytes = torrent.Downloads.Sum(m => GetStats(m.DownloadId).BytesDone);

                    if (totalDownloadBytes > 0)
                    {
                        completePerc = (Int32)(((Double)totalDoneBytes / totalDownloadBytes) * 100);
                    }

                    if (completeCount == torrent.Downloads.Count)
                    {
                        Log($"All downloads complete, marking torrent as complete", torrent);

                        await torrents.UpdateComplete(torrent.TorrentId, null, DateTimeOffset.UtcNow, true);

                        try
                        {
                            await torrents.RunTorrentComplete(torrent.TorrentId);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex.Message, "Unable to run post process: {Message}", ex.Message);
                        }
                    }
                    else
                    {
                        Log($"Waiting for downloads to complete. {completeCount}/{torrent.Downloads.Count} complete ({completePerc}%)", torrent);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex.Message, "Torrent processing result in an unexpected exception: {Message}", ex.Message);
                await torrents.UpdateComplete(torrent.TorrentId, $"Runner error: {ex.Message}", DateTimeOffset.UtcNow, true);
            }
        }

        sw.Stop();

        if (sw.ElapsedMilliseconds > 1000)
        {
            Log($"TorrentRunner Tick End (took {sw.ElapsedMilliseconds}ms)");
        }
    }

    public async Task SetRateLimit(TimeSpan retryAfter, String message)
    {
        coordinator.UpdateCooldown("General", retryAfter);
        var nextDequeueTime = coordinator.GetMaxNextAllowedAt();
        var now = DateTimeOffset.UtcNow;
        var secondsRemaining = nextDequeueTime.HasValue ? (nextDequeueTime.Value - now).TotalSeconds : 0;

        Log($"Rate-limit reached, pausing dequeuing for {retryAfter.TotalMinutes} minutes (until {nextDequeueTime}): {message}");

        _lastNextAllowedAt = nextDequeueTime;

        await remoteService.UpdateRateLimitStatus(new()
        {
            NextDequeueTime = nextDequeueTime,
            SecondsRemaining = secondsRemaining
        });
    }

    private void Log(String message, Download? download, Torrent? torrent)
    {
        if (download != null)
        {
            message = $"{message} {download.ToLog()}";
        }

        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }

    private void Log(String message, Torrent? torrent = null)
    {
        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }

    private void LogError(String message, Download? download, Torrent? torrent)
    {
        if (download != null)
        {
            message = $"{message} {download.ToLog()}";
        }

        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogError(message);
    }
}
