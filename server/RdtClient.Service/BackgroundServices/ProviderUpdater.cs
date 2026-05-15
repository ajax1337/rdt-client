using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Services;

namespace RdtClient.Service.BackgroundServices;

public class ProviderUpdater(ILogger<ProviderUpdater> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private static DateTime _nextUpdate = DateTime.UtcNow;

    // Warm-poll: after a torrent is dequeued (RdId just set), we want to react fast to
    // TorBox flipping `download_present` from false → true (the gate that marks the
    // torrent as ready and triggers the aria2 handover). Without this we'd wait up to
    // CheckInterval seconds (5s) between polls; for a hot-cached torrent that's most of
    // the perceived "start lag". WarmPoll temporarily drops the interval to 1s for a
    // bounded window (default 60s). After the window expires we revert to CheckInterval.
    private static DateTime _warmPollUntil = DateTime.MinValue;

    public static void RequestWarmPoll(TimeSpan? duration = null)
    {
        var window = duration ?? TimeSpan.FromSeconds(60);
        var until = DateTime.UtcNow.Add(window);
        if (until > _warmPollUntil)
        {
            _warmPollUntil = until;
        }
        // Also nudge _nextUpdate so the very next tick fires the warm-cadence poll
        // even if we're mid-interval.
        if (_nextUpdate > DateTime.UtcNow.AddSeconds(1))
        {
            _nextUpdate = DateTime.UtcNow.AddSeconds(1);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        logger.LogInformation("ProviderUpdater started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = serviceProvider.CreateScope();
            var torrentService = scope.ServiceProvider.GetRequiredService<Torrents>();
            var torrentRunner = scope.ServiceProvider.GetRequiredService<TorrentRunner>();

            try
            {
                var torrents = await torrentService.Get();

                if (_nextUpdate < DateTime.UtcNow && (Settings.Get.Provider.AutoImport || torrents.Any(t => t.RdStatus != TorrentStatus.Finished) || RdtHub.HasConnections))
                {
                    logger.LogDebug($"Updating torrent info from debrid provider");

                    // Patched: original logic backed off to 30s when no dashboard
                    // was connected (CheckInterval * 3, hard-floored at 30). For a
                    // single-user self-hosted setup that made "torrent added → first
                    // bytes" feel sluggish since cached Torbox torrents could sit up
                    // to 30s before rdt-client noticed. Collapsed both paths to a
                    // single CheckInterval-driven cadence with a 5s floor.
                    //
                    // During the warm-poll window (set right after DequeueFromDebridQueue),
                    // we drop the floor to 1s so the cached → download_present flip is
                    // picked up within ~1s instead of within CheckInterval seconds.
                    var updateTime = Settings.Get.Provider.CheckInterval;
                    var inWarmPoll = _warmPollUntil > DateTime.UtcNow;

                    if (inWarmPoll && updateTime > 1)
                    {
                        updateTime = 1;
                    }
                    else if (updateTime < 5)
                    {
                        updateTime = 5;
                    }

                    _nextUpdate = DateTime.UtcNow.AddSeconds(updateTime);

                    await torrentService.UpdateRdData();

                    logger.LogDebug("Finished updating torrent info from debrid provider, next update in {updateTime} seconds", updateTime);
                }
            }
            catch (RateLimitException ex)
            {
                await torrentRunner.SetRateLimit(ex.RetryAfter, ex.Message);
                _nextUpdate = DateTime.UtcNow.Add(ex.RetryAfter);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error occurred in ProviderUpdater: {ex.Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }

        logger.LogInformation("ProviderUpdater stopped.");
    }
}
