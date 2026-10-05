using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Catalogue.Agents.FoundryLocal;

/// <summary>
/// IChatClient over Foundry Local's OpenAI-compatible endpoint. The endpoint and model id are resolved lazily
/// (and re-resolved after a restart moves the port or the model gets unloaded); one retry follows a
/// "model not loaded" or connection failure. SDK retries are off: a retried 2-minute generation is worse than an error.
/// </summary>
public sealed class FoundryLocalChatClient(FoundryLocalRuntime runtime, ILogger<FoundryLocalChatClient> log) : IChatClient
{
    private readonly object _sync = new();
    private (FoundryConnection Connection, IChatClient Client)? _current;

    /// <summary>
    /// An assistant turn that only calls tools is sent by the OpenAI SDK with <c>"content": null</c>. Foundry Local's
    /// Ministral 3 chat template drops the connection on that (measured: streaming request with a null-content
    /// assistant message → "connection was closed"; the same request with content "" works). Give such turns an
    /// explicit empty text so every model template accepts the history. Messages are copied, never mutated.
    /// </summary>
    internal static IList<ChatMessage> Normalize(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        if (!list.Any(NeedsContent)) return list;
        return list.Select(m => NeedsContent(m) ? new ChatMessage(m.Role, [new TextContent(" "), .. m.Contents])
            { AuthorName = m.AuthorName, MessageId = m.MessageId, AdditionalProperties = m.AdditionalProperties } : m).ToList();

        static bool NeedsContent(ChatMessage m) => m.Role == ChatRole.Assistant
            && m.Contents.OfType<FunctionCallContent>().Any()
            && !m.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text));
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = Normalize(messages);
        for (var attempt = 0; ; attempt++)
        {
            var client = await ClientAsync(cancellationToken);
            try { return await client.GetResponseAsync(list, options, cancellationToken); }
            catch (Exception ex) when (attempt == 0 && IsRecoverable(ex)) { Recover(ex); }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = Normalize(messages);
        for (var attempt = 0; ; attempt++)
        {
            var client = await ClientAsync(cancellationToken);
            await using var e = client.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            bool hasFirst;
            try { hasFirst = await e.MoveNextAsync(); }
            catch (Exception ex) when (attempt == 0 && IsRecoverable(ex)) { Recover(ex); continue; } // only before anything was yielded
            if (!hasFirst) yield break;
            yield return e.Current;
            while (await e.MoveNextAsync()) yield return e.Current;
            yield break;
        }
    }

    private async Task<IChatClient> ClientAsync(CancellationToken ct)
    {
        var connection = await runtime.EnsureReadyAsync(ct);
        lock (_sync)
        {
            if (_current is { } c && c.Connection == connection) return c.Client;
            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(connection.Endpoint, "v1"),
                NetworkTimeout = runtime.Options.RequestTimeout,
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
            };
            // Foundry Local ignores the key, but the SDK requires one.
            var client = new OpenAI.Chat.ChatClient(connection.ModelId, new ApiKeyCredential("foundry-local"), options).AsIChatClient();
            (_current?.Client as IDisposable)?.Dispose();
            _current = (connection, client);
            log.LogInformation("Using Foundry Local model {Model} at {Endpoint}", connection.ModelId, connection.Endpoint);
            return client;
        }
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is HttpRequestException
        || (ex is ClientResultException cre && (cre.Status == 0 || cre.Message.Contains("not loaded", StringComparison.OrdinalIgnoreCase)));

    private void Recover(Exception ex)
    {
        log.LogWarning("Foundry Local call failed ({Error}); re-checking the service and model", ex.Message);
        runtime.Invalidate();
        lock (_sync) _current = null;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(FoundryLocalChatClient) ? this : _current?.Client.GetService(serviceType, serviceKey);

    public void Dispose() => (_current?.Client as IDisposable)?.Dispose();
}

