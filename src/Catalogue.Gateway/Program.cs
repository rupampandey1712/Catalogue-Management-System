using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Catalogue.Application;
using Catalogue.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Yarp.ReverseProxy.Transforms;

// YARP edge gateway: the single public entry point. Routes /api to the API cluster (load-balanced,
// active + passive health checks) and everything else to the SPA, and owns the cross-cutting edge
// concerns: correlation IDs, rate limiting, timeouts, request-size limits and distributed tracing.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("catalogue-gateway");

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(ctx =>
    {
        // Make the hop visible to the backend and the browser.
        ctx.AddRequestHeader("X-Forwarded-Gateway", "catalogue-gateway");
        ctx.AddResponseTransform(t =>
        {
            t.HttpContext.Response.Headers["X-Served-By"] = "catalogue-gateway";
            if (CorrelationContext.Current is { } id) t.HttpContext.Response.Headers[CorrelationContext.HeaderName] = id;
            return ValueTask.CompletedTask;
        });
    });

// A bearer token only identifies a user for rate limiting once its signature checks out; otherwise anyone
// could send a fresh random "token" per request and get a fresh bucket each time. Uses the same Jwt settings
// as the API. Without a signing key here, every caller is keyed by IP address.
var jwtKey = builder.Configuration["Jwt:SigningKey"];
var verifyTokens = !string.IsNullOrWhiteSpace(jwtKey);
if (verifyTokens)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = true;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "catalogue-dev",
                ValidateAudience = true,
                ValidAudience = builder.Configuration["Jwt:Audience"] ?? "catalogue-api",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey!))
            };
        });
}

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "10";
        ctx.HttpContext.Response.ContentType = "application/problem+json";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            status = 429,
            title = "Too many requests",
            detail = "Rate limit exceeded at the gateway. Retry shortly.",
            correlationId = CorrelationContext.Current
        }, ct);
    };

    // Per client: a verified token identifies a user; everyone else (including invalid tokens) is keyed by IP.
    static string ClientKey(HttpContext ctx) =>
        ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } user
            ? "u:" + user
            : "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    o.AddPolicy("api", ctx => RateLimitPartition.GetSlidingWindowLimiter(ClientKey(ctx), _ => new SlidingWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue("Gateway:RateLimit:PerMinute", 1200),
        Window = TimeSpan.FromMinutes(1),
        SegmentsPerWindow = 6,
        QueueLimit = 20,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    }));
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddRequestTimeouts(o =>
{
    o.AddPolicy("default", new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(60), TimeoutStatusCode = 504 });
    o.AddPolicy("long", new RequestTimeoutPolicy { Timeout = TimeSpan.FromMinutes(20), TimeoutStatusCode = 504 });
});

var app = builder.Build();

app.UseServiceDefaults();
if (verifyTokens) app.UseAuthentication(); // only populates HttpContext.User; the API still authorizes
app.UseRateLimiter();
app.UseRequestTimeouts();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "gateway" }));
app.MapDefaultHealthChecks();
app.MapReverseProxy();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
