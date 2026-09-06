using Microsoft.Extensions.Configuration;
using Xunit;

namespace TestProjects.Sample;

public sealed class ApiTests
{
    private readonly IConfiguration _configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

    [Fact]
    public async Task GreetingComesFromTheReferencedApi()
    {
        using var client = CreateClient();
        var greeting = await client.GetStringAsync("/greeting", TestContext.Current.CancellationToken);
        Assert.Equal("Hello from Aspire", greeting);
    }

    [Fact]
    public void FailsWhenRequested()
    {
        Assert.False(_configuration.GetValue<bool>("TestDemo:Fail"), "Intentional demonstration failure. Filter out FailsWhenRequested to see a successful rerun.");
    }

    [Fact(Skip = "Demonstrates a skipped test in the Markdown report.")]
    public void GreetingForAnotherLanguage() => Assert.Fail("The localized greeting is not implemented.");

    [Fact]
    [Trait("Category", "Slow")]
    public async Task CanBeCanceled()
    {
        Assert.SkipUnless(_configuration.GetValue<bool>("TestDemo:Slow"), "Enable TestDemo:Slow to demonstrate cancellation.");
        using var client = CreateClient();
        using var response = await client.GetAsync("/slow", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient CreateClient()
    {
        var address = _configuration["services:api:http:0"];
        Assert.SkipWhen(string.IsNullOrWhiteSpace(address), "Run this suite through the sample AppHost to inject the API reference.");
        return new() { BaseAddress = new Uri(address) };
    }
}
