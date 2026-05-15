using System.Diagnostics;
using System.Reflection;
using Aria2NET;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RdtClient.Data.Data;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using RdtClient.Service.Services;
using RdtClient.Service.Services.Downloaders;
using DownloadClient = RdtClient.Data.Enums.DownloadClient;

namespace RdtClient.Web.Controllers;

[Authorize(Policy = "AuthSetting")]
[Route("Api/Settings")]
public class SettingsController(Settings settings, Torrents torrents) : Controller
{
    [HttpGet]
    [Route("")]
    public ActionResult Get()
    {
        var result = SettingData.GetAll();

        return Ok(result);
    }

    [HttpPut]
    [Route("")]
    public async Task<ActionResult> Update([FromBody] IList<SettingProperty>? settings1)
    {
        if (settings1 == null)
        {
            return BadRequest();
        }

        await settings.Update(settings1);

        return Ok();
    }

    [HttpGet]
    [Route("Profile")]
    public async Task<ActionResult<Profile>> Profile()
    {
        var profile = await torrents.GetProfile();

        return Ok(profile);
    }

    [HttpGet]
    [Route("Version")]
    public ActionResult<Version> Version()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version!;

        return Ok(new
        {
            Version = version
        });
    }

    [HttpPost]
    [Route("TestPath")]
    public async Task<ActionResult> TestPath([FromBody] SettingsControllerTestPathRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (String.IsNullOrEmpty(request.Path))
        {
            return BadRequest("Invalid path");
        }

        var path = request.Path.TrimEnd('/').TrimEnd('\\');

        if (!Directory.Exists(path))
        {
            throw new($"Path {path} does not exist");
        }

        var testFile = $"{path}/test.txt";

        await System.IO.File.WriteAllTextAsync(testFile, "RealDebridClient Test File, you can remove this file.");

        await FileHelper.Delete(testFile);

        return Ok();
    }

    [HttpGet]
    [Route("TestDownloadSpeed")]
    public async Task<ActionResult> TestDownloadSpeed(CancellationToken cancellationToken)
    {
        var downloadPath = Settings.Get.DownloadClient.DownloadPath;

        // Speed-test source. The historical default was
        // https://34.download.real-debrid.com/speedtest/testDefault.rar, but server
        // 34 in RealDebrid's CDN pool is unreachable from many networks (TCP
        // straight up refuses). Switching to server 20 — empirically reachable
        // from a wider set of hosts and serves the same 10 GB octet-stream
        // (~130 MB/s in testing). Still a public RealDebrid CDN URL, no auth.
        // TorBox has no equivalent public speed-test file (their CDN nodes
        // require per-user tokens), so RD's server is the cleanest provider-
        // hosted option regardless of which debrid you actually use.
        // Override with RDTCLIENT_SPEEDTEST_URL for a closer mirror.
        var testUrl = Environment.GetEnvironmentVariable("RDTCLIENT_SPEEDTEST_URL");
        if (String.IsNullOrWhiteSpace(testUrl))
        {
            testUrl = "https://20.download.real-debrid.com/speedtest/testDefault.rar";
        }
        var testFileName = Path.GetFileName(new Uri(testUrl).AbsolutePath);
        if (String.IsNullOrEmpty(testFileName))
        {
            testFileName = "speedtest.bin";
        }

        var testFilePath = Path.Combine(downloadPath, testFileName);

        await FileHelper.Delete(testFilePath);

        var download = new Download
        {
            Link = testUrl,
            Torrent = new()
            {
                DownloadClient = Settings.Get.DownloadClient.Client == DownloadClient.Symlink ? DownloadClient.Bezzad : Settings.Get.DownloadClient.Client,
                RdName = testFileName
            }
        };

        var downloadClient = new Service.Services.DownloadClient(download, download.Torrent, downloadPath, null);

        await downloadClient.Start();

        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        // Hard ceiling so the endpoint always returns. Without a cap the polling
        // loop runs as long as `Finished` stays false — if aria2 can't even reach
        // the test URL (e.g. the RealDebrid CDN host is unreachable from the
        // host network), it errors and retries in a tight loop and the spinner
        // on the Settings page spins forever. Bail at 30 s with a 400 + the
        // last-known speed so the user sees a failure instead of a hang.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (!downloadClient.Finished)
        {
            await Task.Delay(1000, CancellationToken.None);

            if (cancellationToken.IsCancellationRequested)
            {
                await downloadClient.Cancel();
            }

            if (downloadClient.Downloader is Aria2cDownloader aria2Downloader)
            {
                var aria2NetClient = new Aria2NetClient(Settings.Get.DownloadClient.Aria2cUrl, Settings.Get.DownloadClient.Aria2cSecret, httpClient, 1);

                var allDownloads = await aria2NetClient.TellAllAsync(cancellationToken);

                await aria2Downloader.Update(allDownloads);
            }

            if (downloadClient.BytesDone > 1024 * 1024 * 50)
            {
                await downloadClient.Cancel();

                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                await downloadClient.Cancel();
                await FileHelper.Delete(testFilePath);

                var why = downloadClient.BytesDone == 0
                    ? "Could not reach the speed-test URL (no bytes received in 30 s). Check that your host can reach real-debrid.com and that the configured downloader is working."
                    : $"Test timed out after 30 s with only {downloadClient.BytesDone} bytes received.";

                return BadRequest(why);
            }
        }

        await FileHelper.Delete(testFilePath);

        return Ok(downloadClient.Speed);
    }

    [HttpGet]
    [Route("TestWriteSpeed")]
    public async Task<ActionResult> TestWriteSpeed()
    {
        var downloadPath = Settings.Get.DownloadClient.DownloadPath;

        var testFilePath = Path.Combine(downloadPath, "test.tmp");

        await FileHelper.Delete(testFilePath);

        const Int32 testFileSize = 64 * 1024 * 1024;

        var watch = new Stopwatch();

        watch.Start();

        var rnd = new Random();

        await using var fileStream = new FileStream(testFilePath, FileMode.Create, FileAccess.Write, FileShare.Write);

        var buffer = new Byte[64 * 1024];

        while (fileStream.Length < testFileSize)
        {
            rnd.NextBytes(buffer);

            await fileStream.WriteAsync(buffer.AsMemory(0, buffer.Length));
        }

        watch.Stop();

        var writeSpeed = fileStream.Length / watch.Elapsed.TotalSeconds;

        fileStream.Close();

        await FileHelper.Delete(testFilePath);

        return Ok(writeSpeed);
    }

    [HttpPost]
    [Route("TestAria2cConnection")]
    public async Task<ActionResult<String>> TestAria2cConnection([FromBody] SettingsControllerTestAria2cConnectionRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (String.IsNullOrEmpty(request.Url))
        {
            return BadRequest("Invalid Url");
        }

        var client = new Aria2NetClient(request.Url, request.Secret);

        var version = await client.GetVersionAsync();

        return Ok(version);
    }
}

public class SettingsControllerTestPathRequest
{
    public String? Path { get; set; }
}

public class SettingsControllerTestAria2cConnectionRequest
{
    public String? Url { get; set; }
    public String? Secret { get; set; }
}
