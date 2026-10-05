using Azure;
using Azure.Messaging.EventHubs;
using Azure.Messaging.ServiceBus;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Catalogue.Infrastructure.Resilience;

/// <summary>
/// Classifies failures as transient (retry / wait it out) or permanent (fail fast / dead-letter).
/// The distinction matters for the Event Hub consumers: a database outage must not dead-letter
/// valid chunks, while a genuinely poison chunk must not block its partition forever.
/// </summary>
public static class TransientFaults
{
    // Deadlock, lock timeout, timeouts, Azure SQL throttling/failover, network errors.
    private static readonly HashSet<int> TransientSqlNumbers =
        [1205, 1222, -2, 2, 53, 64, 121, 233, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 11001,
         40143, 40197, 40501, 40540, 40613, 49918, 49919, 49920];

    // Concurrent insert of the same key - safe to retry because the whole transaction rolled back.
    private static readonly HashSet<int> ConflictSqlNumbers = [2627, 2601];

    public static bool IsSqlConflict(Exception ex) => ex is SqlException s && s.Errors.Cast<SqlError>().Any(e => ConflictSqlNumbers.Contains(e.Number));

    public static bool IsTransient(Exception ex) => ex switch
    {
        BrokenCircuitException or TimeoutRejectedException or TimeoutException => true,
        SqlException s => s.Errors.Cast<SqlError>().Any(e => TransientSqlNumbers.Contains(e.Number)),
        EventHubsException eh => eh.IsTransient,
        ServiceBusException sb => sb.IsTransient,
        RequestFailedException rf => rf.Status is 0 or 408 or 429 or >= 500,
        HttpRequestException or IOException or System.Net.Sockets.SocketException => true,
        AggregateException ag => ag.InnerExceptions.Any(IsTransient),
        _ when ex.InnerException is not null => IsTransient(ex.InnerException),
        _ => false
    };

    /// <summary>
    /// Pipeline for one set-based SQL write: retry conflicts/transients with jittered backoff,
    /// a circuit breaker that stops hammering a database that is down, and a hard timeout.
    /// </summary>
    public static ResiliencePipeline SqlWritePipeline(Action<string>? onEvent = null) => new ResiliencePipelineBuilder
        {
            Name = "sql-bulk-write"
        }
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<SqlException>(e => IsSqlConflict(e) || IsTransient(e)),
            MaxRetryAttempts = 6,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(150),
            MaxDelay = TimeSpan.FromSeconds(5),
            OnRetry = a =>
            {
                onEvent?.Invoke($"retry {a.AttemptNumber + 1} after {a.RetryDelay.TotalMilliseconds:0} ms: {a.Outcome.Exception?.Message}");
                return default;
            }
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(e => IsTransient(e) && !IsSqlConflict(e)),
            FailureRatio = 0.5,
            MinimumThroughput = 10,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(20),
            OnOpened = a => { onEvent?.Invoke($"circuit OPEN for {a.BreakDuration.TotalSeconds:0}s"); return default; },
            OnClosed = _ => { onEvent?.Invoke("circuit closed"); return default; },
            OnHalfOpened = _ => { onEvent?.Invoke("circuit half-open (probing)"); return default; }
        })
        .AddTimeout(TimeSpan.FromMinutes(2))
        .Build();
}
