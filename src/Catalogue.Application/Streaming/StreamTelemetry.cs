namespace Catalogue.Application.Streaming;

/// <summary>
/// In-process live metrics for the Event Hub pipeline (publish + consume), feeding the
/// dashboard. Durable totals live on ImportFile; this is the "what is happening right now" view
/// for the partitions this instance owns.
/// </summary>
public sealed class StreamTelemetry
{
    private const int TimelineSeconds = 180;
    private const int RecentCapacity = 60;
    private const int RunCapacity = 25;

    private readonly Lock _gate = new();
    private readonly Bucket[] _timeline = new Bucket[TimelineSeconds];
    private readonly Dictionary<string, PartitionStats> _partitions = new();
    private readonly LinkedList<RecentEvent> _recent = new();
    private readonly Dictionary<long, StreamRun> _runs = new();
    private readonly double[] _latencies = new double[256];
    private int _latencyCount, _latencyNext;
    private long _publishedEvents, _publishedRows, _processedEvents, _processedRows, _errorRows, _duplicates, _failures;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private struct Bucket { public long Second; public long EventsIn, EventsOut, RowsOut, Errors; }

    private ref Bucket Current(long second)
    {
        ref var b = ref _timeline[second % TimelineSeconds];
        if (b.Second != second) b = new Bucket { Second = second };
        return ref b;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ----- producer side -----

    public void RunStarted(long importId, string fileName, string supplier, long totalBytes, int chunkSize)
    {
        lock (_gate)
        {
            _runs[importId] = new StreamRun
            {
                ImportId = importId, FileName = fileName, Supplier = supplier,
                TotalBytes = totalBytes, ChunkSize = chunkSize, State = "Publishing", StartedUtc = DateTime.UtcNow
            };
            while (_runs.Count > RunCapacity)
            {
                var oldest = _runs.Values.Where(r => r.State != "Publishing").MinBy(r => r.StartedUtc);
                _runs.Remove(oldest?.ImportId ?? _runs.Keys.First());
            }
        }
    }

    public void Published(long importId, int events, long rows, long bytesRead)
    {
        lock (_gate)
        {
            _publishedEvents += events;
            _publishedRows += rows;
            Current(Now()).EventsIn += events;
            if (_runs.TryGetValue(importId, out var run))
            {
                run.PublishedEvents += events;
                run.PublishedRows += rows;
                run.BytesRead = bytesRead;
            }
        }
    }

    public void RunFinished(long importId, string state, string? error = null)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(importId, out var run)) return;
            run.State = state;
            run.Error = error;
            run.FinishedUtc = DateTime.UtcNow;
            if (state == "Published") run.BytesRead = run.TotalBytes;
        }
    }

    // ----- consumer side -----

    public void PartitionClaimed(string partitionId)
    {
        lock (_gate) { Partition(partitionId).Owned = true; }
    }

    public void PartitionReleased(string partitionId)
    {
        lock (_gate) { Partition(partitionId).Owned = false; }
    }

    public void Processed(string partitionId, long sequenceNumber, DateTimeOffset enqueuedUtc,
        long importId, int chunkIndex, int okRows, int errorRows, double durationMs, bool duplicate)
    {
        lock (_gate)
        {
            var p = Partition(partitionId);
            p.Owned = true;
            p.Events++;
            p.LastSequenceNumber = sequenceNumber;
            p.LastProcessedUtc = DateTime.UtcNow;
            _processedEvents++;
            ref var b = ref Current(Now());
            b.EventsOut++;
            if (duplicate)
            {
                _duplicates++;
            }
            else
            {
                p.Rows += okRows;
                p.ErrorRows += errorRows;
                _processedRows += okRows;
                _errorRows += errorRows;
                b.RowsOut += okRows;
                b.Errors += errorRows;
                if (_runs.TryGetValue(importId, out var run))
                {
                    run.ProcessedEvents++;
                    run.ProcessedRows += okRows;
                    run.ErrorRows += errorRows;
                }
            }

            var latency = Math.Max(0, (DateTimeOffset.UtcNow - enqueuedUtc).TotalMilliseconds);
            _latencies[_latencyNext] = latency;
            _latencyNext = (_latencyNext + 1) % _latencies.Length;
            _latencyCount = Math.Min(_latencyCount + 1, _latencies.Length);

            _recent.AddFirst(new RecentEvent(partitionId, sequenceNumber, importId, chunkIndex,
                okRows, errorRows, Math.Round(durationMs, 1), Math.Round(latency), duplicate, DateTime.UtcNow));
            if (_recent.Count > RecentCapacity) _recent.RemoveLast();
        }
    }

    public void ProcessingFailed(string partitionId)
    {
        lock (_gate)
        {
            _failures++;
            Partition(partitionId).Failures++;
        }
    }

    private PartitionStats Partition(string id)
    {
        if (!_partitions.TryGetValue(id, out var p))
            _partitions[id] = p = new PartitionStats { PartitionId = id };
        return p;
    }

    // ----- read model -----

    public TelemetrySnapshot Snapshot(IReadOnlyList<PartitionInfo> hubPartitions)
    {
        lock (_gate)
        {
            var now = Now();
            var timeline = new List<TimelinePoint>(TimelineSeconds);
            for (var s = now - TimelineSeconds + 1; s <= now; s++)
            {
                var b = _timeline[s % TimelineSeconds];
                timeline.Add(b.Second == s
                    ? new TimelinePoint(s, b.EventsIn, b.EventsOut, b.RowsOut, b.Errors)
                    : new TimelinePoint(s, 0, 0, 0, 0));
            }

            // Rates over the last 10 complete seconds (the current second is still filling).
            var window = timeline.Skip(timeline.Count - 11).Take(10).ToList();
            var rowsPerSec = window.Average(t => (double)t.RowsOut);
            var eventsInPerSec = window.Average(t => (double)t.EventsIn);
            var eventsOutPerSec = window.Average(t => (double)t.EventsOut);
            var peakRows = timeline.Max(t => t.RowsOut);

            var ids = hubPartitions.Select(p => p.PartitionId).Union(_partitions.Keys)
                .OrderBy(id => int.TryParse(id, out var n) ? n : int.MaxValue).ThenBy(id => id);
            var partitions = ids.Select(id =>
            {
                var hub = hubPartitions.FirstOrDefault(p => p.PartitionId == id);
                _partitions.TryGetValue(id, out var st);
                var last = hub?.LastEnqueuedSequenceNumber ?? -1;
                var done = st?.LastSequenceNumber ?? -1;
                var lag = hub is null || hub.IsEmpty ? 0 : Math.Max(0, last - done);
                return new PartitionView(id, st?.Owned ?? false, st?.Events ?? 0, st?.Rows ?? 0, st?.ErrorRows ?? 0,
                    st?.Failures ?? 0, last, done, lag, st?.LastProcessedUtc, hub?.LastEnqueuedTimeUtc);
            }).ToList();

            var lat = _latencies.Take(_latencyCount).OrderBy(x => x).ToArray();
            double Pct(double q) => lat.Length == 0 ? 0 : lat[Math.Min(lat.Length - 1, (int)(q * lat.Length))];

            return new TelemetrySnapshot(
                _startedUtc,
                new Totals(_publishedEvents, _publishedRows, _processedEvents, _processedRows, _errorRows, _duplicates, _failures),
                new Rates(Math.Round(eventsInPerSec, 1), Math.Round(eventsOutPerSec, 1), Math.Round(rowsPerSec), peakRows,
                    Math.Round(Pct(0.5)), Math.Round(Pct(0.95))),
                partitions.Sum(p => p.Lag),
                partitions,
                timeline,
                _recent.ToList(),
                _runs.Values.OrderByDescending(r => r.StartedUtc).Select(r => r.ToView()).ToList());
        }
    }

    private sealed class PartitionStats
    {
        public required string PartitionId;
        public bool Owned;
        public long Events, Rows, ErrorRows, Failures;
        public long LastSequenceNumber = -1;
        public DateTime? LastProcessedUtc;
    }

    private sealed class StreamRun
    {
        public long ImportId;
        public string FileName = "", Supplier = "", State = "";
        public string? Error;
        public long TotalBytes, BytesRead, PublishedEvents, PublishedRows, ProcessedEvents, ProcessedRows, ErrorRows;
        public int ChunkSize;
        public DateTime StartedUtc;
        public DateTime? FinishedUtc;

        public RunView ToView() => new(ImportId, FileName, Supplier, State, Error, TotalBytes, BytesRead, ChunkSize,
            PublishedEvents, PublishedRows, ProcessedEvents, ProcessedRows, ErrorRows, StartedUtc, FinishedUtc);
    }
}

