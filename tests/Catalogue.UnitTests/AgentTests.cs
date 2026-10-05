using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Catalogue.Agents;
using Catalogue.Agents.FoundryLocal;
using Catalogue.Agents.Review;
using Catalogue.Agents.Runs;
using Catalogue.Agents.Tools;
using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalogue.UnitTests;

public class TextToolCallParserTests
{
    [Fact]
    public void Qwen_xml_tool_call_after_reasoning_is_parsed()
    {
        var raw = "The user wants waiting orders. Submitted means waiting.\n</think>\n\n<tool_call>\n<function=list_orders>\n<parameter=status>\nSubmitted\n</parameter>\n<parameter=limit>\n5\n</parameter>\n</function>\n</tool_call>\n";
        var p = TextToolCallParser.Parse(raw);
        Assert.StartsWith("The user wants", p.Reasoning);
        Assert.Equal("", p.Text);
        var call = Assert.Single(p.ToolCalls);
        Assert.Equal("list_orders", call.Name);
        Assert.Equal("Submitted", call.Arguments["status"]);
        Assert.Equal(5L, call.Arguments["limit"]); // numbers are typed, not strings
    }

    [Fact]
    public void Hermes_json_tool_call_is_parsed()
    {
        var p = TextToolCallParser.Parse("<tool_call>\n{\"name\": \"business_metrics\", \"arguments\": {\"range\": \"7d\"}}\n</tool_call>");
        var call = Assert.Single(p.ToolCalls);
        Assert.Equal("business_metrics", call.Name);
        Assert.Equal("7d", call.Arguments["range"]);
    }

    [Fact]
    public void Plain_answer_and_reasoning_are_separated()
    {
        var p = TextToolCallParser.Parse("Counting...\n</think>\n\nThere are 7 orders waiting.");
        Assert.Equal("Counting...", p.Reasoning);
        Assert.Equal("There are 7 orders waiting.", p.Text);
        Assert.Empty(p.ToolCalls);
        Assert.Equal("Hello!", TextToolCallParser.Parse("Hello!").Text);
    }

    [Fact]
    public void Unclosed_think_block_is_reasoning_only()
    {
        var p = TextToolCallParser.Parse("<think>I need to check the orders and then");
        Assert.Equal("", p.Text);
        Assert.Equal("I need to check the orders and then", p.Reasoning);
    }

    [Fact]
    public void Streaming_parser_handles_tags_split_across_chunks()
    {
        var parser = new StreamingToolCallParser();
        var pieces = new List<StreamingToolCallParser.Piece>();
        foreach (var chunk in new[] { "Let me th", "ink</thi", "nk>\n\nChecking <tool_", "call><function=list_orders><parameter=sta", "tus>Submitted</parameter></function></tool_call>" })
            pieces.AddRange(parser.Push(chunk));
        pieces.AddRange(parser.Complete());

        Assert.Equal("Let me think", string.Concat(pieces.Where(p => p.Kind == StreamingToolCallParser.Kind.Reasoning).Select(p => p.Text)));
        Assert.Equal("Checking ", string.Concat(pieces.Where(p => p.Kind == StreamingToolCallParser.Kind.Text).Select(p => p.Text)));
        var call = Assert.Single(pieces, p => p.Kind == StreamingToolCallParser.Kind.ToolCall).Call!;
        Assert.Equal("list_orders", call.Name);
        Assert.Equal("Submitted", call.Arguments["status"]);
    }

    [Fact]
    public void Output_cut_off_mid_thought_is_never_shown_as_the_answer()
    {
        // Foundry's Qwen template opens the think block itself, so the output starts with plain reasoning.
        var p = TextToolCallParser.Parse("Thinking Process:\n1. Analyze the request...", truncated: true);
        Assert.Equal("", p.Text);
        Assert.StartsWith("Thinking Process", p.Reasoning);

        var parser = new StreamingToolCallParser();
        var pieces = parser.Push("Thinking Process:\n1. Analyze").Concat(parser.Complete(truncated: true)).ToList();
        Assert.DoesNotContain(pieces, x => x.Kind == StreamingToolCallParser.Kind.Text);
    }

