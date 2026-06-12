using RdtClient.Data.Models.Data;
using RdtClient.Service.Services;

namespace RdtClient.Service.Test.Services;

/// <summary>
/// The DownloadComplete handler must let the FIRST terminal event win. In the
/// 2026-06-11 incident a successful completion (Error == null) was followed one
/// poll cycle later by a spurious "Download was not found in Aria2" — the old
/// <c>Error ??= args.Error</c> adopted the late error and a byte-complete download
/// was sent into the retry path.
/// </summary>
public class DownloadClientTest
{
    private static DownloadClient CreateClient()
    {
        var download = new Download
        {
            DownloadId = Guid.NewGuid(),
            Path = "file.bin"
        };

        return new(download, new(), "/data/downloads", null);
    }

    [Fact]
    public void OnDownloadComplete_SuccessThenLateError_KeepsSuccess()
    {
        var client = CreateClient();

        client.OnDownloadComplete(new());
        client.OnDownloadComplete(new()
        {
            Error = "Download was not found in Aria2"
        });

        Assert.True(client.Finished);
        Assert.Null(client.Error);
    }

    [Fact]
    public void OnDownloadComplete_ErrorThenLateSuccess_KeepsError()
    {
        var client = CreateClient();

        client.OnDownloadComplete(new()
        {
            Error = "boom"
        });

        client.OnDownloadComplete(new());

        Assert.True(client.Finished);
        Assert.Equal("boom", client.Error);
    }

    [Fact]
    public void OnDownloadComplete_SingleError_SetsErrorAndFinished()
    {
        var client = CreateClient();

        client.OnDownloadComplete(new()
        {
            Error = "boom"
        });

        Assert.True(client.Finished);
        Assert.Equal("boom", client.Error);
    }
}
