var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

var products = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase)
{
    ["coffee-mug"] = new("coffee-mug", "Aspire coffee mug", 18.50m),
    ["canvas-bag"] = new("canvas-bag", "Modular canvas bag", 24.00m)
};

app.MapGet("/products/{id}", (string id) =>
    products.TryGetValue(id, out var product)
        ? Results.Ok(product)
        : Results.NotFound());
app.MapHealthChecks("/health");

app.Run();

internal sealed record Product(string Id, string Name, decimal Price);
