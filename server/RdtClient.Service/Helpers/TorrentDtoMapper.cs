using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;

namespace RdtClient.Service.Helpers;

public static class TorrentDtoMapper
{
    public static TorrentDto ToListDto(Torrent torrent, Func<Guid, (Int64 Speed, Int64 BytesTotal, Int64 BytesDone)> getDownloadStats)
    {
        // includeDownloads = true so the cold REST GET that the dashboard issues on load
        // carries the same shape as the SignalR push. Without this, the frontend would
        // render with downloads:[] for ~1 s after every page load / reconnect, which makes
        // the progress bar fall back to rdProgress (often 100% for cached torrents).
        return ToDto(torrent, getDownloadStats, includeDownloads: true, includeFiles: false, includeFileOrMagnet: false);
    }

    public static TorrentDto ToUpdateDto(Torrent torrent, Func<Guid, (Int64 Speed, Int64 BytesTotal, Int64 BytesDone)> getDownloadStats)
    {
        return ToDto(torrent, getDownloadStats, includeDownloads: true, includeFiles: false, includeFileOrMagnet: false);
    }

    public static TorrentDto ToDetailDto(Torrent torrent, Func<Guid, (Int64 Speed, Int64 BytesTotal, Int64 BytesDone)> getDownloadStats)
    {
        return ToDto(torrent, getDownloadStats, includeDownloads: true, includeFiles: true, includeFileOrMagnet: true);
    }

    private static TorrentDto ToDto(Torrent torrent,
                                    Func<Guid, (Int64 Speed, Int64 BytesTotal, Int64 BytesDone)> getDownloadStats,
                                    Boolean includeDownloads,
                                    Boolean includeFiles,
                                    Boolean includeFileOrMagnet)
    {
        var downloads = includeDownloads ? torrent.Downloads.Select(download => ToDto(download, getDownloadStats)).ToList() : [];

        var (statusText, localProgress) = GetStatusTextAndProgress(torrent, getDownloadStats);

        return new()
        {
            TorrentId = torrent.TorrentId,
            Hash = torrent.Hash,
            Category = torrent.Category,
            DownloadAction = torrent.DownloadAction,
            FinishedAction = torrent.FinishedAction,
            FinishedActionDelay = torrent.FinishedActionDelay,
            HostDownloadAction = torrent.HostDownloadAction,
            DownloadMinSize = torrent.DownloadMinSize,
            IncludeRegex = torrent.IncludeRegex,
            ExcludeRegex = torrent.ExcludeRegex,
            DownloadManualFiles = torrent.DownloadManualFiles,
            DownloadClient = torrent.DownloadClient,
            Added = torrent.Added,
            FilesSelected = torrent.FilesSelected,
            Completed = torrent.Completed,
            Type = torrent.Type,
            FileOrMagnet = includeFileOrMagnet ? torrent.FileOrMagnet : null,
            IsFile = torrent.IsFile,
            Priority = torrent.Priority,
            RetryCount = torrent.RetryCount,
            DownloadRetryAttempts = torrent.DownloadRetryAttempts,
            TorrentRetryAttempts = torrent.TorrentRetryAttempts,
            DeleteOnError = torrent.DeleteOnError,
            Lifetime = torrent.Lifetime,
            Error = torrent.Error,
            RdId = torrent.RdId,
            RdName = torrent.RdName,
            RdSize = torrent.RdSize,
            RdHost = torrent.RdHost,
            RdSplit = torrent.RdSplit,
            RdProgress = torrent.RdProgress,
            RdStatus = torrent.RdStatus,
            RdStatusRaw = torrent.RdStatusRaw,
            RdAdded = torrent.RdAdded,
            RdEnded = torrent.RdEnded,
            RdSpeed = torrent.RdSpeed,
            RdSeeders = torrent.RdSeeders,
            LocalProgress = localProgress,
            StatusText = statusText,
            FilesCount = torrent.Files.Count,
            DownloadsCount = torrent.Downloads.Count,
            Files = includeFiles ? torrent.Files : [],
            Downloads = downloads
        };
    }

    private static DownloadDto ToDto(Download download, Func<Guid, (Int64 Speed, Int64 BytesTotal, Int64 BytesDone)> getDownloadStats)
    {
        var (speed, bytesTotal, bytesDone) = getDownloadStats(download.DownloadId);

        return new()
        {
            DownloadId = download.DownloadId,
            TorrentId = download.TorrentId,
            Path = download.Path,
            Link = download.Link,
            Added = download.Added,
            DownloadQueued = download.DownloadQueued,
            DownloadStarted = download.DownloadStarted,
            DownloadFinished = download.DownloadFinished,
            UnpackingQueued = download.UnpackingQueued,
            UnpackingStarted = download.UnpackingStarted,
            UnpackingFinished = download.UnpackingFinished,
            Completed = download.Completed,
            RetryCount = download.RetryCount,
            Error = download.Error,
            BytesTotal = bytesTotal,
            BytesDone = bytesDone,
            Speed = speed
        };
    }

