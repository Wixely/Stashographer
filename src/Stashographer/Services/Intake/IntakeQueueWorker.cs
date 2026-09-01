using Stashographer.Services.Ai;
using Stashographer.Services.Config;
using Stashographer.Services.Modify;

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
        {
            await recoveryScope.ServiceProvider.GetRequiredService<IntakeQueueService>()
                .RecoverInterruptedAsync(stoppingToken);
            await recoveryScope.ServiceProvider.GetRequiredService<ModifyQueueService>()
                .RecoverInterruptedAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedAny = false;
                using (var scope = scopes.CreateScope())
                {
                    var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                    var options = await settings.GetIntakeOptionsAsync(stoppingToken);
                    var modifyOptions = await settings.GetModifyOptionsAsync(stoppingToken);
                    var ai = scope.ServiceProvider.GetRequiredService<IAiEnrichmentService>();
                    var intakeQueue = scope.ServiceProvider.GetRequiredService<IntakeQueueService>();
                    var modifyQueue = scope.ServiceProvider.GetRequiredService<ModifyQueueService>();
                    var intakeAt = options.QueueEnabled
                        ? await intakeQueue.GetNextProcessableCreatedAtAsync(options, ai.IsEnabled, stoppingToken)
                        : null;
                    var modifyAt = await modifyQueue.GetNextProcessableCreatedAtAsync(
                        modifyOptions, ai.IsEnabled, stoppingToken);
                    if (modifyAt is not null && (intakeAt is null || modifyAt <= intakeAt))
                    {
                        processedAny = await modifyQueue.ProcessNextAsync(
                            modifyOptions, ai.IsEnabled, stoppingToken);
                    }
                    else if (intakeAt is not null)
                    {
                        processedAny = await intakeQueue.ProcessNextAsync(
                            options, ai.IsEnabled, stoppingToken);
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