    [Theory]
    [InlineData("stop", 1400L, 1400, true)]   // Foundry says "stop" but the whole budget was used
    [InlineData("stop", 300L, 1400, false)]
    [InlineData("length", 10L, 1400, true)]
    [InlineData("stop", null, 1400, false)]
    public void Truncation_is_detected_from_the_token_budget(string finish, long? used, int max, bool expected) =>
        Assert.Equal(expected, TextToolCallChatClient.Truncated(new ChatFinishReason(finish), used, new ChatOptions { MaxOutputTokens = max }));

    [Fact]
    public void Streaming_output_without_think_close_becomes_the_answer()
    {
        var parser = new StreamingToolCallParser();
        var pieces = parser.Push("Seven orders are waiting.").Concat(parser.Complete()).ToList();
        Assert.Equal("Seven orders are waiting.", pieces.Last(p => p.Kind == StreamingToolCallParser.Kind.Text).Text);
    }
}

public class MistralToolCallTests
{
    private static readonly string[] Tools = ["list_orders", "list_imports", "operations_overview"];

    [Fact]
    public void Bare_calls_as_emitted_by_ministral_on_foundry_local_are_parsed()
    {
        // Exact output captured from ministral-3-3b-instruct-2512 for a two-part question.
        Assert.True(TextToolCallParser.TryParseBareCalls("list_orders{\"status\": \"Submitted\"}list_imports{\"status\": \"failed\"}", Tools, out var calls));
        Assert.Equal(["list_orders", "list_imports"], calls.Select(c => c.Name));
        Assert.Equal("failed", calls[1].Arguments["status"]);
    }

    [Theory]
    [InlineData("[TOOL_CALLS]list_orders[ARGS]{\"status\": \"Accepted\"}")]
    [InlineData("[TOOL_CALLS][{\"name\": \"list_orders\", \"arguments\": {\"status\": \"Accepted\"}}]")]
    [InlineData("  list_orders {\"status\": \"Accepted\"}\n")]
    public void Marker_and_array_forms_are_parsed(string text)
    {
        Assert.True(TextToolCallParser.TryParseBareCalls(text, Tools, out var calls));
        Assert.Equal("Accepted", Assert.Single(calls).Arguments["status"]);
    }

    [Theory]
    [InlineData("There is 1 order waiting: PO-1 {HVX}.")]
    [InlineData("delete_everything{\"all\": true}")]          // a tool that was not offered
    [InlineData("list_orders{\"status\": \"Submitted\"} and then some prose")]
    [InlineData("list_orders{\"status\": ")]                    // incomplete JSON
    public void Prose_and_unknown_tools_are_never_turned_into_calls(string text) =>
        Assert.False(TextToolCallParser.TryParseBareCalls(text, Tools, out _));

    [Theory]
    [InlineData("list_", null)]
    [InlineData("list_orders", null)]
    [InlineData("list_orders{", true)]
    [InlineData("[TOOL_", null)]
    [InlineData("[TOOL_CALLS]", true)]
    [InlineData("There", false)]
    [InlineData("list of orders", false)]
    public void Streaming_decides_early_whether_output_is_a_call(string soFar, bool? expected) =>
        Assert.Equal(expected, TextToolCallParser.LooksLikeBareCall(soFar, Tools));

    [Fact]
    public void Truncated_answer_of_a_non_thinking_model_stays_an_answer()
    {
        var p = TextToolCallParser.Parse("There are 7 orders waiting and", truncated: true, toolNames: Tools, thinking: false);
        Assert.Equal("There are 7 orders waiting and", p.Text);
        Assert.Null(p.Reasoning);
    }
}