    /// <summary>
    /// Computes the user-facing status string and the local-download progress percentage
    /// from a single pass over <see cref="Torrent.Downloads"/>. Both outputs share the same
    /// per-download view of "in-flight" vs "finished" so the dashboard's status pill and
    /// progress bar can never disagree.
    /// </summary>
    private static (String StatusText, Int64? LocalProgress) GetStatusTextAndProgress(
        Torrent torrent,
        Func<Guid, (Int64 Speed, Int64 BytesTotal, Int64 BytesDone)> getDownloadStats)
    {
        if (!String.IsNullOrWhiteSpace(torrent.Error))
        {
            return (torrent.Error, null);
        }

        if (torrent.Downloads.Count > 0)
        {
            var allFinished = true;
            var downloadingCount = 0;
            var downloadedCount = 0;
            var unpackingCount = 0;
            var unpackedCount = 0;
            var queuedForUnpackingCount = 0;
            var queuedForDownloadingCount = 0;

            // Byte-weighted aggregation across the whole torrent. A finished download
            // contributes its full size to both numerator and denominator; an in-flight
            // download contributes its live bytesDone/bytesTotal; a queued download
            // contributes its size (when known) to the denominator only. TorrentRunner's
            // KnownDownloadSize cache makes the size visible even after a download has
            // been removed from ActiveDownloadClients on completion, so the math doesn't
            // collapse when one sibling has finished and another is still pulling.
            Int64 totalBytesDone = 0;
            Int64 totalBytesTotal = 0;
            var anyKnownSize = false;
            var anyMissingSize = false;
            var fileCountUnits = 0d;

            foreach (var download in torrent.Downloads)
            {
                if (download.Completed == null)
                {
                    allFinished = false;
                }

                var (_, bytesTotal, bytesDone) = getDownloadStats(download.DownloadId);

                if (download.DownloadFinished != null)
                {
                    downloadedCount += 1;
                    fileCountUnits += 1d;
                    if (bytesTotal > 0)
                    {
                        totalBytesDone += bytesTotal;
                        totalBytesTotal += bytesTotal;
                        anyKnownSize = true;
                    }
                    else
                    {
                        anyMissingSize = true;
                    }
                }
                else if (download.DownloadStarted != null)
                {
                    // In-flight. We deliberately do NOT gate on bytesDone > 0 — a freshly
                    // started download briefly returns (0, 0, 0) from the live tracker
                    // between aria2 chunks, and that 0-byte window used to flip the
                    // status text into "Queued for downloading".
                    downloadingCount += 1;
                    if (bytesTotal > 0)
                    {
                        totalBytesDone += Math.Min(bytesDone, bytesTotal);
                        totalBytesTotal += bytesTotal;
                        anyKnownSize = true;
                        fileCountUnits += Math.Clamp((Double)bytesDone / bytesTotal, 0d, 1d);
                    }
                    else
                    {
                        anyMissingSize = true;
                    }
                }
                else
                {
                    // Queued — contribute to denominator only when we have a size.
                    if (bytesTotal > 0)
                    {
                        totalBytesTotal += bytesTotal;
                        anyKnownSize = true;
                    }
                    else
                    {
                        anyMissingSize = true;
                    }
                }

                if (download.UnpackingFinished != null)
                {
                    unpackedCount += 1;
                }
                else if (download.UnpackingStarted != null)
                {
                    unpackingCount += 1;
                }

                if (download.UnpackingQueued != null && download.UnpackingStarted == null)
                {
                    queuedForUnpackingCount += 1;
                }

                if (download.DownloadStarted == null && download.DownloadFinished == null)
                {
                    queuedForDownloadingCount += 1;
                }
            }

            // Pick the metric that has stable inputs. Byte-weighted is preferred — it's
            // what users intuitively expect ("the .nfo shouldn't count as 50% of a 24 GB
            // torrent"). Fall back to file-count when we have no size info anywhere, or
            // when at least one download has unknown size — file-count is monotone and
            // doesn't lie during the (0,0,0) tracker window.
            Int64 localProgress;
            if (anyKnownSize && !anyMissingSize && totalBytesTotal > 0)
            {
                localProgress = (Int64)Math.Round(Math.Clamp((Double)totalBytesDone / totalBytesTotal * 100d, 0d, 100d));
            }
            else
            {
                localProgress = (Int64)Math.Round(Math.Clamp(fileCountUnits / torrent.Downloads.Count * 100d, 0d, 100d));
            }

            if (allFinished)
            {
                return ("Finished", localProgress);
            }

            if (downloadingCount > 0)
            {
                // Status text reports the same overall byte-weighted percent the bar
                // shows. Previously this string carried the per-current-file percent,
                // which disagreed with the bar whenever there were multiple files.
                return ($"Downloading file {downloadingCount + downloadedCount}/{torrent.Downloads.Count} ({localProgress}%)", localProgress);
            }

            if (unpackingCount > 0)
            {
                return ($"Extracting file {unpackingCount + unpackedCount}/{torrent.Downloads.Count} ({localProgress}%)", localProgress);
            }

            if (queuedForUnpackingCount > 0)
            {
                return ("Queued for unpacking", localProgress);
            }

            if (queuedForDownloadingCount > 0)
            {
                return ("Queued for downloading", localProgress);
            }

            if (unpackedCount > 0)
            {
                return ("Files unpacked", localProgress);
            }

            if (downloadedCount > 0)
            {
                return ("Files downloaded to host", localProgress);
            }
        }

        if (torrent.Completed != null)
        {
            return ("Finished", null);
        }

        var providerStatusText = torrent.RdStatus switch
        {
            TorrentStatus.Queued => "Not Yet Added to Provider",
            TorrentStatus.Downloading when torrent.RdSeeders < 1 && torrent.Type != DownloadType.Nzb => "Torrent stalled",
            TorrentStatus.Downloading => $"Torrent downloading ({torrent.RdProgress}%)",
            TorrentStatus.Processing => "Torrent processing",
            TorrentStatus.WaitingForFileSelection => "Torrent waiting for file selection",
            TorrentStatus.Error => $"Torrent error: {torrent.RdStatusRaw}",
            TorrentStatus.Finished => "Torrent finished, waiting for download links",
            TorrentStatus.Uploading => "Torrent uploading",
            _ => "Unknown status"
        };

        // No local downloads yet — let the frontend fall back to rdProgress.
        return (providerStatusText, null);
    }
}
