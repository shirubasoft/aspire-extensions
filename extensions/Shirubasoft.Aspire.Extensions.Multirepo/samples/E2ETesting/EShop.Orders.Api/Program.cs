using System.Net;
using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient();

var app = builder.Build();

app.MapPost("/orders", async (
    CreateOrderRequest order,
    HttpRequest request,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var expectedApiKey = configuration["Orders:ApiKey"];
    if (string.IsNullOrEmpty(expectedApiKey) ||
        !string.Equals(request.Headers["X-Orders-Api-Key"], expectedApiKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    if (order.Quantity <= 0)
    {
        return Results.BadRequest(new { Error = "Quantity must be greater than zero." });
    }

    var catalogEndpoint = configuration["Catalog:Endpoint"]
        ?? throw new InvalidOperationException("Catalog:Endpoint was not supplied by the AppHost.");
    var productUri = new Uri(
        new Uri(catalogEndpoint, UriKind.Absolute),
        $"products/{Uri.EscapeDataString(order.ProductId)}");

    using var client = httpClientFactory.CreateClient();
    using var catalogResponse = await client.GetAsync(productUri, cancellationToken);
    if (catalogResponse.StatusCode == HttpStatusCode.NotFound)
    {
        return Results.NotFound();
    }

    catalogResponse.EnsureSuccessStatusCode();
    var product = await catalogResponse.Content.ReadFromJsonAsync<Product>(cancellationToken)
        ?? throw new InvalidOperationException("Catalog returned an empty product response.");

    return Results.Ok(new Order(
        Id: Guid.NewGuid(),
        ProductId: product.Id,
        ProductName: product.Name,
        Quantity: order.Quantity,
        Total: product.Price * order.Quantity));
});

app.MapHealthChecks("/health");

app.Run();

internal sealed record CreateOrderRequest(string ProductId, int Quantity);

internal sealed record Product(string Id, string Name, decimal Price);

internal sealed record Order(
    Guid Id,
    string ProductId,
    string ProductName,
    int Quantity,
    decimal Total);