public class TextToolCallChatClientTests
{
    /// <summary>Plays back scripted model outputs, one per call (like Foundry Local returning text-encoded tool calls).</summary>
    private sealed class ScriptedModel(params string[] outputs) : IChatClient
    {
        private int _call;
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Requests.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, outputs[_call++])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(messages.ToList());
            var text = outputs[_call++];
            for (var i = 0; i < text.Length; i += 7) // awkward chunk size on purpose
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, text.Substring(i, Math.Min(7, text.Length - i)));
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static readonly string[] Script =
    [
        "Need the count.\n</think>\n\n<tool_call>\n<function=count_orders>\n<parameter=status>\nSubmitted\n</parameter>\n</function>\n</tool_call>",
        "Got 7.\n</think>\n\nThere are 7 orders waiting for the seller."
    ];

    private static (IChatClient Client, ScriptedModel Model, List<string> Calls) Build()
    {
        var model = new ScriptedModel(Script);
        var calls = new List<string>();
        var client = new TextToolCallChatClient(model).AsBuilder().UseFunctionInvocation().Build();
        return (client, model, calls);
    }

    private static ChatOptions Options(List<string> calls) => new()
    {
        Tools = [AIFunctionFactory.Create((string status) => { calls.Add(status); return "{\"count\":7}"; }, "count_orders")]
    };

    [Fact]
    public async Task Text_tool_calls_are_executed_by_function_invocation()
    {
        var (client, model, calls) = Build();
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "How many orders wait?")], Options(calls));
        Assert.Equal(["Submitted"], calls);
        Assert.Equal("There are 7 orders waiting for the seller.", response.Text);
        // The second model call saw the tool result.
        Assert.Contains(model.Requests[1], m => m.Contents.OfType<FunctionResultContent>().Any());
    }

    [Fact]
    public async Task Ministral_style_bare_calls_run_and_the_answer_streams_as_text()
    {
        var model = new ScriptedModel("count_orders{\"status\": \"Submitted\"}", "There are 7 orders waiting for the seller.");
        var calls = new List<string>();
        var client = new TextToolCallChatClient(model, startsInReasoning: false).AsBuilder().UseFunctionInvocation().Build();
        var text = new StringBuilder();
        var reasoning = 0;
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "How many orders wait?")], Options(calls)))
            foreach (var c in u.Contents)
                if (c is TextReasoningContent) reasoning++;
                else if (c is TextContent t && u.Role == ChatRole.Assistant) text.Append(t.Text);
        Assert.Equal(["Submitted"], calls);
        Assert.Equal("There are 7 orders waiting for the seller.", text.ToString());
        Assert.Equal(0, reasoning); // a non-thinking model produces no reasoning content
    }

    [Fact]
    public async Task Streaming_separates_reasoning_runs_tools_and_streams_the_answer()
    {
        var (client, _, calls) = Build();
        var reasoning = new StringBuilder();
        var text = new StringBuilder();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "How many orders wait?")], Options(calls)))
            foreach (var c in u.Contents)
                if (c is TextReasoningContent r) reasoning.Append(r.Text);
                else if (c is TextContent t && u.Role == ChatRole.Assistant) text.Append(t.Text);
        Assert.Equal(["Submitted"], calls);
        Assert.Equal("There are 7 orders waiting for the seller.", text.ToString());
        Assert.Contains("Need the count.", reasoning.ToString());
        Assert.DoesNotContain("</think>", text.ToString());
    }
}

