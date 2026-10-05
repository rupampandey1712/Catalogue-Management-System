using System.Diagnostics;
using Catalogue.Application;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Catalogue.ServiceDefaults;

/// <summary>
/// Accepts an inbound X-Correlation-ID (or creates one), makes it ambient for the request, echoes
/// it on the response, and rewrites the request header so a proxy (YARP) forwards the same ID.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[CorrelationContext.HeaderName].FirstOrDefault();
        using var scope = CorrelationContext.Begin(inbound);
        var id = CorrelationContext.Current!;

        context.Items["CorrelationId"] = id;
        context.Request.Headers[CorrelationContext.HeaderName] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationContext.HeaderName] = id;
            if (Activity.Current is { } activity)
                context.Response.Headers["traceparent"] = activity.Id;
            return Task.CompletedTask;
        });

        await next(context);
    }
}

/// <summary>Propagates the ambient correlation ID to every outbound HttpClient call.</summary>
public sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (CorrelationContext.Current is { } id && !request.Headers.Contains(CorrelationContext.HeaderName))
            request.Headers.TryAddWithoutValidation(CorrelationContext.HeaderName, id);
        return base.SendAsync(request, ct);
    }
}

/// <summary>Stamps CorrelationId on every log event, including ones from background consumers.</summary>
public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        if (CorrelationContext.Current is { } id)
            logEvent.AddPropertyIfAbsent(factory.CreateProperty(CorrelationContext.LogProperty, id));
    }
}

/// <summary>Adds correlation.id to every span so traces can be searched by the same ID as logs.</summary>
public sealed class CorrelationIdActivityProcessor : OpenTelemetry.BaseProcessor<Activity>
{
    public override void OnStart(Activity activity)
    {
        if (CorrelationContext.Current is { } id && activity.GetTagItem(CorrelationContext.TagName) is null)
            activity.SetTag(CorrelationContext.TagName, id);
    }
}
