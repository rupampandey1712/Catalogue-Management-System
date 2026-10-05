using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Catalogue.Application.Streaming;
using Catalogue.Contracts;

namespace Catalogue.Infrastructure.Streaming;

public sealed class EventHubOptions
{
    public bool Enabled { get; set; }
    public string HubName { get; set; } = "catalogue-edi";
    public string ConsumerGroup { get; set; } = "catalogue-ingest";
    /// <summary>Partition count of the simulated in-memory hub (real hubs report their own).</summary>
    public int PartitionCount { get; set; } = 4;
    /// <summary>Run the partition consumers inside this process.</summary>
    public bool ConsumerInProcess { get; set; } = true;
    public string CheckpointContainer { get; set; } = "eventhub-checkpoints";
    public int ConcurrentPublishers { get; set; } = 2;
    public string? SourceDirectory { get; set; }
}

/// <summary>Explicit SDK retry policy: exponential backoff, bounded attempts and per-try timeout.</summary>
public static class EventHubRetry
{
    public static EventHubsRetryOptions Options => new()
    {
        Mode = EventHubsRetryMode.Exponential,
        MaximumRetries = 6,
        Delay = TimeSpan.FromMilliseconds(500),
        MaximumDelay = TimeSpan.FromSeconds(30),
        TryTimeout = TimeSpan.FromSeconds(60)
    };
}

/// <summary>Maps chunk events to/from Event Hubs events (EDI body + routing metadata as properties).</summary>
internal static class EdiEventCodec
{
    public static EventData ToEventData(EdiChunkEvent e)
    {
        var data = new EventData(Encoding.UTF8.GetBytes(e.Payload))
        {
            ContentType = "application/edifact",
            MessageId = $"{e.ImportId}-{e.ChunkIndex}",
            CorrelationId = e.CorrelationId
        };
        data.Properties["importId"] = e.ImportId;
        data.Properties["supplierId"] = e.SupplierId;
        data.Properties["chunkIndex"] = e.ChunkIndex;
        data.Properties["firstLineItem"] = e.FirstLineItem;
        data.Properties["lineItems"] = e.LineItems;
        data.Properties["correlationId"] = e.CorrelationId;
        if (e.TraceParent is not null) data.Properties["traceparent"] = e.TraceParent;
        return data;
    }

    public static EdiChunkEvent FromEventData(EventData data)
    {
        long L(string key) => Convert.ToInt64(data.Properties[key], CultureInfo.InvariantCulture);
        return new EdiChunkEvent(L("importId"), L("supplierId"), (int)L("chunkIndex"), L("firstLineItem"),
            (int)L("lineItems"), data.CorrelationId ?? "", Encoding.UTF8.GetString(data.EventBody.ToArray()),
            data.Properties.TryGetValue("traceparent", out var tp) ? tp as string : null);
    }
}

/// <summary>Azure Event Hubs producer (works unchanged against the Event Hubs emulator).</summary>
public sealed class AzureEventHubProducer : IEventHubProducer, IAsyncDisposable
{
    private readonly EventHubProducerClient _client;
    private readonly EventHubOptions _options;
    private IReadOnlyList<PartitionInfo> _cached = [];
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public AzureEventHubProducer(string connectionString, EventHubOptions options)
    {
        _options = options;
        _client = new EventHubProducerClient(connectionString, options.HubName, new EventHubProducerClientOptions
        {
            RetryOptions = EventHubRetry.Options
        });
    }

    public string Mode => "EventHubs";
    public string HubName => _client.EventHubName;
    public string ConsumerGroup => _options.ConsumerGroup;
    public string Namespace => _client.FullyQualifiedNamespace;

    public async Task SendAsync(IReadOnlyList<EdiChunkEvent> events, CancellationToken ct)
    {
        // Pack as many chunk events as fit in a 1 MB batch; the service round-robins batches across partitions.
        var batch = await _client.CreateBatchAsync(ct);
        try
        {
            foreach (var e in events)
            {
                var data = EdiEventCodec.ToEventData(e);
                if (batch.TryAdd(data)) continue;
                if (batch.Count == 0) throw new InvalidOperationException(
                    $"Chunk {e.ChunkIndex} ({e.Payload.Length} chars) exceeds the Event Hubs event size limit; lower the chunk size.");
                await _client.SendAsync(batch, ct);
                batch.Dispose();
                batch = await _client.CreateBatchAsync(ct);
                if (!batch.TryAdd(data)) throw new InvalidOperationException("Event too large for an empty batch.");
            }
            if (batch.Count > 0) await _client.SendAsync(batch, ct);
        }
        finally { batch.Dispose(); }
    }

    public async Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromSeconds(1)) return _cached;
        var ids = await _client.GetPartitionIdsAsync(ct);
        var props = await Task.WhenAll(ids.Select(id => _client.GetPartitionPropertiesAsync(id, ct)));
        _cached = props.Select(p => new PartitionInfo(p.Id, p.LastEnqueuedSequenceNumber,
            p.IsEmpty ? null : p.LastEnqueuedTime, p.IsEmpty)).ToList();
        _cachedAtUtc = DateTime.UtcNow;
        return _cached;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

/// <summary>
/// Local stand-in for Event Hubs: N bounded partitions with sequence numbers and round-robin
/// assignment. Bounded capacity gives real back-pressure, so consumer lag is visible locally.
/// </summary>
public sealed class InMemoryEventHub : IEventHubProducer
{
    public sealed record Envelope(EdiChunkEvent Event, long SequenceNumber, DateTimeOffset EnqueuedUtc);

    private sealed class Partition
    {
        public required string Id;
        public required Channel<Envelope> Channel;
        public long NextSequence;
        public DateTimeOffset? LastEnqueuedUtc;
    }

    private readonly Partition[] _partitions;
    private readonly EventHubOptions _options;
    private int _next = -1;

    public InMemoryEventHub(EventHubOptions options)
    {
        _options = options;
        _partitions = Enumerable.Range(0, Math.Clamp(options.PartitionCount, 1, 32))
            .Select(i => new Partition
            {
                Id = i.ToString(CultureInfo.InvariantCulture),
                Channel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(64)
                    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true })
            }).ToArray();
    }

    public string Mode => "InMemory";
    public string HubName => _options.HubName;
    public string ConsumerGroup => "$Default";
    public IReadOnlyList<string> PartitionIds => _partitions.Select(p => p.Id).ToList();

    public async Task SendAsync(IReadOnlyList<EdiChunkEvent> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            var p = _partitions[(int)((uint)Interlocked.Increment(ref _next) % _partitions.Length)];
            await p.Channel.Writer.WaitToWriteAsync(ct);
            long seq;
            lock (p)
            {
                seq = p.NextSequence++;
                p.LastEnqueuedUtc = DateTimeOffset.UtcNow;
            }
            await p.Channel.Writer.WriteAsync(new Envelope(e, seq, DateTimeOffset.UtcNow), ct);
        }
    }

    public ChannelReader<Envelope> Reader(string partitionId) => _partitions.First(p => p.Id == partitionId).Channel.Reader;

    public Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PartitionInfo>>(_partitions
            .Select(p => new PartitionInfo(p.Id, p.NextSequence - 1, p.LastEnqueuedUtc, p.NextSequence == 0))
            .ToList());
}