public class FoundryLocalRuntimeTests
{
    private sealed class FakeCli : IFoundryCli
    {
        public bool Loaded;
        public bool Running = true;
        public List<string> Commands { get; } = [];
        public Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            var cmd = string.Join(' ', args);
            Commands.Add(cmd);
            return Task.FromResult(cmd switch
            {
                "server status -o json" => (0, Running ? """{"running":true,"state":"ready","webUrls":["http://127.0.0.1:55113"]}""" : """{"running":false}"""),
                "model list --loaded -o json" => (0, Loaded
                    ? """{"models":[{"alias":"qwen3.5-9b","id":"qwen3.5-9b-cuda-gpu:4","displayName":"qwen3.5-9b-cuda-gpu","device":"Gpu","supportsToolCalling":true}]}"""
                    : """{"models":[]}"""),
                "model load qwen3.5-9b" => Load(),
                "server start" => Start(),
                _ => (1, "unknown command")
            });
        }
        private (int, string) Load() { Loaded = true; return (0, "Loaded"); }
        private (int, string) Start() { Running = true; return (0, "Server ready"); }
    }

    private sealed class ModelsHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private const string Models = """{"data":[{"id":"qwen3.5-9b-cuda-gpu","parent":"qwen3.5-9b","object":"model"}],"object":"list"}""";

    private static FoundryLocalRuntime Runtime(FakeCli cli, string models = Models) =>
        new(new FoundryLocalOptions { Model = "qwen3.5-9b" }, cli, NullLogger<FoundryLocalRuntime>.Instance, TimeProvider.System, new ModelsHandler(models));

    [Theory]
    [InlineData("qwen3.5-9b", "auto", true)]
    [InlineData("deepseek-r1-distill-qwen-7b", "auto", true)]
    [InlineData("ministral-3-3b-instruct-2512", "auto", false)]
    [InlineData("phi-4-mini", "auto", false)]
    [InlineData("ministral-3-3b-instruct-2512", "true", true)]
    public void Thinking_models_are_recognised(string model, string setting, bool expected) =>
        Assert.Equal(expected, new FoundryLocalOptions { Model = model, Thinking = setting }.IsThinkingModel);

    [Fact]
    public async Task Discovers_the_dynamic_port_and_loads_the_model_on_first_use()
    {
        var cli = new FakeCli();
        using var runtime = Runtime(cli);
        var before = await runtime.GetStatusAsync(refresh: true, default);
        Assert.True(before.ServiceRunning && before.ModelCached);
        Assert.False(before.ModelLoaded);

        var connection = await runtime.EnsureReadyAsync(default);
        Assert.Equal("http://127.0.0.1:55113/", connection.Endpoint.ToString());
        Assert.Equal("qwen3.5-9b-cuda-gpu", connection.ModelId); // alias resolved to the id chat requests need
        Assert.Single(cli.Commands, c => c == "model load qwen3.5-9b");

        await runtime.EnsureReadyAsync(default); // cached: no second load
        Assert.Single(cli.Commands, c => c == "model load qwen3.5-9b");
    }

    [Fact]
    public async Task Starts_the_service_when_it_is_not_running()
    {
        var cli = new FakeCli { Running = false, Loaded = true };
        using var runtime = Runtime(cli);
        await runtime.EnsureReadyAsync(default);
        Assert.Contains("server start", cli.Commands);
    }

    [Fact]
    public async Task Missing_model_explains_how_to_download_it()
    {
        using var runtime = Runtime(new FakeCli(), """{"data":[]}""");
        var ex = await Assert.ThrowsAsync<FoundryLocalException>(() => runtime.EnsureReadyAsync(default));
        Assert.Contains("foundry model download qwen3.5-9b", ex.Message);
    }

    [Theory]
    [InlineData("""{"running":true,"webUrls":["http://127.0.0.1:51000/"]}""", "http://127.0.0.1:51000")]
    [InlineData("""{"service":{"ready":true,"webUrls":["http://127.0.0.1:52000"]}}""", "http://127.0.0.1:52000")]
    [InlineData("""{"running":false,"webUrls":[]}""", null)]
    [InlineData("not json", null)]
    public void Endpoint_is_read_from_cli_json(string json, string? expected) => Assert.Equal(expected, FoundryLocalRuntime.ParseEndpoint(json));

    [Theory]
    [InlineData("http://example.com:5000")]
    [InlineData("http://10.0.0.5:5000")]
    public void Only_loopback_endpoints_are_accepted(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => FoundryLocalOptions.From(Config(("Agent:FoundryLocal:Endpoint", endpoint))));

    [Fact]
    public void Model_alias_cannot_inject_cli_arguments() =>
        Assert.Throws<InvalidOperationException>(() => FoundryLocalOptions.From(Config(("Agent:FoundryLocal:Model", "qwen --force & calc"))));

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();
}

public class AgentToolTests
{
    private static AgentPrincipal User(string name, long? org, long? supplier, params string[] policies) =>
        new(1, name, name, new HashSet<string>(policies), false, false, org, null, supplier, null);

    private static CatalogueAgentTools Tools(CatalogueDbContext db, AgentPrincipal user)
    {
        var cache = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new CatalogueAgentTools(db, new MarketplacePricing(db), new AnalyticsService(db, cache, TimeProvider.System), user, null, TimeProvider.System);
    }

