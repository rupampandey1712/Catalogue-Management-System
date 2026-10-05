using Catalogue.Application;
using Catalogue.Infrastructure.Resilience;
using Polly.CircuitBreaker;

namespace Catalogue.UnitTests;

public class ResilienceAndCorrelationTests
{
    [Theory]
    [InlineData("ui-click-7f3a", "ui-click-7f3a")]
    [InlineData("  abc.DEF_1:2  ", "abc.DEF_1:2")]
    [InlineData("has space", null)]
    [InlineData("<script>", null)]
    [InlineData("", null)]
    public void Correlation_ids_from_callers_are_validated(string input, string? expected)
        => Assert.Equal(expected, CorrelationContext.Normalize(input));

    [Fact]
    public async Task Correlation_context_flows_across_awaits_and_restores_on_dispose()
    {
        Assert.Null(CorrelationContext.Current);
        using (CorrelationContext.Begin("outer"))
        {
            await Task.Yield();
            Assert.Equal("outer", CorrelationContext.Current);
            using (CorrelationContext.Begin("inner"))
                Assert.Equal("inner", await Task.Run(() => CorrelationContext.Current));
            Assert.Equal("outer", CorrelationContext.Current);
        }
        Assert.Null(CorrelationContext.Current);
    }

    [Fact]
    public void Invalid_inbound_id_is_replaced_with_a_generated_one()
    {
        using var _ = CorrelationContext.Begin("bad id with spaces");
        Assert.Matches("^[0-9a-f]{32}$", CorrelationContext.Current!);
    }

    [Fact]
    public void Transient_classification_separates_outages_from_poison()
    {
        Assert.True(TransientFaults.IsTransient(new TimeoutException()));
        Assert.True(TransientFaults.IsTransient(new HttpRequestException("reset")));
        Assert.True(TransientFaults.IsTransient(new BrokenCircuitException()));
        Assert.True(TransientFaults.IsTransient(new InvalidOperationException("wrapped", new IOException())));
        Assert.False(TransientFaults.IsTransient(new FormatException("bad EDI")));
        Assert.False(TransientFaults.IsTransient(new ArgumentException()));
    }

    [Fact]
    public async Task Sql_pipeline_does_not_retry_permanent_failures()
    {
        var pipeline = TransientFaults.SqlWritePipeline();
        var calls = 0;
        await Assert.ThrowsAsync<FormatException>(async () =>
            await pipeline.ExecuteAsync(_ => { calls++; throw new FormatException(); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Circuit_opens_after_sustained_transient_failures()
    {
        var events = new List<string>();
        var pipeline = TransientFaults.SqlWritePipeline(events.Add);
        for (var i = 0; i < 12; i++)
        {
            try { await pipeline.ExecuteAsync(_ => throw new TimeoutException()); }
            catch (TimeoutException) { }
            catch (BrokenCircuitException) { break; }
        }
        Assert.Contains(events, e => e.Contains("circuit OPEN"));
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.CompletedTask));
    }
}
