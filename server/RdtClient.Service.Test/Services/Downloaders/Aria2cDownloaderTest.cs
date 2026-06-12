using Aria2NET;
using Moq;
using RdtClient.Service.Services.Downloaders;

namespace RdtClient.Service.Test.Services.Downloaders;

/// <summary>
/// Regression tests for the 2026-06-11 production incident: a download observed
/// complete triggers Remove() (aria2.removeDownloadResult), purging its gid from
/// aria2 — a snapshot taken AFTER that self-removal no longer contains the gid, and
/// the poller's timing discriminator alone can't tell that apart from aria2
/// genuinely losing the download. The downloader must finalize on any terminal
/// transition so later snapshots are no-ops, while a genuine loss (no completion
/// observed) must still surface the not-found error.
/// </summary>
[Collection("Settings")]
public class Aria2cDownloaderTest : IDisposable
{
    private const String Gid = "652864ee4c83e1f1";

    private readonly String _filePath = Path.Combine(Path.GetTempPath(), $"rdt-aria2-test-{Guid.NewGuid():N}.bin");

    private readonly Mock<IAria2cClient> _client = new();
    private readonly List<DownloadCompleteEventArgs> _completeEvents = [];
    private readonly List<DownloadProgressEventArgs> _progressEvents = [];

    public void Dispose()
    {
        File.Delete(_filePath);
    }

    private Aria2cDownloader CreateDownloader()
    {
        _client.Setup(m => m.ForceRemoveAsync(It.IsAny<String>(), It.IsAny<CancellationToken>())).ReturnsAsync("ok");
        _client.Setup(m => m.RemoveDownloadResultAsync(It.IsAny<String>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var downloader = new Aria2cDownloader(Gid, "https://example.com/file.bin", _filePath, "file.bin", null, _client.Object);

        downloader.DownloadComplete += (_, args) => _completeEvents.Add(args);
        downloader.DownloadProgress += (_, args) => _progressEvents.Add(args);

        return downloader;
    }

    private static DownloadStatusResult Snapshot(String status, Int64 done = 100, Int64 total = 100, String? errorMessage = null, String? errorCode = null)
    {
        return new()
        {
            Gid = Gid,
            Status = status,
            CompletedLength = done,
            TotalLength = total,
            DownloadSpeed = 0,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode
        };
    }

    [Fact]
    public async Task Update_SnapshotMissingGidAfterSelfRemoveOnCompletion_DoesNotEmitNotFoundError()
    {
        // Arrange: the file aria2 wrote is present on disk.
        await File.WriteAllTextAsync(_filePath, "complete");
        var downloader = CreateDownloader();

        // Act: a snapshot reports the download complete -> downloader purges the gid
        // from aria2 itself and fires the success event.
        await downloader.Update([Snapshot("complete")]);

        // A subsequent poll cycle uses a snapshot taken after the self-removal: the
        // gid is gone, but that absence is self-inflicted and must not error.
        await downloader.Update(Array.Empty<DownloadStatusResult>());

        // Assert
        var completeEvent = Assert.Single(_completeEvents);
        Assert.Null(completeEvent.Error);
        Assert.True(downloader.IsFinalized);

        _client.Verify(m => m.ForceRemoveAsync(Gid, It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(m => m.RemoveDownloadResultAsync(Gid, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_SnapshotMissingGidWithoutCompletion_StillEmitsNotFoundError()
    {
        // Arrange: no completion was ever observed — aria2 genuinely lost the gid.
        var downloader = CreateDownloader();

        // Act
        await downloader.Update(Array.Empty<DownloadStatusResult>());

        // Assert
        var completeEvent = Assert.Single(_completeEvents);
        Assert.Equal("Download was not found in Aria2", completeEvent.Error);
        Assert.True(downloader.IsFinalized);
    }

    [Fact]
    public async Task Update_SnapshotMissingGidAfterGenuineLoss_DoesNotRepeatError()
    {
        var downloader = CreateDownloader();

        await downloader.Update(Array.Empty<DownloadStatusResult>());
        await downloader.Update(Array.Empty<DownloadStatusResult>());

        Assert.Single(_completeEvents);
    }

    [Fact]
    public async Task Update_ErrorStatus_EmitsErrorOnceAndIgnoresLaterSnapshots()
    {
        var downloader = CreateDownloader();

        await downloader.Update([Snapshot("error", errorMessage: "boom", errorCode: "3")]);
        await downloader.Update(Array.Empty<DownloadStatusResult>());

        var completeEvent = Assert.Single(_completeEvents);
        Assert.Equal("3: boom", completeEvent.Error);
        Assert.True(downloader.IsFinalized);
    }

    [Fact]
    public async Task Update_AfterCancel_IsNoOp()
    {
        var downloader = CreateDownloader();

        await downloader.Cancel();

        // Whether the gid is still in the snapshot (cancel raced the poll) or
        // already purged, no further events may fire.
        await downloader.Update([Snapshot("active", done: 50)]);
        await downloader.Update(Array.Empty<DownloadStatusResult>());

        Assert.Empty(_completeEvents);
        Assert.Empty(_progressEvents);
        Assert.True(downloader.IsFinalized);

        _client.Verify(m => m.ForceRemoveAsync(Gid, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_ActiveStatus_EmitsProgressAndDoesNotFinalize()
    {
        var downloader = CreateDownloader();

        await downloader.Update([Snapshot("active", done: 50)]);

        var progressEvent = Assert.Single(_progressEvents);
        Assert.Equal(50, progressEvent.BytesDone);
        Assert.Empty(_completeEvents);
        Assert.False(downloader.IsFinalized);
    }
}