    private static async Task<CatalogueDbContext> DbWithOrderAsync()
    {
        var db = new CatalogueDbContext(new DbContextOptionsBuilder<CatalogueDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var po = new PurchaseOrder("PO-1", buyerOrganizationId: 7, 1, supplierId: 5, "GBP", "buyer", null, null);
        po.AddLine(new PurchaseOrderLine(1, 1, "S-1", "Boiler", 1, 500m, 500m, "GBP", ""));
        db.PurchaseOrders.Add(po);
        db.ImportFiles.Add(new ImportFile(5, "blob", "feed.csv", "hash", "CSV", null));
        await db.SaveChangesAsync();
        return db;
    }

    [Theory]
    [InlineData("pending", PurchaseOrderStatus.Submitted)]
    [InlineData("Waiting", PurchaseOrderStatus.Submitted)]
    [InlineData("to ship", PurchaseOrderStatus.Accepted)]
    [InlineData("canceled", PurchaseOrderStatus.Cancelled)]
    public void Order_status_synonyms_are_normalized(string input, PurchaseOrderStatus expected) => Assert.Equal(expected, CatalogueAgentTools.OrderStatus(input));

    [Theory]
    [InlineData("quarter", "3m")]
    [InlineData("last week", "7d")]
    [InlineData("year", "1y")]
    [InlineData(null, "7d")]
    public void Ranges_are_normalized(string? input, string expected) => Assert.Equal(expected, CatalogueAgentTools.Range(input));

    [Fact]
    public void Unknown_values_get_a_helpful_error()
    {
        var ex = Assert.Throws<ArgumentException>(() => CatalogueAgentTools.OrderStatus("lost"));
        Assert.Contains("Submitted", ex.Message);
    }

    [Fact]
    public async Task Router_gives_each_question_only_the_tools_it_needs_and_the_user_may_use()
    {
        await using var db = await DbWithOrderAsync();
        var ops = Tools(db, User("ops", null, null, AgentPolicies.ImportOps, AgentPolicies.Marketplace));
        var opsTools = ToolRouter.Available(ops, User("ops", null, null, AgentPolicies.ImportOps, AgentPolicies.Marketplace));
        var forOrders = ToolRouter.Select("Which orders are waiting for the seller?", opsTools).Select(t => t.Name).ToList();
        Assert.Contains("list_orders", forOrders);
        Assert.DoesNotContain("list_imports", forOrders);
        Assert.Contains(CatalogueAgentTools.Overview, forOrders);
        Assert.Contains("list_imports", ToolRouter.Select("Did any EDI imports fail?", opsTools).Select(t => t.Name));

        var buyer = User("buyer", 7, null, AgentPolicies.Buyer, AgentPolicies.Marketplace);
        var buyerTools = ToolRouter.Available(Tools(db, buyer), buyer);
        Assert.DoesNotContain(buyerTools, t => t.Function.Name is "list_imports" or "pending_approvals");
    }

    [Fact]
    public async Task Proposals_check_who_may_act_and_are_not_duplicated()
    {
        await using var db = await DbWithOrderAsync();
        var seller = User("seller", 9, 5, AgentPolicies.Seller, AgentPolicies.Marketplace);
        var first = JsonSerializer.SerializeToElement(await Tools(db, seller).ProposeAction("accept_order", "po-1", "Stock confirmed"));
        var again = JsonSerializer.SerializeToElement(await Tools(db, seller).ProposeAction("accept", "PO-1", "again"));
        Assert.Equal(first.GetProperty("proposalId").GetInt64(), again.GetProperty("proposalId").GetInt64());
        Assert.Single(db.AgentActionProposals);
        Assert.Equal(ProposalStatus.Pending, db.AgentActionProposals.Single().Status);

        var buyer = User("buyer", 7, null, AgentPolicies.Buyer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tools(db, buyer).ProposeAction("accept_order", "PO-1", "x")); // buyer cannot accept
        await Tools(db, buyer).ProposeAction("cancel_order", "PO-1", "Ordered by mistake"); // but can cancel
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tools(db, seller).ProposeAction("ship_order", "PO-1", "x")); // must be accepted first
        await Assert.ThrowsAsync<InvalidOperationException>(() => Tools(db, buyer).ProposeAction("reprocess_import", "1", "x")); // needs ImportOps
        await Assert.ThrowsAsync<ArgumentException>(() => Tools(db, seller).ProposeAction("delete_everything", "PO-1", "x"));
    }

