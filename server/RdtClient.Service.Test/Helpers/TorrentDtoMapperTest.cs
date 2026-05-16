using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Service.Helpers;

namespace RdtClient.Service.Test.Helpers;

/// <summary>
/// Covers the unified status-text + local-progress mapper. The behaviours pinned here
/// are the ones that have regressed at least once: in-flight downloads with no live
/// tracker bytes yet, and status/progress agreement across both outputs.
/// </summary>
public class TorrentDtoMapperTest
{
    private static (Int64 Speed, Int64 BytesTotal, Int64 BytesDone) NoStats(Guid _) => (0, 0, 0);

    private static Func<Guid, (Int64 Speed, Int64 BytesTotal, Int64 BytesDone)> StaticStats(IDictionary<Guid, (Int64 Speed, Int64 BytesTotal, Int64 BytesDone)> table)
    {
        return id => table.TryGetValue(id, out var value) ? value : (0, 0, 0);
    }

    private static Torrent NewTorrent(params Download[] downloads)
    {
        var torrentId = Guid.NewGuid();
        var t = new Torrent
        {
            TorrentId = torrentId,
            Hash = "deadbeef",
            FileOrMagnet = "magnet:?xt=urn:btih:deadbeef",
            Added = DateTimeOffset.UtcNow,
            Type = DownloadType.Torrent,
            Downloads = downloads.ToList()
        };

        foreach (var d in downloads)
        {
            d.TorrentId = torrentId;
        }

        return t;
    }

    private static Download Queued() => new()
    {
        DownloadId = Guid.NewGuid(),
        Path = "https://torbox.app/fakedl/1/0",
        Added = DateTimeOffset.UtcNow
    };

    private static Download Started() => new()
    {
        DownloadId = Guid.NewGuid(),
        Path = "https://torbox.app/fakedl/1/1",
        Added = DateTimeOffset.UtcNow,
        DownloadStarted = DateTimeOffset.UtcNow
    };

    private static Download Finished() => new()
    {
        DownloadId = Guid.NewGuid(),
        Path = "https://torbox.app/fakedl/1/2",
        Added = DateTimeOffset.UtcNow,
        DownloadStarted = DateTimeOffset.UtcNow,
        DownloadFinished = DateTimeOffset.UtcNow,
        Completed = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Maps_NoDownloads_LocalProgressNullAndStatusReflectsRdStatus()
    {
        var torrent = NewTorrent();
        torrent.RdStatus = TorrentStatus.Downloading;
        torrent.RdProgress = 42;
        torrent.RdSeeders = 5;

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, NoStats);

        Assert.Null(dto.LocalProgress);
        Assert.Equal("Torrent downloading (42%)", dto.StatusText);
    }

    [Fact]
    public void Maps_InFlightWithLiveBytes_ByteWeighted_StatusAndBarAgree()
    {
        // Two files: tiny .nfo finished (504 B, full size still reported by the
        // KnownDownloadSize cache), and a 24 GB .mkv at 17.82%. This is the screenshot
        // that prompted the byte-weighted switch — file-count weighting was reporting
        // 59% on the bar while the badge said 17.82%, two views of the same state.
        var finished = Finished();
        var inFlight = Started();

        var torrent = NewTorrent(finished, inFlight);

        var stats = StaticStats(new Dictionary<Guid, (Int64, Int64, Int64)>
        {
            // Finished download: simulates TorrentRunner.KnownDownloadSize returning
            // (0, size, size) after the live downloader was removed.
            [finished.DownloadId] = (Speed: 0, BytesTotal: 504, BytesDone: 504),
            [inFlight.DownloadId] = (Speed: 100_000_000, BytesTotal: 24_000_000_000, BytesDone: 4_276_800_000) // 17.82% of the mkv
        });

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, stats);

        // (504 + 4_276_800_000) / (504 + 24_000_000_000) ≈ 17.82% → 18
        Assert.Equal(18, dto.LocalProgress);
        // Status text now mirrors the bar — single overall percent, no per-file drift.
        Assert.Equal("Downloading file 2/2 (18%)", dto.StatusText);
    }

    [Fact]
    public void Maps_InFlightWithZeroByteTrackerWindow_FallsBackToFileCount()
    {
        // Regression: aria2 has started but hasn't fired its first progress event,
        // and the finished sibling isn't in the KnownDownloadSize cache yet either.
        // Without the size info we cannot do byte-weighted math safely (the cached
        // .nfo would dominate and drive the bar to 100%). Fall back to file-count.
        var finished = Finished();
        var startedNoBytes = Started();

        var torrent = NewTorrent(finished, startedNoBytes);

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, NoStats);

        Assert.StartsWith("Downloading file 2/2", dto.StatusText);
        // (1.0 + 0.0) / 2 = 0.5 → 50
        Assert.Equal(50, dto.LocalProgress);
    }

    [Fact]
    public void Maps_FinishedSiblingDominantWithoutSize_DoesNotBlipTo100()
    {
        // This is the failure mode that byte-weighted math could produce: one sibling
        // finished with a known size, the other in-flight without any size info yet.
        // Naive byte-weighting would compute (smallSize / smallSize) = 100% during the
        // window before the in-flight sibling's first progress event. The fallback to
        // file-count when any size is missing prevents that.
        var finished = Finished();
        var startedNoBytes = Started();

        var torrent = NewTorrent(finished, startedNoBytes);

        var stats = StaticStats(new Dictionary<Guid, (Int64, Int64, Int64)>
        {
            [finished.DownloadId] = (Speed: 0, BytesTotal: 504, BytesDone: 504)
            // startedNoBytes is intentionally missing — simulates the (0,0,0) window
        });

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, stats);

        // File-count fallback: (1.0 + 0.0) / 2 = 50, NOT 100.
        Assert.Equal(50, dto.LocalProgress);
    }

    [Fact]
    public void Maps_AllFinished_StatusFinishedAndLocalProgress100()
    {
        var torrent = NewTorrent(Finished(), Finished());

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, NoStats);

        Assert.Equal("Finished", dto.StatusText);
        Assert.Equal(100, dto.LocalProgress);
    }

    [Fact]
    public void Maps_AllQueued_StatusIsQueuedForDownloading_LocalProgress0()
    {
        var torrent = NewTorrent(Queued(), Queued());

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, NoStats);

        Assert.Equal("Queued for downloading", dto.StatusText);
        Assert.Equal(0, dto.LocalProgress);
    }

    [Fact]
    public void Maps_ToListDto_IncludesDownloads()
    {
        // The cold REST GET must carry the same Downloads payload as the SignalR push,
        // otherwise the frontend renders for ~1 s against an empty downloads array and
        // falls back to rdProgress (often 100 for cached items).
        var d1 = Finished();
        var d2 = Started();

        var torrent = NewTorrent(d1, d2);
        torrent.RdProgress = 100;

        var dto = TorrentDtoMapper.ToListDto(torrent, NoStats);

        Assert.Equal(2, dto.Downloads.Count);
        Assert.Equal(2, dto.DownloadsCount);
        Assert.NotNull(dto.LocalProgress);
    }

    [Fact]
    public void Maps_ErrorTakesPrecedence_LocalProgressNull()
    {
        var torrent = NewTorrent(Started());
        torrent.Error = "boom";

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, NoStats);

        Assert.Equal("boom", dto.StatusText);
        Assert.Null(dto.LocalProgress);
    }
}
