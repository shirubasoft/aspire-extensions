using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class ResourceTests
{
    [Fact]
    public async Task NativeProjectHasExplicitStartupReferencesAndMtpReporting()
    {
        using var directory = new TestDirectory();
        var builder = DistributedApplication.CreateBuilder();
        var database = builder.AddConnectionString("database", "Server=example");
        var api = builder.AddExecutable("api", "dotnet", ".").WithHttpEndpoint();
        var tests = builder.AddTestProject("tests", new Projects.TestProjects_Tests().ProjectPath, new() { ResultsDirectory = directory.Path })
            .WithReference(database).WithReference(api.GetEndpoint("http")).WaitFor(api).WithArgs("--filter-method", "*Greeting*");
        Assert.IsType<ProjectResource>(tests.Resource);
        Assert.Equal(Path.GetFullPath(new Projects.TestProjects_Tests().ProjectPath, builder.AppHostDirectory), tests.Resource.GetProjectMetadata().ProjectPath);
        Assert.Single(tests.Resource.Annotations.OfType<ExplicitStartupAnnotation>());
        Assert.Contains(ManifestPublishingCallbackAnnotation.Ignore, tests.Resource.Annotations);
        Assert.Same(api.Resource, Assert.Single(tests.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        var commands = tests.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.Contains(commands, command => command.Name == "run-tests");
        Assert.Contains(commands, command => command.Name == "test-results");
        Assert.All(commands, command => Assert.Empty(command.Arguments));
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tests.Resource, app.Services), TestContext.Current.CancellationToken);
        var config = await ExecutionConfigurationBuilder.Create(tests.Resource).WithArgumentsConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Contains(config.Arguments, arg => arg.Value == "--report-trx");
        Assert.Contains(config.Arguments, arg => arg.Value == "--filter-method");
        Assert.Contains(config.Arguments, arg => arg.Value == "*Greeting*");
        Assert.Single(Directory.GetDirectories(Path.Combine(directory.Path, "tests")));
    }

    [Fact]
    public void MetadataOverloadUsesNativeProjectMetadata()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddTestProject<TestMetadata>("tests");
        Assert.Equal(new Projects.TestProjects_Tests().ProjectPath, resource.Resource.GetProjectMetadata().ProjectPath);
        Assert.Single(resource.Resource.Annotations.OfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public void InvalidRegistrationFailsBeforeAddingAResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        Assert.Throws<ArgumentNullException>(() => TestProjectBuilderExtensions.AddTestProject(null!, "tests", "tests.csproj"));
        Assert.Throws<ArgumentException>(() => builder.AddTestProject("tests", ""));
        Assert.Throws<ArgumentException>(() => builder.AddTestProject("tests", "a.csproj", new() { ResultsDirectory = "" }));
        Assert.Empty(builder.Resources);
    }

    [Fact]
    public void RunCommandFollowsNativeResourceStates()
    {
        Assert.True(TestProjectCommands.CanStart(KnownResourceStates.NotStarted));
        Assert.True(TestProjectCommands.CanStart(KnownResourceStates.Exited));
        Assert.True(TestProjectCommands.CanStart(KnownResourceStates.Finished));
        Assert.True(TestProjectCommands.CanStart(KnownResourceStates.FailedToStart));
        Assert.False(TestProjectCommands.CanStart(KnownResourceStates.Running));
        Assert.False(TestProjectCommands.CanStart(null));
    }

    public sealed class TestMetadata : IProjectMetadata
    {
        public string ProjectPath => new Projects.TestProjects_Tests().ProjectPath;
    }
}
