using Catalogue.Application;
using Catalogue.Infrastructure;
using Catalogue.Infrastructure.Persistence;
using Catalogue.ServiceDefaults;
using Catalogue.Workers;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults("catalogue-worker");
builder.Services.AddCatalogueInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddScoped<ImportProcessor>();
builder.Services.AddSingleton<IActorContext, WorkerActorContext>();
builder.Services.AddHostedService<ImportWorker>();

// Extra Event Hub consumers: every instance joins the consumer group and the processor
// load-balances partition ownership, so scaling the worker out scales ingestion.
if (builder.Configuration.GetValue<bool>("EventHub:Enabled") &&
    builder.Configuration.GetValue<bool>("EventHub:ConsumerInProcess"))
    builder.Services.AddCatalogueEventHub(builder.Configuration, builder.Environment.ContentRootPath, publisher: false);

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogueDbContext>();
    await db.Database.EnsureCreatedAsync();
}

host.Run();

internal sealed class WorkerActorContext : IActorContext
{
    public string ActorId => "worker";
    public string CorrelationId => CorrelationContext.Current ?? CorrelationContext.NewId();
}
