#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

internal static class PipelineStepRunner
{
    /// <summary>
    /// Builds the AppHost and runs each step with its dependencies in order, so a step's failure propagates as it
    /// does from <c>aspire do</c>.
    /// </summary>
    public static async Task RunAsync(IDistributedApplicationBuilder builder, params string[] steps)
    {
        await using var application = builder.Build();
        var services = application.Services;
        var pipeline = services.GetRequiredService<IDistributedApplicationPipeline>();
        var options = services.GetRequiredService<IOptions<PipelineOptions>>().Value;
        var context = new PipelineContext(
            services.GetRequiredService<DistributedApplicationModel>(),
            services.GetRequiredService<DistributedApplicationExecutionContext>(),
            services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        foreach (var step in steps)
        {
            options.Step = step;
            await pipeline.ExecuteAsync(context);
        }
    }
}
