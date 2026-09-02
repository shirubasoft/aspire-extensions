var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", (IConfiguration configuration) => Results.Ok(new
{
    service = "dependency-gateway",
    api = configuration["UPSTREAM_API"],
    staticSite = configuration["UPSTREAM_STATIC"],
    generatedStaticSite = configuration["UPSTREAM_GENERATED_STATIC"],
    message = configuration["MODULE_MESSAGE"]
}));

app.MapGet("/health", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var dependencies = new[]
    {
        configuration["UPSTREAM_API"],
        configuration["UPSTREAM_STATIC"],
        configuration["UPSTREAM_GENERATED_STATIC"]
    };

    if (dependencies.Any(string.IsNullOrWhiteSpace))
    {
        return Results.Problem("One or more upstream references were not injected.", statusCode: 503);
    }

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var probes = dependencies.Select(async dependency =>
    {
        try
        {
            using var response = await client.GetAsync(dependency, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    });

    return (await Task.WhenAll(probes)).All(healthy => healthy)
        ? Results.Ok(new { status = "Healthy" })
        : Results.Problem("At least one upstream is unhealthy.", statusCode: 503);
});

app.Run();
