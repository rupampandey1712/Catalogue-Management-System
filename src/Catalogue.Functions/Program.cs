using Catalogue.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Reuse the application/infrastructure composition root: EF Core, supplier adapters.
builder.Services.AddCatalogueInfrastructure(builder.Configuration);
builder.Services.AddSingleton<Catalogue.Functions.CatalogueDbContextFactory>();
builder.Services.AddSingleton<Catalogue.Functions.SupplierClientResolver>();

builder.Build().Run();