    [Fact]
    public async Task Guarded_tools_turn_errors_into_results_enforce_the_budget_and_trace_calls()
    {
        var trace = new List<ToolTraceEntry>();
        var budget = new ToolBudget(limit: 2, maxChars: 40);
        var failing = new GuardedFunction(AIFunctionFactory.Create(new Func<string>(() => throw new ArgumentException("bad status")), "f"), budget, (e, start) => { if (!start) trace.Add(e); });
        var big = new GuardedFunction(AIFunctionFactory.Create(() => new string('x', 500), "g"), budget, (e, start) => { if (!start) trace.Add(e); });

        Assert.Contains("bad status", (string)(await failing.InvokeAsync(new AIFunctionArguments()))!);
        Assert.EndsWith("…(truncated)", (string)(await big.InvokeAsync(new AIFunctionArguments()))!);
        Assert.Contains("limit reached", (string)(await big.InvokeAsync(new AIFunctionArguments()))!);
        Assert.Equal([true, false, true], trace.Select(t => t.Failed));
    }
}

public class AgentRunTests
{
    [Fact]
    public async Task One_active_run_per_user_bounded_queue_and_replayable_events()
    {
        var coordinator = new AgentRunCoordinator(new AgentOptions { MaxQueue = 2 });
        var run = coordinator.Enqueue("chat", 1, null, (_, _) => Task.CompletedTask);
        Assert.Throws<AgentBusyException>(() => coordinator.Enqueue("chat", 1, null, (_, _) => Task.CompletedTask));
        coordinator.Enqueue("chat", 2, null, (_, _) => Task.CompletedTask);
        Assert.Throws<AgentBusyException>(() => coordinator.Enqueue("chat", 3, null, (_, _) => Task.CompletedTask)); // queue full

        run.Publish("text", new { delta = "a" });
        run.Publish("text", new { delta = "b" });
        var reader = Task.Run(async () =>
        {
            var seen = new List<string>();
            await foreach (var e in run.ReadAsync(afterSeq: 1, CancellationToken.None)) seen.Add(e.Type);
            return seen;
        });
        await Task.Delay(50);
        coordinator.Dequeued(run);
        run.MarkRunning();
        run.Finish(AgentRunStatus.Completed, null);
        var types = await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["text", "text", "started", "done"], types); // replayed from after the first event ("queued"), then live
    }
}

public class OperationsReviewTests
{
    private static OperationsSnapshot Snapshot(int stuck = 0, IReadOnlyList<FailedImport>? failed = null, IReadOnlyList<SmallPriceChange>? small = null,
        decimal orders = 10, decimal prevOrders = 10) => new(1, DateTime.UtcNow,
        failed ?? [], stuck, 3, 12, 40, 5, 4.5, small ?? [], 2, [], [], 1, 0, 0, 0, 0, orders, prevOrders, 5000, 5000, 1000, 50, 0);

    [Fact]
    public void Triage_orders_by_severity_and_suggests_safe_actions()
    {
        var findings = TriageExecutor.Triage(Snapshot(stuck: 1,
            failed: [new FailedImport(42, "feed.edi", "HVX", nameof(ImportStatus.Failed), "Bad segment", DateTime.UtcNow)],
            small: [new SmallPriceChange(9, "BLR-1", "NWD", 100m, 101m, 1m)]));
        Assert.Equal("critical", findings[0].Severity);
        Assert.Contains(findings, f => f.Action is { Kind: AgentActionKind.ReprocessImport, TargetId: "42" });
        Assert.Contains(findings, f => f.Action is { Kind: AgentActionKind.ApproveChange, TargetId: "9" });
        Assert.Contains(findings, f => f.Area == "Approvals" && f.Severity == "warning"); // oldest change waiting 4.5 days
    }

    [Fact]
    public void Sales_drop_is_flagged()
        => Assert.Contains(TriageExecutor.Triage(Snapshot(orders: 3, prevOrders: 10)), f => f.Area == "Sales" && f.Severity == "warning");

    [Fact]
    public void Rule_based_briefing_has_the_same_shape_as_the_model_briefing()
    {
        var snapshot = Snapshot(stuck: 1);
        var text = BriefingExecutor.RuleBased(new TriageResult(snapshot, TriageExecutor.Triage(snapshot)));
        Assert.StartsWith("Status: 1 critical issue(s)", text);
        Assert.Contains("Priorities:", text);
        Assert.Contains("Next steps:", text);
    }

