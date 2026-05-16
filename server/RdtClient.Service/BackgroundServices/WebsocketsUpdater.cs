using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RdtClient.Service.Services;

namespace RdtClient.Service.BackgroundServices;

public class WebsocketsUpdater(ILogger<WebsocketsUpdater> logger, IServiceProvider serviceProvider) : BackgroundService
{
    // When a single Update() takes longer than this we log a warning. The push cadence
    // when the dashboard is open is 1 s, so anything over ~750 ms is eating into the
    // user-perceived freshness budget.
    private static readonly TimeSpan SlowTickThreshold = TimeSpan.FromMilliseconds(750);

    // Capped exponential backoff after consecutive exceptions so we don't tight-loop
    // an unhealthy DB or SignalR hub. Reset on the first successful tick.
    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        logger.LogInformation("WebsocketsUpdater started.");

        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var stopwatch = Stopwatch.StartNew();
            var failed = false;

            try
            {
                using var scope = serviceProvider.CreateScope();
                var remoteService = scope.ServiceProvider.GetRequiredService<RemoteService>();

                await remoteService.Update();
            }
            catch (Exception ex)
            {
                failed = true;
                logger.LogError(ex, "Unexpected error occurred in WebsocketsUpdater: {Message}", ex.Message);
            }

            stopwatch.Stop();

            if (!failed)
            {
                consecutiveFailures = 0;

                if (stopwatch.Elapsed > SlowTickThreshold)
                {
                    logger.LogWarning("WebsocketsUpdater tick took {Elapsed}ms — pushes are dropping behind the 1 s SignalR cadence; expect dashboard staleness.", stopwatch.ElapsedMilliseconds);
                }
            }
            else
            {
                consecutiveFailures += 1;
            }

            TimeSpan delay;

            if (consecutiveFailures > 0)
            {
                // 1 s, 2 s, 4 s, 8 s, 16 s, 30 s (cap). Linger at 30 s while broken.
                var seconds = Math.Min(MaxFailureBackoff.TotalSeconds, Math.Pow(2, Math.Min(consecutiveFailures - 1, 5)));
                delay = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                delay = RdtHub.HasConnections
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(5);
            }

            await Task.Delay(delay, stoppingToken);
        }

        logger.LogInformation("WebsocketsUpdater stopped.");
    }
}
