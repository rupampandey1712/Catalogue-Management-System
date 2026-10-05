using System.Net.Http.Json;
using Catalogue.Application;
using Catalogue.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Catalogue.Infrastructure.Suppliers;

/// <summary>Deterministic fake used by tests and offline demos.</summary>
public sealed class FakeSupplierProductClient : ISupplierProductClient
{
    public Task<IReadOnlyList<SupplierProductDto>> GetProductsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SupplierProductDto>>([
            new("SUP-1001", "Demo Heat Pump", 1299.99m, "GBP", 25, "8kW air source heat pump", "HVAC"),
            new("SUP-1002", "Demo Thermostat", 89.50m, "GBP", 140, "Smart programmable thermostat", "CONTROLS")
        ]);
}

/// <summary>DummyJSON-backed demo supplier adapter ("Supplier-Demo-01").</summary>
public sealed class DummyJsonSupplierProductClient : ISupplierProductClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DummyJsonSupplierProductClient> _logger;

    public DummyJsonSupplierProductClient(HttpClient http, IConfiguration config,
        ILogger<DummyJsonSupplierProductClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.BaseAddress ??= new Uri(config["SupplierApis:DummyJsonBaseUrl"] ?? "https://dummyjson.com");
    }

    public async Task<IReadOnlyList<SupplierProductDto>> GetProductsAsync(CancellationToken ct)
    {
        var response = await _http.GetFromJsonAsync<DummyJsonResponse>("/products?limit=30", ct)
            ?? new DummyJsonResponse([]);
        _logger.LogInformation("DummyJSON supplier returned {Count} products", response.Products.Count);
        return response.Products.Select(p => new SupplierProductDto(
            $"DJ-{p.Id}", p.Title, p.Price, "USD", p.Stock, p.Description, p.Category)).ToList();
    }

    private sealed record DummyJsonResponse(List<DummyJsonProduct> Products);
    private sealed record DummyJsonProduct(int Id, string Title, string? Description,
        decimal Price, int Stock, string? Category);
}
