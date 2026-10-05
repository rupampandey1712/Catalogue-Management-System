using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Catalogue.Application;

/// <summary>
/// Ambient correlation ID that flows with the async call chain (HTTP request, queue message,
/// Event Hub chunk). Logs, traces, outbound HTTP calls and persisted records all read from here,
/// so one ID ties a browser click to every log line and span it caused - across processes.
/// </summary>
public static class CorrelationContext
{
    public const string HeaderName = "X-Correlation-ID";
    public const string LogProperty = "CorrelationId";
    public const string TagName = "correlation.id";

    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current => _current.Value;

    /// <summary>Sets the ID for the current async flow; dispose to restore the previous one.</summary>
    public static IDisposable Begin(string? correlationId)
    {
        var previous = _current.Value;
        var id = Normalize(correlationId) ?? NewId();
        _current.Value = id;
        Activity.Current?.SetTag(TagName, id);
        Activity.Current?.SetBaggage(TagName, id);
        return new Restore(previous);
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Accepts caller-supplied IDs only if they are short and safe to log/echo.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 64) return null;
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) return null;
        return value;
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}

/// <summary>Custom traces and metrics for the ingestion pipelines (exported via OpenTelemetry).</summary>
public static class CatalogueDiagnostics
{
    public const string SourceName = "Catalogue.Ingestion";

    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
    public static readonly Meter Meter = new(SourceName, "1.0.0");

    public static readonly Counter<long> RowsIngested =
        Meter.CreateCounter<long>("catalogue.ingestion.rows", "{row}", "Rows written to the catalogue.");
    public static readonly Counter<long> RowsRejected =
        Meter.CreateCounter<long>("catalogue.ingestion.rows_rejected", "{row}", "Rows rejected by validation.");
    public static readonly Counter<long> ChunksProcessed =
        Meter.CreateCounter<long>("catalogue.ingestion.chunks", "{chunk}", "Event Hub chunks consumed.");
    public static readonly Counter<long> ChunksPublished =
        Meter.CreateCounter<long>("catalogue.ingestion.chunks_published", "{chunk}", "Event Hub chunks published.");
    public static readonly Counter<long> ChunkFailures =
        Meter.CreateCounter<long>("catalogue.ingestion.chunk_failures", "{failure}", "Chunk processing attempts that failed.");
    public static readonly Histogram<double> ChunkDuration =
        Meter.CreateHistogram<double>("catalogue.ingestion.chunk.duration", "ms", "Time to parse, validate and apply one chunk.");
    public static readonly Histogram<double> EndToEndLatency =
        Meter.CreateHistogram<double>("catalogue.ingestion.e2e_latency", "ms", "Enqueue-to-applied latency of a chunk.");
    public static readonly Counter<long> ImportsCompleted =
        Meter.CreateCounter<long>("catalogue.imports.completed", "{import}", "Imports reaching a terminal state.");

    /// <summary>W3C traceparent of the current span, for carrying across queues/hubs.</summary>
    public static string? CurrentTraceParent => Activity.Current?.Id;

    /// <summary>Starts a consumer span that continues the producer's trace when a traceparent is known.</summary>
    public static Activity? StartConsumer(string name, string? traceParent, string? correlationId)
    {
        ActivityContext.TryParse(traceParent, null, out var parent);
        var activity = ActivitySource.StartActivity(name, ActivityKind.Consumer, parent);
        if (correlationId is not null) activity?.SetTag(CorrelationContext.TagName, correlationId);
        return activity;
    }
}
