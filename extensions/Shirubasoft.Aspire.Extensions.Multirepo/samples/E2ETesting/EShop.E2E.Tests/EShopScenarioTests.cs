using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using EShop.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EShop.E2E.Tests;

public sealed class EShopScenarioTests
{
    private const string AppHostMode = "apphost";
    private const string ComposeMode = "compose";
    private const string ModeEnvironmentVariableName = "ESHOP_E2E_MODE";
    private const string OrdersApiKey = "e2e-orders-key";
    private static readonly TimeSpan TestTimeout = Environment.GetEnvironmentVariable("CI") is null
        ? TimeSpan.FromMinutes(2)
        : TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Customer_can_order_a_product_from_the_catalog()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var builder = await CreateBuilderAsync(cancellationToken);
        var application = await StartApplicationAsync(builder, cancellationToken);
        using var catalog = application.CreateHttpClient(CatalogModule.ApiResourceName, "http");
        using var orders = application.CreateHttpClient(OrdersModule.ApiResourceName, "http");

        var product = await catalog.GetFromJsonAsync<Product>("/products/coffee-mug", cancellationToken);
        Assert.NotNull(product);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest(product.Id, Quantity: 2))
        };
        var configuration = application.Services.GetRequiredService<IConfiguration>();
        request.Headers.Add(
            "X-Orders-Api-Key",
            configuration.GetRequiredSection("Parameters:orders-api-key").Value);

        using var response = await orders.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<Order>(cancellationToken);

        Assert.NotNull(order);
        Assert.Equal(product.Id, order.ProductId);
        Assert.Equal(product.Name, order.ProductName);
        Assert.Equal(37.00m, order.Total);

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest(product.Id, Quantity: 0))
        };
        invalidRequest.Headers.Add(
            "X-Orders-Api-Key",
            configuration.GetRequiredSection("Parameters:orders-api-key").Value);

        using var invalidResponse = await orders.SendAsync(invalidRequest, cancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidResponse.StatusCode);
    }

    private static async Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);
        var mode = Environment.GetEnvironmentVariable(ModeEnvironmentVariableName) ?? AppHostMode;
        return mode switch
        {
            AppHostMode => await DistributedApplicationTestingBuilder.CreateAsync<Projects.EShop_E2E_AppHost>(
                [$"Parameters:orders-api-key={OrdersApiKey}"],
                timeout.Token),
            ComposeMode => await DockerComposeDeploymentTestingBuilder
                .DeployAsync<Projects.EShop_E2E_AppHost>(timeout.Token),
            _ => throw new InvalidOperationException(
                $"Unsupported {ModeEnvironmentVariableName} value '{mode}'. Use '{AppHostMode}' or '{ComposeMode}'.")
        };
    }

    private static async Task<DistributedApplication> StartApplicationAsync(
        IDistributedApplicationTestingBuilder builder,
        CancellationToken cancellationToken)
    {
        var application = await builder.BuildAsync(cancellationToken)
            .WaitAsync(TestTimeout, cancellationToken);

        try
        {
            await application.StartAsync(cancellationToken)
                .WaitAsync(TestTimeout, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TestTimeout);
            await application.ResourceNotifications.WaitForResourceHealthyAsync(
                CatalogModule.ApiResourceName,
                timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync(
                OrdersModule.ApiResourceName,
                timeout.Token);
            return application;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    private sealed record Product(string Id, string Name, decimal Price);

    private sealed record CreateOrderRequest(string ProductId, int Quantity);

    private sealed record Order(
        Guid Id,
        string ProductId,
        string ProductName,
        int Quantity,
        decimal Total);
}
