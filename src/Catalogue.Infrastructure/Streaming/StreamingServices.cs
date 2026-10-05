using System.Collections.Concurrent;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using Catalogue.Application;
using Catalogue.Application.Streaming;
using Catalogue.Infrastructure.Resilience;
using Catalogue.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Catalogue.Infrastructure.Streaming;

/// <summary>
/// Event Hubs consumer. Against Azure / the emulator it runs an <see cref="EventProcessorClient"/>
/// (partition ownership load-balanced across every instance in the consumer group, checkpoints in
/// Blob storage). Locally it drains the in-memory hub with one loop per partition.
/// </summary>
public sealed class EventHubIngestionService(
    IEventHubProducer hub,
    EdiChunkProcessor processor,
    IBulkCatalogueWriter writer,
    StreamTelemetry telemetry,
    EventHubOptions options,
    IServiceProvider services,
    ILogger<EventHubIngestionService> logger) : BackgroundService
{
    private const int MaxAttempts = 5;
    private const int CheckpointEveryEvents = 25;
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => hub switch
    {
        InMemoryEventHub memory => RunInMemoryAsync(memory, stoppingToken),
        _ => RunEventProcessorAsync(stoppingToken)
    };

    private Task RunInMemoryAsync(InMemoryEventHub memory, CancellationToken ct)
    {
        logger.LogInformation("In-memory Event Hub consumer started on {Count} partitions.", memory.PartitionIds.Count);
        return Task.WhenAll(memory.PartitionIds.Select(async pid =>
        {
            telemetry.PartitionClaimed(pid);
            try
            {
                await foreach (var env in memory.Reader(pid).ReadAllAsync(ct))
                    await HandleAsync(env.Event, pid, env.SequenceNumber, env.EnqueuedUtc, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }));
    }

    private async Task RunEventProcessorAsync(CancellationToken ct)
    {
        var connection = services.GetRequiredService<EventHubConnection>();
        var checkpoints = new BlobContainerClient(connection.CheckpointStorage, options.CheckpointContainer);

        // The emulator/Azurite may still be starting; retry instead of crashing the host.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await checkpoints.CreateIfNotExistsAsync(cancellationToken: ct);
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 30)
            {
                logger.LogWarning("Checkpoint store not reachable ({Message}); retrying.", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), ct);
            }
        }

        var processorClient = new EventProcessorClient(checkpoints, options.ConsumerGroup,
            connection.EventHub, options.HubName, new EventProcessorClientOptions
            {
                PrefetchCount = 300,
                MaximumWaitTime = TimeSpan.FromSeconds(2),
                LoadBalancingStrategy = LoadBalancingStrategy.Greedy,
                RetryOptions = EventHubRetry.Options
            });

        var sinceCheckpoint = new ConcurrentDictionary<string, (int Count, DateTime At)>();

        processorClient.PartitionInitializingAsync += args =>
        {
            telemetry.PartitionClaimed(args.PartitionId);
            logger.LogInformation("Claimed partition {Partition}.", args.PartitionId);
            return Task.CompletedTask;
        };
        processorClient.PartitionClosingAsync += args =>
        {
            telemetry.PartitionReleased(args.PartitionId);
            logger.LogInformation("Released partition {Partition} ({Reason}).", args.PartitionId, args.Reason);
            return Task.CompletedTask;
        };
        processorClient.ProcessEventAsync += async args =>
        {
            if (!args.HasEvent) return;
            var pid = args.Partition.PartitionId;
            await HandleAsync(EdiEventCodec.FromEventData(args.Data), pid, args.Data.SequenceNumber,
                args.Data.EnqueuedTime, args.CancellationToken);

            var (count, at) = sinceCheckpoint.GetOrAdd(pid, _ => (0, DateTime.UtcNow));
            count++;
            if (count >= CheckpointEveryEvents || DateTime.UtcNow - at >= CheckpointEvery)
            {
                await args.UpdateCheckpointAsync(args.CancellationToken);
                count = 0;
                at = DateTime.UtcNow;
            }
            sinceCheckpoint[pid] = (count, at);
        };
        processorClient.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Event processor error on partition {Partition} during {Operation}.",
                args.PartitionId, args.Operation);
            if (args.PartitionId is not null) telemetry.ProcessingFailed(args.PartitionId);
            return Task.CompletedTask;
        };

        logger.LogInformation("Event Hub processor starting: hub {Hub}, consumer group {Group}.",
            options.HubName, options.ConsumerGroup);
        await processorClient.StartProcessingAsync(ct);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await processorClient.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task HandleAsync(EdiChunkEvent chunk, string partitionId, long sequence,
        DateTimeOffset enqueued, CancellationToken ct)
    {
        // Every log line, span and outbound call for this chunk carries the import's correlation ID.
        using var correlation = CorrelationContext.Begin(chunk.CorrelationId);
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["ImportId"] = chunk.ImportId, ["ChunkIndex"] = chunk.ChunkIndex, ["PartitionId"] = partitionId
        });
        using var activity = CatalogueDiagnostics.StartConsumer("eventhub.chunk.process", chunk.TraceParent, chunk.CorrelationId);
        activity?.SetTag("messaging.system", "eventhubs");
        activity?.SetTag("messaging.destination.partition.id", partitionId);
        activity?.SetTag("messaging.eventhubs.message.sequence_number", sequence);
        activity?.SetTag("catalogue.import_id", chunk.ImportId);
        activity?.SetTag("catalogue.chunk_index", chunk.ChunkIndex);

        var permanentFailures = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await processor.ProcessAsync(chunk, partitionId, sequence, enqueued, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (TransientFaults.IsTransient(ex))
            {
                // Dependency outage (DB down, circuit open, throttling): never dead-letter valid data.
                // Hold the partition and retry with capped backoff; the checkpoint does not advance.
                telemetry.ProcessingFailed(partitionId);
                CatalogueDiagnostics.ChunkFailures.Add(1, new KeyValuePair<string, object?>("kind", "transient"));
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                logger.LogWarning("Transient failure applying chunk (attempt {Attempt}); waiting {Delay}s. {Error}",
                    attempt, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (++permanentFailures < MaxAttempts)
            {
                telemetry.ProcessingFailed(partitionId);
                CatalogueDiagnostics.ChunkFailures.Add(1, new KeyValuePair<string, object?>("kind", "permanent"));
                logger.LogWarning(ex, "Chunk failed (attempt {Attempt}/{Max}); retrying.", permanentFailures, MaxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, permanentFailures)), ct);
            }
            catch (Exception ex)
            {
                // Poison chunk: record it as row errors so the import still reaches a terminal state.
                telemetry.ProcessingFailed(partitionId);
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                logger.LogError(ex, "Chunk dead-lettered after {Attempts} attempts.", MaxAttempts);
                await writer.WriteChunkAsync(new BulkChunk(chunk.ImportId, chunk.SupplierId, chunk.ChunkIndex, [],
                    [new BulkError((int)Math.Min(int.MaxValue, chunk.FirstLineItem), "Chunk",
                        $"Chunk {chunk.ChunkIndex} failed after {MaxAttempts} attempts: {ex.Message}", null)],
                    chunk.LineItems), ct);
                return;
            }
        }
    }
}

/// <summary>Runs queued stream jobs (file -> Event Hub) with bounded concurrency.</summary>
public sealed class EdiStreamPublisherService(
    StreamJobQueue jobs,
    IServiceScopeFactory scopes,
    EventHubOptions options,
    ILogger<EdiStreamPublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Parallel.ForEachAsync(jobs.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.ConcurrentPublishers), CancellationToken = stoppingToken },
            async (job, ct) =>
            {
                try
                {
                    using var correlation = CorrelationContext.Begin(job.CorrelationId);
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<EdiStreamPublisher>().RunAsync(job, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Stream job for import {ImportId} crashed.", job.ImportId);
                }
            }).ConfigureAwait(false);
    }
}

public sealed record EventHubConnection(string EventHub, string CheckpointStorage);
