using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Catalogue.Agents.FoundryLocal;

public sealed record ParsedToolCall(string Name, Dictionary<string, object?> Arguments);

public sealed record ParsedModelOutput(string? Reasoning, string Text, IReadOnlyList<ParsedToolCall> ToolCalls);

/// <summary>
/// Local models served by Foundry Local (Qwen 3.x here) return tool calls and their reasoning as plain text
/// instead of OpenAI <c>tool_calls</c>. This turns that text into structure. Two tool-call dialects are accepted:
/// <code>
/// &lt;tool_call&gt;&lt;function=name&gt;&lt;parameter=status&gt;Submitted&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;   (Qwen 3.5 / Coder XML)
/// &lt;tool_call&gt;{"name": "name", "arguments": {"status": "Submitted"}}&lt;/tool_call&gt;                (Hermes / Qwen 2.5-3 JSON)
/// </code>
/// Reasoning is everything before the last <c>&lt;/think&gt;</c> (the chat template opens the think block itself).
/// </summary>
public static partial class TextToolCallParser
{
    public const string ThinkOpen = "<think>";
    public const string ThinkClose = "</think>";
    public const string CallOpen = "<tool_call>";
    public const string CallClose = "</tool_call>";

    /// <param name="truncated">The generation hit its token limit (finish reason "length").</param>
    /// <param name="toolNames">Tools offered on this request; enables the bare Mistral-style dialect (<c>name{json}</c>).</param>
    /// <param name="thinking">The model reasons before answering (Qwen 3.x, DeepSeek-R1); its template opens the think block.</param>
    public static ParsedModelOutput Parse(string? raw, bool truncated = false, IReadOnlyCollection<string>? toolNames = null, bool thinking = true)
    {
        raw ??= "";
        string? reasoning = null;
        var rest = raw;
        var close = raw.LastIndexOf(ThinkClose, StringComparison.Ordinal);
        if (close >= 0)
        {
            reasoning = raw[..close].Replace(ThinkOpen, "", StringComparison.Ordinal).Replace(ThinkClose, "", StringComparison.Ordinal).Trim();
            rest = raw[(close + ThinkClose.Length)..];
        }
        else if ((truncated && thinking) || raw.TrimStart().StartsWith(ThinkOpen, StringComparison.Ordinal))
        {
            // Unclosed think block (the template may have opened it): the model ran out of tokens while reasoning.
            reasoning = raw.Replace(ThinkOpen, "", StringComparison.Ordinal).Trim();
            rest = "";
        }

        var calls = ExtractCalls(rest, out var text);
        if (calls.Count == 0 && text.Length == 0 && reasoning?.Contains(CallOpen, StringComparison.Ordinal) == true)
            calls = ExtractCalls(reasoning, out _); // tool call emitted without closing the think block
        if (calls.Count == 0 && text.Length > 0 && toolNames is { Count: > 0 } && TryParseBareCalls(text, toolNames, out var bare))
        {
            calls = bare;
            text = "";
        }
        return new ParsedModelOutput(string.IsNullOrWhiteSpace(reasoning) ? null : reasoning, text, calls);
    }

    public const string MistralCalls = "[TOOL_CALLS]";
    public const string MistralArgs = "[ARGS]";