    [Theory]
    [InlineData("""["Fix import 42","Chase PO-1"]""", "Fix import 42|Chase PO-1")]                                        // Qwen: strings
    [InlineData("""[{"action":"Fix import 42","reason":"feed failed"}]""", "Fix import 42 - feed failed")]                  // Ministral: objects
    [InlineData("\"1. Fix import 42\\n2. Chase PO-1\"", "Fix import 42|Chase PO-1")]                                       // one numbered string
    public void Briefing_priorities_accept_every_shape_models_produce(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, string.Join("|", BriefingExecutor.FlattenPriorities(doc.RootElement)));
    }

    [Fact]
    public void Tool_call_only_assistant_turns_get_text_content_for_strict_templates()
    {
        // Foundry Local's Ministral template drops the connection on an assistant message with "content": null.
        List<ChatMessage> history =
        [
            new(ChatRole.User, "q"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "list_orders")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "{}")])
        ];
        var sent = FoundryLocalChatClient.Normalize(history);
        Assert.Contains(sent[1].Contents, c => c is TextContent);
        Assert.Contains(sent[1].Contents, c => c is FunctionCallContent);
        Assert.DoesNotContain(history[1].Contents, c => c is TextContent); // the caller's history is not mutated
        List<ChatMessage> plain = [new(ChatRole.User, "q"), new(ChatRole.Assistant, "answer")];
        Assert.Same(plain, FoundryLocalChatClient.Normalize(plain)); // nothing to fix: no copy is made
    }

    [Fact]
    public void Proposals_expire_and_cannot_be_decided_twice()
    {
        var p = new AgentActionProposal(AgentActionKind.AcceptOrder, "PO-1", "Accept order PO-1", "ok", "assistant", "seller");
        p.Executed("admin", "done", DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => p.Rejected("admin", null, DateTime.UtcNow));
        var old = new AgentActionProposal(AgentActionKind.ShipOrder, "PO-2", "Ship", "ok", "assistant", "seller");
        Assert.False(old.IsOpen(DateTime.UtcNow.Add(AgentActionProposal.Lifetime).AddMinutes(1)));
    }
}

public class AnalyticsWindowTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 14, 30, 0, DateTimeKind.Utc); // a Monday

    [Fact]
    public void Today_is_hourly_for_the_whole_day()
    {
        var w = AnalyticsWindow.For("today", Now);
        Assert.Equal(AnalyticsBucket.Hour, w.Bucket);
        Assert.Equal(24, w.Buckets.Count);
        Assert.Equal(Now.Date.AddDays(-1).AddHours(9.5), w.PreviousFromUtc); // equally long period before
    }

    [Theory]
    [InlineData("7d", 7, AnalyticsBucket.Day)]
    [InlineData("15d", 15, AnalyticsBucket.Day)]
    [InlineData("1m", 30, AnalyticsBucket.Day)]
    [InlineData("3m", 90, AnalyticsBucket.Day)]
    [InlineData("6m", 26, AnalyticsBucket.Week)]
    [InlineData("1y", 12, AnalyticsBucket.Month)]
    public void Ranges_have_the_expected_buckets(string range, int count, AnalyticsBucket bucket)
    {
        var w = AnalyticsWindow.For(range, Now);
        Assert.Equal(bucket, w.Bucket);
        Assert.Equal(count, w.Buckets.Count);
        Assert.All(w.Buckets, b => Assert.True(b <= Now));
    }

    [Fact]
    public void Weeks_start_on_monday_and_months_on_the_first()
    {
        Assert.All(AnalyticsWindow.For("6m", Now).Buckets, b => Assert.Equal(DayOfWeek.Monday, b.DayOfWeek));
        Assert.All(AnalyticsWindow.For("1y", Now).Buckets, b => Assert.Equal(1, b.Day));
        Assert.Equal(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc), AnalyticsWindow.For("6m", Now).BucketOf(new DateTime(2026, 10, 4, 23, 0, 0)));
    }

    [Fact]
    public void Unknown_range_is_rejected() => Assert.Throws<ArgumentException>(() => AnalyticsWindow.For("2w", Now));
}