/// <summary>
/// Converts text-encoded tool calls and reasoning from local models into Microsoft.Extensions.AI content
/// (<see cref="FunctionCallContent"/>, <see cref="TextReasoningContent"/>), so FunctionInvokingChatClient and
/// Agent Framework work with them exactly as with a hosted model.
/// </summary>
public sealed class TextToolCallChatClient(IChatClient inner, bool startsInReasoning = true) : DelegatingChatClient(inner)
{
    private static IReadOnlyCollection<string> ToolNames(ChatOptions? options) =>
        options?.Tools is { Count: > 0 } tools ? tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) : [];

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        var sawCall = false;
        var names = ToolNames(options);
        foreach (var message in response.Messages.Where(m => m.Role == ChatRole.Assistant))
        {
            if (message.Contents.OfType<FunctionCallContent>().Any()) continue; // the server did parse it
            var parsed = TextToolCallParser.Parse(string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text)),
                truncated: Truncated(response.FinishReason, response.Usage?.OutputTokenCount, options), names, startsInReasoning);
            var others = message.Contents.Where(c => c is not TextContent).ToList();
            message.Contents.Clear();
            if (parsed.Reasoning is { } r) message.Contents.Add(new TextReasoningContent(r));
            if (parsed.Text.Length > 0) message.Contents.Add(new TextContent(parsed.Text));
            foreach (var call in parsed.ToolCalls) { message.Contents.Add(ToContent(call)); sawCall = true; }
            foreach (var c in others) message.Contents.Add(c);
        }
        if (sawCall) response.FinishReason = ChatFinishReason.ToolCalls;
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parser = new StreamingToolCallParser(startsInReasoning);
        ChatResponseUpdate? template = null;
        ChatFinishReason? finish = null;
        long? outputTokens = null;
        var sawCall = false;
        // Non-thinking models (Ministral) start a tool call straight away as plain text ("list_orders{...}"). Hold the
        // first characters back until it is clear whether this is a call (buffer to the end) or an answer (stream on).
        var names = ToolNames(options);
        var hold = !startsInReasoning && names.Count > 0 ? new StringBuilder() : null;
        var bareCall = false;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            template ??= update;
            finish = update.FinishReason ?? finish;
            if (update.Contents.OfType<UsageContent>().LastOrDefault() is { } usage) outputTokens = usage.Details.OutputTokenCount ?? outputTokens;
            if (update.Contents.OfType<FunctionCallContent>().Any()) { yield return update; continue; }
            var text = string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
            var others = update.Contents.Where(c => c is not TextContent).ToList();
            if (hold is not null && text.Length > 0)
            {
                hold.Append(text);
                text = "";
                if (!bareCall)
                {
                    switch (TextToolCallParser.LooksLikeBareCall(hold.ToString(), names))
                    {
                        case true: bareCall = true; break;
                        case false: text = hold.ToString(); hold = null; break; // an answer: release and stream normally
                    }
                }
            }
            var pieces = text.Length > 0 ? parser.Push(text).ToList() : [];
            if (pieces.Count > 0 || others.Count > 0)
            {
                var u = Clone(update, ToContents(pieces, ref sawCall).Concat(others));
                if (u.FinishReason is not null && sawCall) u.FinishReason = ChatFinishReason.ToolCalls;
                yield return u;
            }
        }
        if (hold is { Length: > 0 })
        {
            if (TextToolCallParser.TryParseBareCalls(hold.ToString(), names, out var calls) && template is not null)
            {
                var u = Clone(template, calls.Select(c => (AIContent)ToContent(c)));
                u.FinishReason = ChatFinishReason.ToolCalls;
                yield return u;
                yield break;
            }
            foreach (var p in parser.Push(hold.ToString()).ToList()) // looked like a call but wasn't: it is text after all
                if (template is not null) yield return Clone(template, ToContents([p], ref sawCall));
        }
        var tail = parser.Complete(truncated: Truncated(finish, outputTokens, options)).ToList();
        if (tail.Count > 0 && template is not null)
        {
            var u = Clone(template, ToContents(tail, ref sawCall));
            if (sawCall) u.FinishReason = ChatFinishReason.ToolCalls;
            yield return u;
        }
    }

    private static List<AIContent> ToContents(List<StreamingToolCallParser.Piece> pieces, ref bool sawCall)
    {
        var list = new List<AIContent>(pieces.Count);
        foreach (var p in pieces)
        {
            switch (p.Kind)
            {
                case StreamingToolCallParser.Kind.Reasoning: list.Add(new TextReasoningContent(p.Text!)); break;
                case StreamingToolCallParser.Kind.Text: list.Add(new TextContent(p.Text!)); break;
                default: list.Add(ToContent(p.Call!)); sawCall = true; break;
            }
        }
        return list;
    }

    private static ChatResponseUpdate Clone(ChatResponseUpdate source, IEnumerable<AIContent> contents) => new(ChatRole.Assistant, contents.ToList())
    {
        ResponseId = source.ResponseId,
        MessageId = source.MessageId,
        ConversationId = source.ConversationId,
        ModelId = source.ModelId,
        CreatedAt = source.CreatedAt,
        FinishReason = source.FinishReason,
        AdditionalProperties = source.AdditionalProperties
    };

    /// <summary>
    /// Foundry Local reports finish_reason "stop" even when it cut the output at max_completion_tokens, so a
    /// generation that used its whole token budget is treated as cut off as well.
    /// </summary>
    internal static bool Truncated(ChatFinishReason? finish, long? outputTokens, ChatOptions? options) =>
        finish == ChatFinishReason.Length || (options?.MaxOutputTokens is { } max && outputTokens >= max);

    internal static FunctionCallContent ToContent(ParsedToolCall call) =>
        new($"call_{Guid.NewGuid():N}"[..17], call.Name, call.Arguments);
}
