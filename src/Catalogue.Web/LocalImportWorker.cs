using Catalogue.Application;

namespace Catalogue.Web;

/// <summary>
/// Runs the import worker in-process during local development so `dotnet run` alone
/// processes imports. In Azure, the standalone Catalogue.Workers process does this
/// against Service Bus instead.
/// </summary>
public sealed class LocalImportWorker(
    IImportQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<LocalImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Local in-process import worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await queue.ReceiveAsync(stoppingToken);
                if (message is null) continue;
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ImportProcessor>();
                await processor.ProcessAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Local import worker error.");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
