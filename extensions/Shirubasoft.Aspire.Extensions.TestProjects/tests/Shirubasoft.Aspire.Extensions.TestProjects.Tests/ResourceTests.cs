using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class ResourceTests
{
    [Fact]
    public async Task ReferencesWaitsAndArgumentsUseAspireAnnotations()
    {
        var builder = DistributedApplication.CreateBuilder();
        var database = builder.AddConnectionString("database", "Server=example");
        var api = builder.AddExecutable("api", "dotnet", ".").WithHttpEndpoint();
        var tests = builder.AddTestProject("tests", "../Tests/Tests.csproj")
            .WithReference(database).WithReference(api.GetEndpoint("http")).WaitFor(api).WithArgs("--no-restore");
        Assert.Equal(Path.GetFullPath("../Tests/Tests.csproj", builder.AppHostDirectory), tests.Resource.ProjectPath);
        Assert.Contains(ManifestPublishingCallbackAnnotation.Ignore, tests.Resource.Annotations);
        Assert.Same(api.Resource, Assert.Single(tests.Resource.Annotations.OfType<WaitAnnotation>()).Resource);
        var commands = tests.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.Equal(["run-tests", "test-results"], commands.Select(command => command.Name));
        Assert.NotNull(commands[0].Progress);
        Assert.Single(commands[0].Arguments);
        var env = await ExecutionConfigurationBuilder.Create(tests.Resource).WithArgumentsConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Equal("--no-restore", Assert.Single(env.Arguments).Value);
    }

    [Fact]
    public void MetadataOverloadAndMtpOptionsWork()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddTestProject<TestMetadata>("mtp", new() { Runner = TestProjectRunner.MicrosoftTestingPlatform });
        Assert.Equal(Path.GetFullPath("Tests.csproj"), resource.Resource.ProjectPath);
        Assert.Empty(resource.Resource.Annotations.OfType<ResourceCommandAnnotation>().First().Arguments);
    }

    [Fact]
    public void InvalidRegistrationFailsBeforeAddingAResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        Assert.Throws<ArgumentNullException>(() => TestProjectBuilderExtensions.AddTestProject(null!, "tests", "tests.csproj"));
        Assert.Throws<ArgumentException>(() => builder.AddTestProject("tests", ""));
        Assert.Throws<ArgumentException>(() => new TestProjectResource("tests", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddTestProject("tests", "a.csproj", new() { Runner = (TestProjectRunner)99 }));
        Assert.Throws<ArgumentException>(() => builder.AddTestProject("tests", "a.csproj", new() { Configuration = "" }));
        Assert.Throws<ArgumentException>(() => builder.AddTestProject("tests", "a.csproj", new() { ResultsDirectory = "" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddTestProject("tests", "a.csproj", new() { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddTestProject("tests", "a.csproj", new() { Timeout = TimeSpan.MaxValue }));
        Assert.Empty(builder.Resources);
    }

    [Fact]
    public void RunnerArgumentsPreservePathsAndSelectTheRightTrxReporter()
    {
        var vstest = TestRunnerArguments.Create("path with spaces.csproj", new(), "reports with spaces", "FullyQualifiedName~Hello", ["--no-restore"]);
        Assert.Contains("path with spaces.csproj", vstest);
        Assert.Contains("trx;LogFilePrefix=results", vstest);
        Assert.Equal(["--filter", "FullyQualifiedName~Hello"], vstest[^2..]);
        Assert.DoesNotContain("--filter", TestRunnerArguments.Create("p", new(), "r", null, []));
        var mtp = TestRunnerArguments.Create("p", new() { Runner = TestProjectRunner.MicrosoftTestingPlatform }, "r", null, ["--minimum-expected-tests", "1"]);
        Assert.Equal("run", mtp[0]);
        Assert.Contains("--no-launch-profile", mtp);
        Assert.Contains("--report-trx", mtp);
        Assert.Contains("--minimum-expected-tests", mtp);
    }

    public sealed class TestMetadata : IProjectMetadata
    {
        public string ProjectPath => Path.GetFullPath("Tests.csproj");
    }
}