    /// <summary>
    /// Mistral-family models (Ministral 3 on Foundry Local) emit calls as plain text once the special tokens are
    /// stripped: <c>list_orders{"status": "Submitted"}list_imports{"status": "failed"}</c>, optionally with
    /// <c>[TOOL_CALLS]</c> / <c>[ARGS]</c> markers, or as a JSON array <c>[{"name": …, "arguments": {…}}]</c>.
    /// Succeeds only when the whole text is calls to tools that were offered, so normal prose is never misread.
    /// </summary>
    public static bool TryParseBareCalls(string text, IReadOnlyCollection<string> toolNames, out List<ParsedToolCall> calls)
    {
        calls = [];
        var s = text.Trim();
        if (s.StartsWith(MistralCalls, StringComparison.Ordinal)) s = s[MistralCalls.Length..].TrimStart();
        if (s.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
                foreach (var item in doc.RootElement.EnumerateArray())
                    if (ParseCall(item.GetRawText()) is { } c && toolNames.Contains(c.Name)) calls.Add(c);
                    else { calls.Clear(); return false; }
                return calls.Count > 0;
            }
            catch (JsonException) { return false; }
        }
        var pos = 0;
        while (pos < s.Length)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            if (pos >= s.Length) break;
            if (string.CompareOrdinal(s, pos, MistralCalls, 0, MistralCalls.Length) == 0) { pos += MistralCalls.Length; continue; }
            var name = toolNames.Where(n => string.CompareOrdinal(s, pos, n, 0, n.Length) == 0).OrderByDescending(n => n.Length).FirstOrDefault();
            if (name is null) { calls.Clear(); return false; }
            pos += name.Length;
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            if (string.CompareOrdinal(s, pos, MistralArgs, 0, MistralArgs.Length) == 0) pos += MistralArgs.Length;
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            var end = JsonObjectEnd(s, pos);
            if (end < 0) { calls.Clear(); return false; }
            try
            {
                using var doc = JsonDocument.Parse(s[pos..end]);
                var args = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in doc.RootElement.EnumerateObject()) args[p.Name] = ToClr(p.Value);
                calls.Add(new ParsedToolCall(name, args));
            }
            catch (JsonException) { calls.Clear(); return false; }
            pos = end;
        }
        return calls.Count > 0;
    }

    /// <summary>Index just past the JSON object starting at <paramref name="start"/> (string- and escape-aware), or -1.</summary>
    private static int JsonObjectEnd(string s, int start)
    {
        if (start >= s.Length || s[start] != '{') return -1;
        var depth = 0;
        var inString = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i + 1;
        }
        return -1;
    }

    /// <summary>
    /// For streaming: decides from the first characters whether a non-thinking model is emitting a bare tool call.
    /// Null = not decided yet (the text could still be the start of a call).
    /// </summary>
    public static bool? LooksLikeBareCall(string soFar, IReadOnlyCollection<string> toolNames)
    {
        var t = soFar.TrimStart();
        if (t.Length == 0) return null;
        if (MistralCalls.StartsWith(t, StringComparison.Ordinal)) return t.Length >= MistralCalls.Length ? true : null;
        if (t.StartsWith(MistralCalls, StringComparison.Ordinal)) return true;
        if (t == "[") return null;
        if (t.StartsWith("[{", StringComparison.Ordinal)) return true;
        foreach (var name in toolNames)
        {
            if (name.StartsWith(t, StringComparison.Ordinal)) return null;
            if (!t.StartsWith(name, StringComparison.Ordinal)) continue;
            var rest = t[name.Length..].TrimStart();
            if (rest.Length == 0 || MistralArgs.StartsWith(rest, StringComparison.Ordinal)) return null;
            if (rest[0] == '{' || rest.StartsWith(MistralArgs, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static List<ParsedToolCall> ExtractCalls(string s, out string remaining)
    {
        var calls = new List<ParsedToolCall>();
        var sb = new StringBuilder();
        var pos = 0;
        while (true)
        {
            var open = s.IndexOf(CallOpen, pos, StringComparison.Ordinal);
            if (open < 0) { sb.Append(s, pos, s.Length - pos); break; }
            sb.Append(s, pos, open - pos);
            var bodyStart = open + CallOpen.Length;
            var end = s.IndexOf(CallClose, bodyStart, StringComparison.Ordinal);
            var body = end < 0 ? s[bodyStart..] : s[bodyStart..end];
            if (ParseCall(body) is { } call) calls.Add(call);
            if (end < 0) break;
            pos = end + CallClose.Length;
        }
        remaining = sb.ToString().Replace(ThinkOpen, "", StringComparison.Ordinal).Replace(ThinkClose, "", StringComparison.Ordinal).Trim();
        return calls;
    }

    /// <summary>Parses the inside of one &lt;tool_call&gt; block; null when it is neither dialect.</summary>
    public static ParsedToolCall? ParseCall(string body)
    {
        body = body.Trim();
        var fn = FunctionTag().Match(body);
        if (fn.Success)
        {
            var args = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (Match p in ParameterTag().Matches(body))
                args[p.Groups[1].Value.Trim()] = Value(p.Groups[2].Value);
            return new ParsedToolCall(fn.Groups[1].Value.Trim(), args);
        }
        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) return null;
                var args = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (root.TryGetProperty("arguments", out var a) || root.TryGetProperty("parameters", out a))
                {
                    var obj = a.ValueKind == JsonValueKind.String ? JsonDocument.Parse(a.GetString() ?? "{}").RootElement : a;
                    if (obj.ValueKind == JsonValueKind.Object)
                        foreach (var p in obj.EnumerateObject()) args[p.Name] = ToClr(p.Value);
                }
                return new ParsedToolCall(name, args);
            }
            catch (JsonException) { return null; }
        }
        return null;
    }

    /// <summary>XML parameter values are text; numbers, booleans and JSON arrays/objects are recovered.</summary>
    private static object? Value(string raw)
    {
        var v = raw.Trim('\r', '\n', ' ', '\t');
        if (v.Length == 0) return "";
        if (v is "null") return null;
        if (v[0] is '{' or '[' || v is "true" or "false" || NumberLike().IsMatch(v))
        {
            try { using var doc = JsonDocument.Parse(v); return ToClr(doc.RootElement); }
            catch (JsonException) { }
        }
        return v;
    }

    private static object? ToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(), // keep integers integral
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.Clone() // arrays/objects: AIFunction binding deserializes JsonElement
    };

    [GeneratedRegex(@"<function=([^>\s]+)\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionTag();

    [GeneratedRegex(@"<parameter=([^>\s]+)\s*>(.*?)</parameter>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ParameterTag();

    [GeneratedRegex(@"^-?\d+(\.\d+)?([eE][-+]?\d+)?$")]
    private static partial Regex NumberLike();
}

/// <summary>
/// Incremental version of <see cref="TextToolCallParser"/> for streaming: classifies text as reasoning or
/// answer as it arrives (so the UI can show the model "thinking" live) and emits a tool call once its closing
/// tag arrives. Tags split across chunks are handled by holding back a possible tag prefix.
/// </summary>
public sealed class StreamingToolCallParser
{
    public enum Kind { Reasoning, Text, ToolCall }
    public readonly record struct Piece(Kind Kind, string? Text, ParsedToolCall? Call);

    private enum State { Reasoning, Answer, ToolCall }
    private static readonly string[] Tags = [TextToolCallParser.ThinkOpen, TextToolCallParser.ThinkClose, TextToolCallParser.CallOpen, TextToolCallParser.CallClose];
    private static readonly int MaxTag = Tags.Max(t => t.Length);

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _call = new();
    private readonly StringBuilder _reasoningSoFar = new();
    private State _state;
    private bool _sawThinkClose, _emittedText, _emittedCall;

    /// <param name="startsInReasoning">True when the chat template opens the think block (Qwen 3.x on Foundry Local).</param>
    public StreamingToolCallParser(bool startsInReasoning = true) => _state = startsInReasoning ? State.Reasoning : State.Answer;

    public IEnumerable<Piece> Push(string chunk)
    {
        _pending.Append(chunk);
        return Drain(final: false);
    }

    /// <param name="truncated">Generation hit the token limit: unclosed reasoning is then unfinished thinking, not an answer.</param>
    public IEnumerable<Piece> Complete(bool truncated = false)
    {
        var pieces = Drain(final: true).ToList();
        if (_state == State.ToolCall && _call.Length > 0 && TextToolCallParser.ParseCall(_call.ToString()) is { } call)
        {
            pieces.Add(new Piece(Kind.ToolCall, null, call));
            _emittedCall = true;
        }
        // No </think> ever came and nothing else was produced: what we called reasoning was the answer
        // (a model that skipped thinking) - unless it was cut off mid-thought.
        if (!truncated && !_sawThinkClose && !_emittedText && !_emittedCall && _reasoningSoFar.Length > 0)
            pieces.Add(new Piece(Kind.Text, _reasoningSoFar.ToString().Trim(), null));
        return pieces;
    }

    private IEnumerable<Piece> Drain(bool final)
    {
        var output = new List<Piece>();
        while (_pending.Length > 0)
        {
            var s = _pending.ToString();
            var (index, tag) = FirstTag(s);
            if (index < 0)
            {
                // Hold back a tail that might be the start of a tag split across chunks.
                var keep = final ? 0 : PossibleTagPrefix(s);
                Emit(output, s[..^keep]);
                _pending.Clear().Append(s[^keep..]);
                break;
            }
            Emit(output, s[..index]);
            _pending.Remove(0, index + tag!.Length);
            switch (tag)
            {
                case TextToolCallParser.ThinkOpen: break;
                case TextToolCallParser.ThinkClose:
                    _sawThinkClose = true;
                    if (_state == State.Reasoning) _state = State.Answer;
                    break;
                case TextToolCallParser.CallOpen:
                    if (_state != State.ToolCall) { _state = State.ToolCall; _call.Clear(); }
                    break;
                case TextToolCallParser.CallClose:
                    if (_state == State.ToolCall)
                    {
                        if (TextToolCallParser.ParseCall(_call.ToString()) is { } call) { output.Add(new Piece(Kind.ToolCall, null, call)); _emittedCall = true; }
                        _call.Clear();
                        _state = State.Answer;
                    }
                    break;
            }
        }
        return output;
    }

    private void Emit(List<Piece> output, string text)
    {
        if (text.Length == 0) return;
        switch (_state)
        {
            case State.Reasoning:
                _reasoningSoFar.Append(text);
                output.Add(new Piece(Kind.Reasoning, text, null));
                break;
            case State.Answer:
                if (!_emittedText) text = text.TrimStart();
                if (text.Length == 0) return;
                _emittedText = true;
                output.Add(new Piece(Kind.Text, text, null));
                break;
            default:
                _call.Append(text);
                break;
        }
    }

    private static (int Index, string? Tag) FirstTag(string s)
    {
        var best = -1;
        string? found = null;
        foreach (var t in Tags)
        {
            var i = s.IndexOf(t, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) { best = i; found = t; }
        }
        return (best, found);
    }

    private static int PossibleTagPrefix(string s)
    {
        for (var len = Math.Min(MaxTag - 1, s.Length); len > 0; len--)
        {
            var tail = s[^len..];
            if (Tags.Any(t => t.StartsWith(tail, StringComparison.Ordinal))) return len;
        }
        return 0;
    }
}
