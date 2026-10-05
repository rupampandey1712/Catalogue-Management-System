using Catalogue.Application;

namespace Catalogue.Workers;

public sealed class ImportWorker(
    IImportQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Import worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await queue.ReceiveAsync(stoppingToken);
                if (message is null) continue;

                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ImportProcessor>();

                using (logger.BeginScope(new Dictionary<string, object>
                {
                    ["ImportId"] = message.ImportId,
                    ["CorrelationId"] = message.CorrelationId
                }))
                {
                    await processor.ProcessAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in import worker; continuing.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
