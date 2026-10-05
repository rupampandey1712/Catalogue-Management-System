using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Catalogue.Agents.FoundryLocal;

/// <summary>Settings for the on-device model served by Foundry Local (section <c>Agent:FoundryLocal</c>).</summary>
public sealed class FoundryLocalOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>The foundry CLI (on PATH after installing Foundry Local).</summary>
    public string CliPath { get; init; } = "foundry";
    /// <summary>Model alias as shown by <c>foundry cache list</c> (e.g. qwen3.5-9b), or an exact model id.</summary>
    public string Model { get; init; } = "ministral-3-3b-instruct-2512";
    /// <summary>Fixed endpoint (e.g. http://127.0.0.1:55113). Empty = discover with <c>foundry server status</c>; the port changes per start.</summary>
    public string? Endpoint { get; init; }
    public bool AutoStartService { get; init; } = true;
    public bool AutoLoadModel { get; init; } = true;
    public int MaxOutputTokens { get; init; } = 1200;
    public float Temperature { get; init; } = 0.2f;
    /// <summary>One model call. Local models on a laptop GPU can take minutes for a long answer.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ModelLoadTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>
    /// "auto" (default), "true" or "false": whether the model reasons in a think block before answering. Auto
    /// recognises thinking families by name (Qwen 3.x, DeepSeek-R1, Magistral, *-reasoning); instruct models such as
    /// Ministral 3 answer directly, so their output streams as answer text from the first token.
    /// </summary>
    public string Thinking { get; init; } = "auto";

    public bool IsThinkingModel => Thinking.ToLowerInvariant() switch
    {
        "true" or "on" or "yes" => true,
        "false" or "off" or "no" => false,
        _ => Regex.IsMatch(Model, @"qwen3|deepseek-r1|magistral|reason|think|qwq", RegexOptions.IgnoreCase)
    };

    public static FoundryLocalOptions From(IConfiguration config)
    {
        var s = config.GetSection("Agent:FoundryLocal");
        var o = new FoundryLocalOptions
        {
            Enabled = s.GetValue("Enabled", true),
            CliPath = s["CliPath"] is { Length: > 0 } cli ? cli : "foundry",
            Model = s["Model"] is { Length: > 0 } m ? m : "ministral-3-3b-instruct-2512",
            Endpoint = s["Endpoint"] is { Length: > 0 } e ? e.TrimEnd('/') : null,
            AutoStartService = s.GetValue("AutoStartService", true),
            AutoLoadModel = s.GetValue("AutoLoadModel", true),
            MaxOutputTokens = Math.Clamp(s.GetValue("MaxOutputTokens", 1200), 64, 8192),
            Temperature = Math.Clamp(s.GetValue("Temperature", 0.2f), 0f, 2f),
            RequestTimeout = s.GetValue("RequestTimeout", TimeSpan.FromMinutes(10)),
            ModelLoadTimeout = s.GetValue("ModelLoadTimeout", TimeSpan.FromMinutes(10)),
            Thinking = s["Thinking"] is { Length: > 0 } th ? th : "auto",
        };
        // The alias is passed to a process, so keep it to a safe character set.
        if (!Regex.IsMatch(o.Model, @"^[A-Za-z0-9._:\-]{1,100}$")) throw new InvalidOperationException($"Agent:FoundryLocal:Model '{o.Model}' is not a valid model alias.");
        if (o.Endpoint is not null && (!Uri.TryCreate(o.Endpoint, UriKind.Absolute, out var u) || !u.IsLoopback))
            throw new InvalidOperationException("Agent:FoundryLocal:Endpoint must be a loopback URL (Foundry Local runs on this machine).");
        return o;
    }
}

public sealed record FoundryLocalStatus(
    bool Enabled, bool ServiceRunning, string? Endpoint, string Model, string? ModelId, bool ModelCached, bool ModelLoaded,
    bool? SupportsToolCalling, string? Device, string? Error, DateTimeOffset CheckedAtUtc)
{
    public bool Ready => Enabled && ServiceRunning && ModelLoaded && ModelId is not null;
}

public sealed record FoundryConnection(Uri Endpoint, string ModelId);

