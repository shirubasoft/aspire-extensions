using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class CliFormatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportedFilesMatchPinnedAspireCliJson(bool empty)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        var token = timeout.Token;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/api/telemetry/{signal}", async (string signal, HttpContext context) =>
        {
            Assert.Equal("fixture-key", context.Request.Headers["x-api-key"].ToString());
            var response = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", signal + ".json"), token);
            if (empty && signal != "resources")
            {
                response = "{\"data\":{},\"totalCount\":0,\"returnedCount\":0}";
            }
            return Results.Text(response, "application/json");
        });
        await server.StartAsync(token);
        var url = Assert.Single(server.Urls);
        using var client = DashboardTelemetry.CreateClient(new Uri(url), "fixture-key");
        var directory = Directory.CreateTempSubdirectory("aspire-cli-format-").FullName;
        try
        {
            var errors = new List<string>();
            await DashboardTelemetry.ExportResponsesAsync(client, directory, errors, token);
            Assert.Empty(errors);
            foreach (var signal in new[] { "logs", "traces" })
            {
                var expected = JsonNode.Parse(await RunCliAsync(signal, url, token));
                var actual = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, signal + ".json"), token));
                var original = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", signal + ".json"), token));
                var imported = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "otlp", signal + ".json"), token));
                Assert.True(JsonNode.DeepEquals(empty ? new JsonObject() : original!["data"], imported),
                    $"{signal} import file must retain the original OTLP payload.");
                Assert.IsType<JsonArray>(actual);
                Assert.True(JsonNode.DeepEquals(expected, actual), $"{signal} differs from aspire otel --format Json.\nExpected: {expected}\nActual: {actual}");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"resourceLogs\":null,\"resourceSpans\":null}}")]
    public void MissingTelemetryProducesEmptyCliArrays(string? json)
    {
        var response = json is null ? null : JsonNode.Parse(json);
        var resources = new TelemetryResourceNames(null);
        Assert.Empty(CliLogJson.Convert(response, resources, "http://localhost"));
        Assert.Empty(CliTraceJson.Convert(response, resources, "http://localhost"));
    }

    private static async Task<string> RunCliAsync(string signal, string dashboardUrl, CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".config", "dotnet-tools.json")))
        {
            directory = directory.Parent;
        }
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory!.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "tool", "run", "aspire", "--", "otel", signal, "--format", "Json",
            "--dashboard-url", dashboardUrl, "--api-key", "fixture-key", "--limit", "2147483647", "--non-interactive", "--nologo" })
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
