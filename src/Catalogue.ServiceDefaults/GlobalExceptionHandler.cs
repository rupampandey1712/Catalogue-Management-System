using System.Diagnostics;
using Catalogue.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Catalogue.ServiceDefaults;

/// <summary>
/// Single place that turns unhandled exceptions into RFC 7807 problem responses. Every response
/// carries the correlation ID and trace ID so a user-reported error maps straight to logs/traces.
/// Exception details are only exposed in Development.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment env,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, retryAfter) = Classify(exception, context);

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception on {Method} {Path} -> {Status}",
                context.Request.Method, context.Request.Path, status);
        else
            logger.LogWarning("Request {Method} {Path} failed with {Status}: {Message}",
                context.Request.Method, context.Request.Path, status, exception.Message);

        if (context.Response.HasStarted) return false;

        context.Response.StatusCode = status;
        if (retryAfter is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString();

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpstatuses.io/{status}",
            Detail = status < 500 || env.IsDevelopment() ? exception.Message : "An unexpected error occurred. Quote the correlation ID when reporting it.",
            Instance = context.Request.Path
        };
        problem.Extensions["correlationId"] = CorrelationContext.Current ?? context.TraceIdentifier;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        // Back-compat with the SPA, which historically read `error`.
        problem.Extensions["error"] = problem.Detail;
        if (env.IsDevelopment() && status >= 500) problem.Extensions["exception"] = exception.GetType().FullName;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static (int Status, string Title, int? RetryAfter) Classify(Exception ex, HttpContext ctx) => ex switch
    {
        OperationCanceledException when ctx.RequestAborted.IsCancellationRequested => (499, "Client closed request", null),
        BadHttpRequestException b => (b.StatusCode, "Bad request", null),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found", null),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden", null),
        ArgumentException or FormatException => (StatusCodes.Status400BadRequest, "Invalid request", null),
        // Domain rule violations are conflicts; EF query-translation failures are server bugs.
        InvalidOperationException ioe when !ioe.Message.Contains("could not be translated") => (StatusCodes.Status409Conflict, "Conflict", null),
        BrokenCircuitException => (StatusCodes.Status503ServiceUnavailable, "Dependency temporarily unavailable", 30),
        TimeoutRejectedException or TimeoutException => (StatusCodes.Status503ServiceUnavailable, "Dependency timed out", 5),
        _ when ex.GetType().Name == "SqlException" => (StatusCodes.Status503ServiceUnavailable, "Database unavailable", 10),
        _ => (StatusCodes.Status500InternalServerError, "Server error", null)
    };
}
