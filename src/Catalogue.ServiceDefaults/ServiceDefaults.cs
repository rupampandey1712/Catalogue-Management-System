using System.Text.Json;
using Catalogue.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace Catalogue.ServiceDefaults;

/// <summary>
/// Cross-cutting defaults shared by the API, worker and gateway (Aspire "ServiceDefaults" style):
/// structured logging, OpenTelemetry, correlation IDs, HTTP resilience, health checks and
/// problem-details error handling.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public const string LiveTag = "live";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddStructuredLogging(serviceName);
        builder.AddOpenTelemetry(serviceName);

        builder.Services.AddTransient<CorrelationIdHandler>();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Retry (exponential + jitter) -> circuit breaker -> attempt/total timeouts on every HttpClient.
            http.AddStandardResilienceHandler();
            http.AddHttpMessageHandler<CorrelationIdHandler>();
        });

        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);
        // Every problem response (incl. framework 401/404/415 ones) carries the IDs support needs.
        builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Extensions.TryAdd("correlationId", CorrelationContext.Current ?? ctx.HttpContext.TraceIdentifier);
            ctx.ProblemDetails.Extensions.TryAdd("traceId", System.Diagnostics.Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier);
        });
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        return builder;
    }

    // ----- logging -----

    private static void AddStructuredLogging(this IHostApplicationBuilder builder, string serviceName)
    {
        var config = builder.Configuration;
        var json = config.GetValue("Logging:Json", !builder.Environment.IsDevelopment());
        var filePath = config["Logging:File:Path"];
        var otlp = config["OTEL_EXPORTER_OTLP_ENDPOINT"];

        builder.Services.AddSerilog((services, lc) =>
        {
            lc.ReadFrom.Configuration(config)
              .ReadFrom.Services(services)
              .MinimumLevel.Information()
              .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
              .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
              .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
              .MinimumLevel.Override("Azure", LogEventLevel.Warning)
              .MinimumLevel.Override("Yarp", LogEventLevel.Warning)
              .MinimumLevel.Override("Polly", LogEventLevel.Warning)
              .Enrich.FromLogContext()
              .Enrich.With<CorrelationIdEnricher>()
              .Enrich.WithSpan()
              .Enrich.WithMachineName()
              .Enrich.WithThreadId()
              .Enrich.WithProperty("Application", serviceName)
              .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName);

            if (json)
                lc.WriteTo.Console(new RenderedCompactJsonFormatter());
            else
                lc.WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {SourceContext}{NewLine}      {Message:lj}{NewLine}{Exception}");

            if (!string.IsNullOrWhiteSpace(filePath))
                lc.WriteTo.File(new CompactJsonFormatter(), filePath, rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14, fileSizeLimitBytes: 100 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true);

            if (!string.IsNullOrWhiteSpace(otlp))
                lc.WriteTo.OpenTelemetry(o =>
                {
                    o.Endpoint = otlp;
                    o.Protocol = otlp.Contains(":4318") ? OtlpProtocol.HttpProtobuf : OtlpProtocol.Grpc;
                    o.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName,
                        ["deployment.environment"] = builder.Environment.EnvironmentName
                    };
                });
        });
    }

    // ----- OpenTelemetry -----

    private static void AddOpenTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        // Azure SDKs (Event Hubs, Service Bus, Blob) emit spans only behind this switch.
        AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: typeof(ServiceDefaultsExtensions).Assembly.GetName().Version?.ToString())
                .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)]))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.RecordException = true;
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                })
                .AddHttpClientInstrumentation(o => o.RecordException = true)
                .AddSqlClientInstrumentation()
                .AddSource(CatalogueDiagnostics.SourceName, "Azure.*", "Yarp.ReverseProxy",
                    "Catalogue.Agents", "Microsoft.Agents.AI*", "Experimental.Microsoft.Agents.AI*", "Experimental.Microsoft.Extensions.AI*")
                .AddProcessor(new CorrelationIdActivityProcessor()))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(CatalogueDiagnostics.SourceName, "Polly", "Microsoft.AspNetCore.RateLimiting", "Microsoft.Extensions.Caching.Hybrid",
                    "Catalogue.Agents", "Experimental.Microsoft.Extensions.AI*", "Experimental.Microsoft.Agents.AI*"));

        // Exports only when an OTLP endpoint is configured (Aspire dashboard, Jaeger, Grafana, App Insights...).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();
    }

    // ----- pipeline -----

    /// <summary>Correlation ID first, then request logging and exception handling.</summary>
    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            o.GetLevel = (ctx, elapsed, ex) =>
                ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
                : ctx.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : elapsed > 2000 ? LogEventLevel.Warning
                : LogEventLevel.Information;
            o.EnrichDiagnosticContext = (diag, ctx) =>
            {
                diag.Set("ClientIp", ctx.Connection.RemoteIpAddress?.ToString());
                diag.Set("User", ctx.User.Identity?.Name ?? ctx.User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ?? "anonymous");
                diag.Set("UserAgent", ctx.Request.Headers.UserAgent.ToString());
            };
        });
        return app;
    }

    /// <summary>/health/live = process is up; /health/ready = dependencies reachable.</summary>
    public static WebApplication MapDefaultHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains(LiveTag), ResponseWriter = WriteJson });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = WriteJson });
        return app;
    }

    private static Task WriteJson(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                error = e.Value.Exception?.Message
            })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
