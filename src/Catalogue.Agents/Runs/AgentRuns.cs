using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Catalogue.Agents.Runs;

public enum AgentRunStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed record AgentRunEvent(int Seq, string Type, object? Data, DateTime AtUtc);

/// <summary>
/// One queued unit of model work (a chat turn or an operations review). Its events are buffered so a client
/// can (re)connect at any time and replay from a sequence number; the run itself never depends on the HTTP request.
/// </summary>
public sealed class AgentRun
{
    private readonly object _sync = new();
    private readonly List<AgentRunEvent> _events = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid Id { get; } = Guid.NewGuid();
    public required string Kind { get; init; }
    public required long UserId { get; init; }
    public Guid? ConversationId { get; init; }
    public required Func<AgentRun, CancellationToken, Task> Work { get; init; }
    public AgentRunStatus Status { get; private set; } = AgentRunStatus.Queued;
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public string? Error { get; private set; }
    internal CancellationTokenSource Cancellation { get; } = new();

    public bool IsFinished => Status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled;

    public void Publish(string type, object? data = null)
    {
        lock (_sync)
        {
            _events.Add(new AgentRunEvent(_events.Count + 1, type, data, DateTime.UtcNow));
            var previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    internal void MarkRunning() { Status = AgentRunStatus.Running; StartedAtUtc = DateTime.UtcNow; Publish("started"); }

    internal void Finish(AgentRunStatus status, string? error)
    {
        // Status and the "done" event change together under the lock readers use; otherwise a reader could see
        // the run as finished before "done" is in the buffer and stop without delivering it.
        lock (_sync)
        {
            Status = status;
            Error = error;
            CompletedAtUtc = DateTime.UtcNow;
            Publish("done", new { status = status.ToString(), error });
        }
    }

    public void Cancel() => Cancellation.Cancel();

    /// <summary>Replays events after <paramref name="afterSeq"/>, then follows live ones until the run ends.</summary>
    public async IAsyncEnumerable<AgentRunEvent> ReadAsync(int afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        var next = afterSeq;
        while (!ct.IsCancellationRequested)
        {
            AgentRunEvent[] batch;
            Task changed;
            bool finished;
            lock (_sync)
            {
                batch = _events.Skip(next).ToArray();
                changed = _changed.Task;
                finished = IsFinished;
            }
            foreach (var e in batch) { next = e.Seq; yield return e; }
            if (finished && batch.Length == 0) yield break;
            if (batch.Length == 0)
            {
                var timedOut = false;
                try { await changed.WaitAsync(TimeSpan.FromSeconds(15), ct); }
                catch (TimeoutException) { timedOut = true; }
                // Keeps proxies from closing an idle stream while the model is still reading the prompt.
                if (timedOut) yield return new AgentRunEvent(next, "heartbeat", null, DateTime.UtcNow);
            }
        }
    }
}

public sealed class AgentBusyException(string message) : Exception(message);

/// <summary>
/// Serializes model work: a local GPU runs one generation at a time, so runs queue (bounded) and report their
/// position. Each user may have one run waiting or running; finished runs are kept for 30 minutes for replay.
/// </summary>
public sealed class AgentRunCoordinator(AgentOptions options)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
    private readonly Channel<AgentRun> _queue = Channel.CreateUnbounded<AgentRun>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, AgentRun> _runs = new();
    private readonly LinkedList<AgentRun> _waiting = new();
    private readonly object _sync = new();

    public AgentOptions Options => options;
    internal ChannelReader<AgentRun> Reader => _queue.Reader;

    public AgentRun Enqueue(string kind, long userId, Guid? conversationId, Func<AgentRun, CancellationToken, Task> work)
    {
        Evict();
        var run = new AgentRun { Kind = kind, UserId = userId, ConversationId = conversationId, Work = work };
        lock (_sync)
        {
            if (_waiting.Count >= options.MaxQueue)
                throw new AgentBusyException($"The assistant queue is full ({options.MaxQueue} waiting). Try again in a few minutes.");
            if (userId != 0 && _runs.Values.Any(r => r.UserId == userId && !r.IsFinished))
                throw new AgentBusyException("You already have a request in progress. Wait for it to finish or cancel it.");
            _runs[run.Id] = run;
            _waiting.AddLast(run);
            run.Publish("queued", new { position = _waiting.Count });
        }
        _queue.Writer.TryWrite(run);
        return run;
    }

    public AgentRun? Get(Guid id) => _runs.GetValueOrDefault(id);

    public IReadOnlyList<AgentRun> Active() => _runs.Values.Where(r => !r.IsFinished).OrderBy(r => r.CreatedAtUtc).ToList();

    public int Waiting { get { lock (_sync) return _waiting.Count; } }

    internal void Dequeued(AgentRun run)
    {
        lock (_sync)
        {
            _waiting.Remove(run);
            var position = 0;
            foreach (var w in _waiting) w.Publish("queued", new { position = ++position });
        }
    }

    private void Evict()
    {
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var r in _runs.Values.Where(r => r.IsFinished && r.CompletedAtUtc < cutoff)) _runs.TryRemove(r.Id, out _);
    }
}

/// <summary>Executes queued runs one at a time with a timeout; failures become events, never crashes.</summary>
public sealed class AgentRunWorker(AgentRunCoordinator coordinator, ILogger<AgentRunWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var run in coordinator.Reader.ReadAllAsync(stoppingToken))
        {
            coordinator.Dequeued(run);
            if (run.Cancellation.IsCancellationRequested) { run.Finish(AgentRunStatus.Cancelled, "Cancelled before it started."); continue; }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, run.Cancellation.Token);
            cts.CancelAfter(coordinator.Options.RunTimeout);
            run.MarkRunning();
            try
            {
                await run.Work(run, cts.Token);
                run.Finish(AgentRunStatus.Completed, null);
            }
            catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
            {
                run.Finish(AgentRunStatus.Cancelled, "Cancelled.");
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                run.Finish(AgentRunStatus.Failed, $"Timed out after {coordinator.Options.RunTimeout.TotalMinutes:0} minutes.");
            }
            catch (OperationCanceledException) { run.Finish(AgentRunStatus.Cancelled, "Server is shutting down."); }
            catch (Exception ex)
            {
                log.LogError(ex, "Agent run {RunId} ({Kind}) failed", run.Id, run.Kind);
                run.Finish(AgentRunStatus.Failed, ex is FoundryLocal.FoundryLocalException ? ex.Message : "The assistant failed: " + ex.Message);
            }
        }
    }
}