/// <summary>Runs the foundry CLI. An interface so tests can fake it.</summary>
public interface IFoundryCli
{
    Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

public sealed partial class FoundryCli(FoundryLocalOptions options, ILogger<FoundryCli> log) : IFoundryCli
{
    public async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(options.CliPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a); // no shell, no string concatenation
        psi.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new FoundryLocalException($"The foundry CLI ('{options.CliPath}') could not be started. Is Foundry Local installed? {ex.Message}");
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new FoundryLocalException($"'foundry {string.Join(' ', args)}' did not finish within {timeout.TotalSeconds:0}s.");
        }
        var output = Ansi().Replace(await stdout + await stderr, "");
        log.LogDebug("foundry {Args} exited {Code}", string.Join(' ', args), process.ExitCode);
        return (process.ExitCode, output);
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
}

public sealed class FoundryLocalException(string message) : Exception(message);

/// <summary>
/// Finds the Foundry Local service (the port changes on every start), starts it if needed and makes sure the
/// configured model is loaded. Status is cached briefly; calls are single-flight because loading a 7 GB model
/// twice at once would exhaust GPU memory.
/// </summary>
public sealed class FoundryLocalRuntime(FoundryLocalOptions options, IFoundryCli cli, ILogger<FoundryLocalRuntime> log, TimeProvider clock,
    HttpMessageHandler? httpHandler = null) : IDisposable
{
    private static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new(httpHandler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(10) };
    private FoundryLocalStatus? _status;

    public FoundryLocalOptions Options => options;

    public void Invalidate() => _status = null;

    public async Task<FoundryLocalStatus> GetStatusAsync(bool refresh, CancellationToken ct)
    {
        if (!options.Enabled) return Disabled();
        if (!refresh && _status is { } cached && clock.GetUtcNow() - cached.CheckedAtUtc < StatusTtl) return cached;
        await _gate.WaitAsync(ct);
        try { return _status = await ProbeAsync(ct); }
        finally { _gate.Release(); }
    }

    /// <summary>Returns a usable connection, starting the service and loading the model when allowed.</summary>
    public async Task<FoundryConnection> EnsureReadyAsync(CancellationToken ct)
    {
        if (!options.Enabled) throw new FoundryLocalException("The local model is disabled (Agent:FoundryLocal:Enabled=false).");
        if (_status is { Ready: true } s && clock.GetUtcNow() - s.CheckedAtUtc < StatusTtl) return new(new Uri(s.Endpoint!), s.ModelId!);
        await _gate.WaitAsync(ct);
        try
        {
            var status = await ProbeAsync(ct);
            if (!status.ServiceRunning && options.Endpoint is null && options.AutoStartService)
            {
                log.LogInformation("Starting Foundry Local service");
                var (code, output) = await cli.RunAsync(["server", "start"], TimeSpan.FromMinutes(2), ct);
                if (code != 0) throw new FoundryLocalException($"Could not start Foundry Local: {Last(output)}");
                status = await ProbeAsync(ct);
            }
            if (!status.ServiceRunning) throw new FoundryLocalException(status.Error ?? "Foundry Local is not running. Start it with 'foundry server start'.");
            if (!status.ModelCached || status.ModelId is null)
                throw new FoundryLocalException($"Model '{options.Model}' is not downloaded. Run 'foundry model download {options.Model}'.");
            if (!status.ModelLoaded)
            {
                if (!options.AutoLoadModel) throw new FoundryLocalException($"Model '{options.Model}' is not loaded. Run 'foundry model load {options.Model}'.");
                log.LogInformation("Loading model {Model} into Foundry Local (this can take a minute)", options.Model);
                var sw = Stopwatch.StartNew();
                var (code, output) = await cli.RunAsync(["model", "load", options.Model], options.ModelLoadTimeout, ct);
                if (code != 0) throw new FoundryLocalException($"Could not load model '{options.Model}': {Last(output)}");
                log.LogInformation("Model {Model} loaded in {Seconds:0.0}s", options.Model, sw.Elapsed.TotalSeconds);
                status = await ProbeAsync(ct);
                if (!status.ModelLoaded) throw new FoundryLocalException($"Model '{options.Model}' did not report as loaded after 'foundry model load'.");
            }
            _status = status;
            return new(new Uri(status.Endpoint!), status.ModelId!);
        }
        finally { _gate.Release(); }
    }

    private async Task<FoundryLocalStatus> ProbeAsync(CancellationToken ct)
    {
        string? endpoint = options.Endpoint;
        try
        {
            if (endpoint is null)
            {
                var (code, output) = await cli.RunAsync(["server", "status", "-o", "json"], CliTimeout, ct);
                endpoint = code == 0 ? ParseEndpoint(output) : null;
                if (endpoint is null) return Status(running: false, null, null, false, false, null, null, "Foundry Local service is not running.");
            }

            // The OpenAI-compatible model list maps the alias to the id used in chat requests.
            string? modelId = null;
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{endpoint}/v1/models", ct));
                modelId = ResolveModelId(doc.RootElement, options.Model);
            }
            catch (HttpRequestException ex) { return Status(false, endpoint, null, false, false, null, null, $"Foundry Local at {endpoint} is not reachable: {ex.Message}"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Status(false, endpoint, null, false, false, null, null, $"Foundry Local at {endpoint} timed out."); }

            var (lcode, loadedJson) = await cli.RunAsync(["model", "list", "--loaded", "-o", "json"], CliTimeout, ct);
            var (loaded, tools, device) = lcode == 0 ? ParseLoaded(loadedJson, options.Model, modelId) : (false, null, null);
            return Status(true, endpoint, modelId, modelId is not null, loaded, tools, device,
                modelId is null ? $"Model '{options.Model}' is not in Foundry Local's cache." : null);
        }
        catch (FoundryLocalException ex) { return Status(false, endpoint, null, false, false, null, null, ex.Message); }
    }

    internal static string? ParseEndpoint(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonStart(json));
            var root = doc.RootElement;
            if (root.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.False) return null;
            var urls = root.TryGetProperty("webUrls", out var w) ? w
                : root.TryGetProperty("service", out var svc) && svc.TryGetProperty("webUrls", out var w2) ? w2 : default;
            return urls.ValueKind == JsonValueKind.Array && urls.GetArrayLength() > 0 ? urls[0].GetString()?.TrimEnd('/') : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string? ResolveModelId(JsonElement models, string model)
    {
        if (!models.TryGetProperty("data", out var data)) return null;
        string? byParent = null;
        foreach (var m in data.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (id is null) continue;
            if (string.Equals(id, model, StringComparison.OrdinalIgnoreCase)) return id;
            if (m.TryGetProperty("parent", out var p) && string.Equals(p.GetString(), model, StringComparison.OrdinalIgnoreCase)) byParent ??= id;
        }
        return byParent;
    }

    internal static (bool Loaded, bool? Tools, string? Device) ParseLoaded(string json, string model, string? modelId)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonStart(json));
            if (!doc.RootElement.TryGetProperty("models", out var models)) return (false, null, null);
            foreach (var m in models.EnumerateArray())
            {
                string? S(string name) => m.TryGetProperty(name, out var v) ? v.GetString() : null;
                var id = S("id") ?? "";
                if (string.Equals(S("alias"), model, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(S("displayName"), modelId, StringComparison.OrdinalIgnoreCase)
                    || (modelId is not null && id.StartsWith(modelId, StringComparison.OrdinalIgnoreCase)))
                    return (true, m.TryGetProperty("supportsToolCalling", out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False ? t.GetBoolean() : null, S("device"));
            }
        }
        catch (JsonException) { }
        return (false, null, null);
    }

    private static string JsonStart(string s) { var i = s.IndexOf('{'); return i >= 0 ? s[i..] : s; }
    private static string Last(string output) => output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "unknown error";

    private FoundryLocalStatus Status(bool running, string? endpoint, string? modelId, bool cached, bool loaded, bool? tools, string? device, string? error) =>
        new(true, running, endpoint, options.Model, modelId, cached, loaded, tools, device, error, clock.GetUtcNow());

    private FoundryLocalStatus Disabled() =>
        new(false, false, null, options.Model, null, false, false, null, null, "Disabled by configuration.", clock.GetUtcNow());

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
