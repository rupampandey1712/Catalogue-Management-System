namespace Catalogue.Domain;

/// <summary>A chat between one user and the operations assistant. The agent's session state is stored with it.</summary>
public sealed class AgentConversation
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public long UserId { get; private set; }
    public string UserName { get; private set; } = null!;
    public string Title { get; private set; } = "New conversation";
    /// <summary>Serialized Agent Framework session (chat history), restored on the next message.</summary>
    public string? SessionState { get; private set; }
    public int MessageCount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; private set; } = DateTime.UtcNow;

    private AgentConversation() { }

    public AgentConversation(long userId, string userName)
    {
        UserId = userId;
        UserName = userName;
    }

    public void Record(string? sessionState, int addedMessages, string? firstQuestion)
    {
        SessionState = sessionState ?? SessionState;
        MessageCount += addedMessages;
        if (Title == "New conversation" && !string.IsNullOrWhiteSpace(firstQuestion))
        {
            var t = firstQuestion.Trim().ReplaceLineEndings(" ");
            Title = t.Length > 80 ? t[..77] + "..." : t;
        }
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

public sealed class AgentMessage
{
    public long Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public string Role { get; private set; } = null!;
    public string Content { get; private set; } = "";
    public string? Reasoning { get; private set; }
    /// <summary>JSON array of {name, arguments, result} for the tools the agent called.</summary>
    public string? ToolTrace { get; private set; }
    public string? Error { get; private set; }
    public int? DurationMs { get; private set; }
    public int? InputTokens { get; private set; }
    public int? OutputTokens { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;

    private AgentMessage() { }

    public static AgentMessage User(Guid conversationId, string text) => new() { ConversationId = conversationId, Role = "user", Content = text };

    public static AgentMessage Assistant(Guid conversationId, string text, string? reasoning, string? toolTrace, string? error,
        int durationMs, int? inputTokens, int? outputTokens) => new()
    {
        ConversationId = conversationId, Role = "assistant", Content = text,
        Reasoning = reasoning is { Length: > 8000 } ? reasoning[..8000] : reasoning,
        ToolTrace = toolTrace, Error = error, DurationMs = durationMs, InputTokens = inputTokens, OutputTokens = outputTokens
    };
}

/// <summary>Things the agent may suggest. It never performs them; a person with the right permission approves.</summary>
public enum AgentActionKind { ReprocessImport, ApproveChange, RejectChange, AcceptOrder, RejectOrder, ShipOrder, CancelOrder }

public enum ProposalStatus { Pending, Executed, Rejected, Failed, Expired }

/// <summary>
/// An action proposed by the assistant or the operations review. Persisted so it survives restarts and can be
/// approved by any authorized person; executed by deterministic code with the approver's permissions.
/// </summary>
public sealed class AgentActionProposal
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(3);

    public long Id { get; private set; }
    public AgentActionKind Kind { get; private set; }
    public string TargetId { get; private set; } = null!;
    public string TargetLabel { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public string Source { get; private set; } = null!;
    public Guid? ConversationId { get; private set; }
    public long? ReviewId { get; private set; }
    public string RequestedBy { get; private set; } = null!;
    public ProposalStatus Status { get; private set; } = ProposalStatus.Pending;
    public string? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? Result { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; private set; }

    private AgentActionProposal() { }

    public AgentActionProposal(AgentActionKind kind, string targetId, string targetLabel, string reason, string source,
        string requestedBy, Guid? conversationId = null, long? reviewId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        Kind = kind;
        TargetId = targetId.Trim();
        TargetLabel = Clip(targetLabel, 200);
        Reason = Clip(string.IsNullOrWhiteSpace(reason) ? "No reason given." : reason.Trim(), 500);
        Source = source;
        RequestedBy = requestedBy;
        ConversationId = conversationId;
        ReviewId = reviewId;
        ExpiresAtUtc = CreatedAtUtc.Add(Lifetime);
    }

    public bool IsOpen(DateTime now) => Status == ProposalStatus.Pending && ExpiresAtUtc > now;

    public void Executed(string by, string result, DateTime now) => Close(ProposalStatus.Executed, by, result, now);
    public void Rejected(string by, string? note, DateTime now) => Close(ProposalStatus.Rejected, by, note ?? "Rejected", now);
    public void Failed(string by, string error, DateTime now) => Close(ProposalStatus.Failed, by, error, now);
    public void Expire(DateTime now) { if (Status == ProposalStatus.Pending) { Status = ProposalStatus.Expired; DecidedAtUtc = now; } }

    private void Close(ProposalStatus status, string by, string result, DateTime now)
    {
        if (Status != ProposalStatus.Pending) throw new InvalidOperationException($"Proposal {Id} is already {Status.ToString().ToLowerInvariant()}.");
        if (ExpiresAtUtc <= now) throw new InvalidOperationException($"Proposal {Id} has expired.");
        Status = status;
        DecidedBy = by;
        DecidedAtUtc = now;
        Result = Clip(result, 500);
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}

public enum ReviewStatus { Running, Completed, Failed }

/// <summary>One run of the operations review workflow: findings from every area plus the model's briefing.</summary>
public sealed class OperationsReview
{
    public long Id { get; private set; }
    public string Trigger { get; private set; } = null!;
    public string RequestedBy { get; private set; } = null!;
    public ReviewStatus Status { get; private set; } = ReviewStatus.Running;
    public DateTime StartedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; private set; }
    public string? Briefing { get; private set; }
    public bool GeneratedByModel { get; private set; }
    public string? ModelId { get; private set; }
    public string FindingsJson { get; private set; } = "[]";
    public string? SnapshotJson { get; private set; }
    public int Critical { get; private set; }
    public int Warnings { get; private set; }
    public int Infos { get; private set; }
    public int ProposalCount { get; private set; }
    public string? Error { get; private set; }
    public int? DurationMs { get; private set; }

    private OperationsReview() { }

    public OperationsReview(string trigger, string requestedBy)
    {
        Trigger = trigger;
        RequestedBy = requestedBy;
    }

    public void Complete(string briefing, bool byModel, string? modelId, string findingsJson, string snapshotJson,
        int critical, int warnings, int infos, int proposals, string? modelError, DateTime now)
    {
        Status = ReviewStatus.Completed;
        Briefing = briefing;
        GeneratedByModel = byModel;
        ModelId = modelId;
        FindingsJson = findingsJson;
        SnapshotJson = snapshotJson;
        (Critical, Warnings, Infos, ProposalCount) = (critical, warnings, infos, proposals);
        Error = modelError is { Length: > 500 } ? modelError[..500] : modelError;
        CompletedAtUtc = now;
        DurationMs = (int)(now - StartedAtUtc).TotalMilliseconds;
    }

    public void Fail(string error, DateTime now)
    {
        Status = ReviewStatus.Failed;
        Error = error.Length > 500 ? error[..500] : error;
        CompletedAtUtc = now;
        DurationMs = (int)(now - StartedAtUtc).TotalMilliseconds;
    }
}
