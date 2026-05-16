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
    public void Maps_InFlightWithLiveBytes_StatusTextAndLocalProgressAgree()
    {
        // Two files: small one finished, big one mid-flight. This is the dashboard
        // screenshot scenario that originally regressed.
        var finished = Finished();
        var inFlight = Started();

        var torrent = NewTorrent(finished, inFlight);

        var stats = StaticStats(new Dictionary<Guid, (Int64, Int64, Int64)>
        {
            [inFlight.DownloadId] = (Speed: 100_000_000, BytesTotal: 24_000_000_000, BytesDone: 4_276_800_000) // 17.82%
        });

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, stats);

        // File-count weighted: (1.0 + 0.1782) / 2 = 0.5891 → 59
        Assert.Equal(59, dto.LocalProgress);
        Assert.Equal("Downloading file 2/2 (17.82%)", dto.StatusText);
    }

    [Fact]
    public void Maps_InFlightWithZeroByteTrackerWindow_StatusStillDownloading_LocalProgressIsHalf()
    {
        // Regression: aria2 has started but hasn't fired its first progress event.
        // getDownloadStats returns (0,0,0). The old code would (a) drop the download
        // out of the "downloading" branch and flip to "Queued for downloading", and
        // (b) cause the frontend bar to show 100% (the completed sibling dominated).
        var finished = Finished();
        var startedNoBytes = Started();

        var torrent = NewTorrent(finished, startedNoBytes);

        var dto = TorrentDtoMapper.ToUpdateDto(torrent, NoStats);

        Assert.StartsWith("Downloading file 2/2", dto.StatusText);
        // (1.0 + 0.0) / 2 = 0.5 → 50
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
