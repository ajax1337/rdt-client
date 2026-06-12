using RdtClient.Data.Models.Data;
using RdtClient.Service.Services;

namespace RdtClient.Service.Test.Services;

/// <summary>
/// The download (re)start gate must NOT require Error == null: the retry path
/// (Reset + UpdateError write-back for the UI) leaves the transient error on the
/// row while it waits to restart. Gating on Error wedged those rows forever —
/// Error set, DownloadStarted/DownloadFinished null, never reprocessed — and the
/// torrent sat at "Waiting for downloads to complete" until a manual SQL reset.
/// </summary>
public class TorrentRunnerTest
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CanStartDownload_TransientRetryRowWithError_IsEligible(Int32 retryCount)
    {
        // The exact wedged state observed in production on 2026-06-11.
        var download = new Download
        {
            Path = "file.bin",
            DownloadQueued = DateTimeOffset.UtcNow,
            DownloadStarted = null,
            DownloadFinished = null,
            Completed = null,
            Error = "Download was not found in Aria2",
            RetryCount = retryCount
        };

        Assert.True(TorrentRunner.CanStartDownload(download));
    }

    [Fact]
    public void CanStartDownload_FreshQueuedRow_IsEligible()
    {
        var download = new Download
        {
            Path = "file.bin",
            DownloadQueued = DateTimeOffset.UtcNow
        };

        Assert.True(TorrentRunner.CanStartDownload(download));
    }

    [Fact]
    public void CanStartDownload_PermanentFailure_IsNotEligible()
    {
        // Every permanent failure path sets Completed alongside Error.
        var download = new Download
        {
            Path = "file.bin",
            DownloadQueued = DateTimeOffset.UtcNow,
            Completed = DateTimeOffset.UtcNow,
            Error = "boom"
        };

        Assert.False(TorrentRunner.CanStartDownload(download));
    }

    [Fact]
    public void CanStartDownload_InFlightRow_IsNotEligible()
    {
        var download = new Download
        {
            Path = "file.bin",
            DownloadQueued = DateTimeOffset.UtcNow,
            DownloadStarted = DateTimeOffset.UtcNow
        };

        Assert.False(TorrentRunner.CanStartDownload(download));
    }

    [Fact]
    public void CanStartDownload_NotQueuedRow_IsNotEligible()
    {
        var download = new Download
        {
            Path = "file.bin"
        };

        Assert.False(TorrentRunner.CanStartDownload(download));
    }
}
