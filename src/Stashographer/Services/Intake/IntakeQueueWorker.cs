using Stashographer.Services.Ai;
using Stashographer.Services.Config;

namespace Stashographer.Services.Intake;

/// <summary>Sequential worker so each model call sees the earlier items from its session.</summary>
public sealed class IntakeQueueWorker(
    IServiceScopeFactory scopes,
    IntakeQueueSignal signal,
    ILogger<IntakeQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var recoveryScope = scopes.CreateScope())
            await recoveryScope.ServiceProvider.GetRequiredService<IntakeQueueService>()
                .RecoverInterruptedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedAny = false;
                using (var scope = scopes.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                    var options = await settings.GetIntakeOptionsAsync(stoppingToken);
                    if (options.QueueEnabled)
                    {
                        var ai = scope.ServiceProvider.GetRequiredService<IAiEnrichmentService>();
                        var queue = scope.ServiceProvider.GetRequiredService<IntakeQueueService>();
                        processedAny = await queue.ProcessNextAsync(options, ai.IsEnabled, stoppingToken);
                    }
                }

                if (!processedAny)
                    await signal.WaitAsync(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Intake queue worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }
}
