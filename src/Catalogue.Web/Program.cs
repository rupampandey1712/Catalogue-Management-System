using Catalogue.Agents;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Infrastructure;
using Catalogue.Infrastructure.Persistence;
using Catalogue.ServiceDefaults;
using Catalogue.Web;
using Catalogue.Web.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Logging (Serilog), OpenTelemetry, correlation IDs, HttpClient resilience, ProblemDetails, health.
builder.AddServiceDefaults("catalogue-api");

builder.Services.AddCatalogueInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddCatalogueMediaFetcher();
builder.Services.AddCataloguePayments(builder.Configuration);
builder.Services.AddCatalogueAgents(builder.Configuration);
builder.Services.AddScoped<Catalogue.Web.Agent.AgentPrincipalFactory>();
builder.Services.AddScoped<Catalogue.Web.Agent.AgentActionExecutor>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActorContext, HttpActorContext>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<ImportProcessor>();
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<MarketplacePricing>();
builder.Services.AddScoped<SourcingService>();
builder.Services.AddScoped<OrderStatusService>();
builder.Services.AddScoped<ImportReprocessService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddCatalogueEventHub(builder.Configuration, builder.Environment.ContentRootPath);

// --- Caching: HybridCache = in-process L1 + optional Redis L2, stampede protection, tag invalidation ---
if (builder.Configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
{
    builder.Services.AddStackExchangeRedisCache(o => { o.Configuration = redis; o.InstanceName = "catalogue:"; });
    // Decorate Redis with timeout + circuit breaker so a cache outage never slows or fails requests.
    var redisDescriptor = builder.Services.Last(d => d.ServiceType == typeof(Microsoft.Extensions.Caching.Distributed.IDistributedCache));
    builder.Services.Remove(redisDescriptor);
    builder.Services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(sp => new ResilientDistributedCache(
        (Microsoft.Extensions.Caching.Distributed.IDistributedCache)(redisDescriptor.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, redisDescriptor.ImplementationType!)),
        sp.GetRequiredService<ILogger<ResilientDistributedCache>>()));
}
builder.Services.AddHybridCache(o =>
{
    o.MaximumPayloadBytes = 4 * 1024 * 1024;
    o.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };
});

