using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var resultsDirectory = builder.Configuration["TestDemo:ResultsDirectory"] ?? "TestResults";
var api = builder.AddProject<Projects.TestProjects_Api>("api")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health");

builder.AddTestProject<Projects.TestProjects_Tests>("api-tests", new TestProjectOptions { ResultsDirectory = resultsDirectory })
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("TestDemo__Fail", builder.Configuration.GetValue<bool>("TestDemo:Fail").ToString())
    .WithEnvironment("TestDemo__Slow", builder.Configuration.GetValue<bool>("TestDemo:Slow").ToString());

await builder.Build().RunAsync();