public sealed record TelemetrySnapshot(DateTime StartedUtc, Totals Totals, Rates Rates, long TotalLag,
    IReadOnlyList<PartitionView> Partitions, IReadOnlyList<TimelinePoint> Timeline,
    IReadOnlyList<RecentEvent> Recent, IReadOnlyList<RunView> Runs);

public sealed record Totals(long PublishedEvents, long PublishedRows, long ProcessedEvents, long ProcessedRows,
    long ErrorRows, long DuplicateEvents, long Failures);

public sealed record Rates(double EventsInPerSec, double EventsOutPerSec, double RowsPerSec, long PeakRowsPerSec,
    double LatencyP50Ms, double LatencyP95Ms);

public sealed record PartitionView(string PartitionId, bool Owned, long Events, long Rows, long ErrorRows, long Failures,
    long LastEnqueuedSequenceNumber, long LastProcessedSequenceNumber, long Lag,
    DateTime? LastProcessedUtc, DateTimeOffset? LastEnqueuedUtc);

public sealed record TimelinePoint(long T, long EventsIn, long EventsOut, long RowsOut, long Errors);

public sealed record RecentEvent(string PartitionId, long SequenceNumber, long ImportId, int ChunkIndex,
    int Rows, int Errors, double DurationMs, double LatencyMs, bool Duplicate, DateTime AtUtc);

public sealed record RunView(long ImportId, string FileName, string Supplier, string State, string? Error,
    long TotalBytes, long BytesRead, int ChunkSize, long PublishedEvents, long PublishedRows,
    long ProcessedEvents, long ProcessedRows, long ErrorRows, DateTime StartedUtc, DateTime? FinishedUtc);
