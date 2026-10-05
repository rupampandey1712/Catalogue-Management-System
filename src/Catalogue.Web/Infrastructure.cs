using Polly;
using Catalogue.Application.Streaming;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Catalogue.Web;

public static class Policies
{
    public const string LongRunning = "long-running";
    public const string Login = "login";
    public const string Register = "register";
    /// <summary>Assistant questions and reviews, per user: each one occupies the local GPU for minutes.</summary>
    public const string Agent = "agent";
}

/// <summary>Cache keys and tags; writes invalidate by tag so every API instance sees fresh data.</summary>
public static class CacheTags
{
    public const string Categories = "categories";
    public const string Suppliers = "suppliers";
    public const string Products = "products";
    public const string Dashboard = "dashboard";
    public static string Product(long id) => $"product:{id}";
}

/// <summary>
/// Fault-tolerant L2 cache: wraps Redis with a short timeout and a circuit breaker. When Redis is
/// down the cache "fails open" - reads become misses, writes are skipped - and after a few failures
/// the breaker stops calling Redis entirely for 30s, so requests don't pay a connect timeout each.
/// </summary>
public sealed class ResilientDistributedCache(Microsoft.Extensions.Caching.Distributed.IDistributedCache inner,
    ILogger<ResilientDistributedCache> logger) : Microsoft.Extensions.Caching.Distributed.IDistributedCache
{
    private readonly Polly.ResiliencePipeline _pipeline = new Polly.ResiliencePipelineBuilder()
        .AddCircuitBreaker(new Polly.CircuitBreaker.CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30),
            OnOpened = a => { logger.LogWarning("Redis circuit OPEN for {Seconds}s; serving from L1 + database.", a.BreakDuration.TotalSeconds); return default; },
            OnClosed = _ => { logger.LogInformation("Redis circuit closed; distributed cache restored."); return default; }
        })
        .AddTimeout(TimeSpan.FromMilliseconds(500))
        .Build();

    private async Task<T?> Try<T>(Func<CancellationToken, Task<T?>> op, CancellationToken ct)
    {
        try { return await _pipeline.ExecuteAsync(async t => await op(t), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (ex is not Polly.CircuitBreaker.BrokenCircuitException)
                logger.LogDebug("Distributed cache unavailable: {Message}", ex.Message);
            return default;
        }
    }

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Try(t => inner.GetAsync(key, t), token);
    public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) => SetAsync(key, value, options).GetAwaiter().GetResult();
    public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default)
        => Try<object>(async t => { await inner.SetAsync(key, value, options, t); return null; }, token);
    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();
    public Task RefreshAsync(string key, CancellationToken token = default)
        => Try<object>(async t => { await inner.RefreshAsync(key, t); return null; }, token);
    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();
    public Task RemoveAsync(string key, CancellationToken token = default)
        => Try<object>(async t => { await inner.RemoveAsync(key, t); return null; }, token);
}

/// <summary>
/// Reports the Event Hub as Degraded (not Unhealthy) when unreachable: the catalogue API can still
/// serve reads and file imports, so an Event Hub outage should not pull it out of the load balancer.
/// </summary>
public sealed class EventHubHealthCheck(IEventHubProducer hub) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var partitions = await hub.GetPartitionsAsync(timeout.Token);
            return HealthCheckResult.Healthy($"{hub.Mode} hub '{hub.HubName}' with {partitions.Count} partitions");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"{hub.Mode} hub unreachable", ex);
        }
    }
}
