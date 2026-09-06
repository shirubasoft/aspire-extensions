#pragma warning disable ASPIREPROCESSCOMMAND001 // Use the pinned Aspire process runner for cancellation and process-tree cleanup.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

internal static class TestProjectCommands
{
    internal static void Register(IResourceBuilder<TestProjectResource> builder, TestProjectOptions options)
    {
        var run = new TestRun(builder.Resource, options, builder.ApplicationBuilder.AppHostDirectory);
        var commandOptions = new ProcessCommandOptions
        {
            IconName = "Play",
            IsHighlighted = true,
            Description = "Build and run tests against this AppHost's resources.",
            Progress = new() { Title = "Running tests", Message = "Waiting for dependencies, building, and running tests…" },
            UpdateState = context => context.ResourceSnapshot.State?.Text == "Running" ? ResourceCommandState.Disabled : ResourceCommandState.Enabled,
            MaxOutputLineCount = 200,
            Arguments = FilterArguments(options.Runner),
            GetCommandResult = context => TestRunArtifacts.ReadResultAsync(context, run.DirectoryPath),
        };
        builder.WithProcessCommand("run-tests", "Run tests", context => CreateProcessAsync(builder, options, run, context), commandOptions);

        // Wrap Aspire's command to release the run gate even when process startup or cancellation fails.
        var processCommand = builder.Resource.Annotations.OfType<ResourceCommandAnnotation>().Last();
        builder.Resource.Annotations.Remove(processCommand);
        builder.WithCommand("run-tests", "Run tests", context => run.ExecuteAsync(context, processCommand.ExecuteCommand), commandOptions);
        builder.WithCommand("test-results", "Test results", _ => Task.FromResult(run.LastResult),
            new CommandOptions { IconName = "DocumentBulletList", Description = "Show the latest completed run, including failures and skipped tests." });
    }

    internal static IReadOnlyList<InteractionInput> FilterArguments(TestProjectRunner runner) =>
        runner == TestProjectRunner.VSTest
            ? [new() { Name = "filter", Label = "Test filter", InputType = InputType.Text, Required = false, Placeholder = "Optional VSTest filter expression" }]
            : [];

    private static async ValueTask<ProcessCommandSpec> CreateProcessAsync(
        IResourceBuilder<TestProjectResource> builder, TestProjectOptions options, TestRun run, ExecuteCommandContext context)
    {
        await context.Services.GetRequiredService<ResourceNotificationService>()
            .WaitForDependenciesAsync(builder.Resource, context.CancellationToken);
        var configuration = await ExecutionConfigurationBuilder.Create(builder.Resource)
            .WithEnvironmentVariablesConfig().WithArgumentsConfig()
            .BuildAsync(builder.ApplicationBuilder.ExecutionContext, context.Logger, context.CancellationToken);
        if (configuration.Exception is not null)
        {
            throw new InvalidOperationException("Could not resolve the test project's resource references.", configuration.Exception);
        }

        return new("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(builder.Resource.ProjectPath),
            EnvironmentVariables = configuration.EnvironmentVariables.ToDictionary(),
            Arguments = TestRunnerArguments.Create(builder.Resource.ProjectPath, options, run.DirectoryPath,
                options.Runner == TestProjectRunner.VSTest ? context.Arguments.GetString("filter") : null, configuration.Arguments.Select(argument => argument.Value)),
        };
    }
}