// --- Fault tolerance at the edge: request timeouts + login brute-force protection ---
builder.Services.AddRequestTimeouts(o =>
{
    o.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(30), TimeoutStatusCode = StatusCodes.Status504GatewayTimeout };
    o.AddPolicy(Policies.LongRunning, new RequestTimeoutPolicy { Timeout = TimeSpan.FromMinutes(15), TimeoutStatusCode = StatusCodes.Status504GatewayTimeout });
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(Policies.Register, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    o.AddPolicy(Policies.Agent, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    o.AddPolicy(Policies.Login, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

// Large EDI interchanges can be uploaded for Event Hub streaming (up to 2 GB, spooled to disk).
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 30L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = Catalogue.Application.Streaming.EdiStreamService.MaxUploadBytes + 1024 * 1024);
if (builder.Configuration.GetValue<bool>("Worker:InProcess", defaultValue: true))
    builder.Services.AddHostedService<LocalImportWorker>();

// --- Authentication: our own JWT issuer (swap authority/audience config for Entra ID) ---
var jwt = JwtSettings.From(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>(); // PBKDF2-SHA512, 100k iterations
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = true;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwt.Key),
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("CatalogueAdmin", p => p.RequireRole(AppRoles.Admin));
    o.AddPolicy("PlatformAdmin", p => p.RequireRole(AppRoles.Admin));
    o.AddPolicy("SupplierAdmin", p => p.RequireRole(AppRoles.Admin, AppRoles.SupplierManager));
    o.AddPolicy("ImportOps", p => p.RequireRole(AppRoles.Admin, AppRoles.SupplierManager, AppRoles.Support));
    o.AddPolicy("ReadCatalogue", p => p.RequireRole(AppRoles.Admin, AppRoles.SupplierManager, AppRoles.Buyer, AppRoles.Seller,
        AppRoles.Support, AppRoles.Viewer));
    // Marketplace: companies buy (Buyer) and sell (Seller); the platform admin can act for its own company.
    o.AddPolicy("Buyer", p => p.RequireRole(AppRoles.Buyer, AppRoles.Admin));
    o.AddPolicy("Seller", p => p.RequireRole(AppRoles.Seller, AppRoles.Admin, AppRoles.SupplierManager));
    o.AddPolicy("Marketplace", p => p.RequireRole(AppRoles.Buyer, AppRoles.Seller, AppRoles.Admin, AppRoles.SupplierManager, AppRoles.Support));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CatalogueDbContext>("database")
    .AddCheck<EventHubHealthCheck>("eventhub")
    .AddCheck<Catalogue.Web.Agent.FoundryLocalHealthCheck>("local-model");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("X-Total-Count", CorrelationContext.HeaderName, "traceparent")));

var app = builder.Build();

app.UseServiceDefaults();

// Baseline headers for a JSON API: no MIME sniffing, no framing, no Referer leakage, and nothing may execute
// in this origin. Swagger UI (Development) runs its own scripts, and signed media (images, PDFs opened in a
// tab) must render in the browser's viewers, so both are left out of the CSP.
app.Use((ctx, next) =>
{
    ctx.Response.OnStarting(static state =>
    {
        var http = (HttpContext)state;
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        if (!http.Request.Path.StartsWithSegments("/swagger") && !http.Request.Path.StartsWithSegments("/api/media"))
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        return Task.CompletedTask;
    }, ctx);
    return next(ctx);
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseRequestTimeouts();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogueDbContext>();
    await db.SeedAsync();
    // The demo users have published, trivially guessable passwords (admin/admin123), so they are only
    // created in Development unless a deployment opts in explicitly with Seed:DemoUsers=true.
    if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Seed:DemoUsers"))
        await IdentitySeed.SeedAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>());
    else if (!await db.Users.AnyAsync())
        app.Logger.LogWarning("No users exist and demo users are not seeded outside Development. " +
            "Set Seed:DemoUsers=true for a demo deployment, or create the first administrator directly.");

    // Recovery sweep: a restart can strand imports mid-processing (in-memory queue loses
    // the message). Mark them failed so they become reprocessable. Streamed imports survive a
    // restart when backed by a real Event Hub (events are durable and checkpointed).
    var durableStreams = app.Configuration.GetValue<bool>("EventHub:Enabled");
    var stranded = await db.ImportFiles
        .Where(i => i.Status == ImportStatus.Processing)
        .Where(i => !durableStreams || i.Format != ImportFormats.EdiStream)
        .ToListAsync();
    foreach (var s in stranded)
        s.Fail("Reset on startup: worker restarted while processing; safe to reprocess.");
    if (stranded.Count > 0)
    {
        await db.SaveChangesAsync();
        app.Logger.LogWarning("Recovery sweep reset {Count} stranded imports.", stranded.Count);
    }
}

app.MapGet("/health", (Catalogue.Application.Streaming.IEventHubProducer hub) =>
    Results.Ok(new { status = "ok", eventHub = hub.Mode })).DisableRequestTimeout();
app.MapDefaultHealthChecks();

var api = app.MapGroup("/api");
Catalogue.Web.Endpoints.AuthEndpoints.Map(api);
Catalogue.Web.Endpoints.ProductEndpoints.Map(api);
Catalogue.Web.Endpoints.CategoryEndpoints.Map(api);
Catalogue.Web.Endpoints.SupplierEndpoints.Map(api);
Catalogue.Web.Endpoints.ImportEndpoints.Map(api);
Catalogue.Web.Endpoints.EventHubEndpoints.Map(api);
Catalogue.Web.Endpoints.DashboardEndpoints.Map(api);
Catalogue.Web.Endpoints.ApprovalEndpoints.Map(api);
Catalogue.Web.Endpoints.PricingEndpoints.Map(api);
Catalogue.Web.Endpoints.MediaEndpoints.Map(api);
Catalogue.Web.Endpoints.OrganizationEndpoints.Map(api);
Catalogue.Web.Endpoints.MarketplaceEndpoints.Map(api);
Catalogue.Web.Endpoints.PaymentEndpoints.Map(api);
Catalogue.Web.Endpoints.AgentEndpoints.Map(api);
Catalogue.Web.Endpoints.AnalyticsEndpoints.Map(api);

try
{
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { } // for WebApplicationFactory in tests
