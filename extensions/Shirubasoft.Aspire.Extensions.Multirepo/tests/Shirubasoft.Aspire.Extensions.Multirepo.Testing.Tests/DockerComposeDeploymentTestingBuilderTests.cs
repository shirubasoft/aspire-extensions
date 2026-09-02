using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;

namespace Aspire.Hosting.Multirepo.Testing.Tests;

public sealed class DockerComposeDeploymentTestingBuilderTests
{
    [Fact]
    public async Task Create_imports_endpoints_and_configuration_into_an_Aspire_testing_builder()
    {
        var endpointName = Encode("catalog-api");
        var httpEndpointName = Encode("public");
        var adminEndpointName = Encode("admin");
        var configurationKey = Encode("Parameters:orders-api-key");
        using var file = TemporaryFile.Create($$"""
            # External test endpoint catalog-api
            ASPIRE_TEST_ENDPOINT__{{endpointName}}__{{httpEndpointName}}=http://localhost:5101/
            ASPIRE_TEST_ENDPOINT_HEALTH_PATH__{{endpointName}}__{{httpEndpointName}}=/health
            ASPIRE_TEST_ENDPOINT__{{endpointName}}__{{adminEndpointName}}=https://localhost:5102/

            # External test configuration value Parameters:orders-api-key
            ASPIRE_TEST_VALUE__{{configurationKey}}=secret=with=separators
            """);

        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);

        Assert.IsAssignableFrom<IDistributedApplicationTestingBuilder>(builder);
        Assert.Equal("secret=with=separators", builder.Configuration["Parameters:orders-api-key"]);
        Assert.True(builder.Resources.TryGetByName("catalog-api", out var resource));
        Assert.IsAssignableFrom<IResourceWithEndpoints>(resource);

        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(endpoint => endpoint.Name);
        Assert.Equal(["admin", "public"], endpoints.Keys.Order());
        Assert.Equal("localhost", endpoints["public"].AllocatedEndpoint?.Address);
        Assert.Equal(5101, endpoints["public"].AllocatedEndpoint?.Port);
        Assert.Equal(5102, endpoints["admin"].AllocatedEndpoint?.Port);
        Assert.Contains(resource.Annotations, annotation => annotation is HealthCheckAnnotation);

        await using var application = await builder.BuildAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Create_parses_dotenv_quotes_exports_comments_and_https_without_health_checks()
    {
        var endpointName = Encode("orders-api");
        var configurationKey = Encode("Feature:Mode");
        var literalKey = Encode("Feature:Literal");
        var enabledKey = Encode("Feature:Enabled");
        using var file = TemporaryFile.Create(
            $"\uFEFF   # generated values{Environment.NewLine}" +
            $"export ASPIRE_TEST_VALUE__{configurationKey}=\"second=with=equals  \" # selected{Environment.NewLine}" +
            $"ASPIRE_TEST_VALUE__{literalKey}='literal # value'{Environment.NewLine}" +
            $"ASPIRE_TEST_VALUE__{enabledKey}=true # enabled{Environment.NewLine}" +
            $" ASPIRE_TEST_ENDPOINT__{endpointName}=https://example.test:5443/{Environment.NewLine}");

        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);

        Assert.Equal("second=with=equals  ", builder.Configuration["Feature:Mode"]);
        Assert.Equal("literal # value", builder.Configuration["Feature:Literal"]);
        Assert.Equal("true", builder.Configuration["Feature:Enabled"]);
        Assert.True(builder.Resources.TryGetByName("orders-api", out var resource));
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("https", endpoint.Name);
        Assert.Equal("example.test", endpoint.AllocatedEndpoint?.Address);
        Assert.Equal(5443, endpoint.AllocatedEndpoint?.Port);
        Assert.DoesNotContain(resource.Annotations, annotation => annotation is HealthCheckAnnotation);

        await builder.DisposeAsync();
    }

    [Theory]
    [InlineData("not-an-endpoint")]
    [InlineData("ftp://example.test:21/")]
    [InlineData("https://example.test:5443/base/")]
    [InlineData("https://user@example.test:5443/")]
    public void Create_rejects_an_invalid_or_unsupported_exported_uri(string uri)
    {
        var endpointName = Encode("catalog");
        using var file = TemporaryFile.Create($"ASPIRE_TEST_ENDPOINT__{endpointName}={uri}");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path));

        Assert.Contains("absolute HTTP(S) origin", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing separator")]
    [InlineData("DUPLICATE=first\nDUPLICATE=second")]
    [InlineData("BROKEN=\"unterminated")]
    public void Create_rejects_malformed_dotenv_content(string content)
    {
        using var file = TemporaryFile.Create(content.Replace("\\n", Environment.NewLine, StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path));

        Assert.Contains("environment file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_rejects_a_health_check_without_its_endpoint()
    {
        using var file = TemporaryFile.Create(
            $"ASPIRE_TEST_ENDPOINT_HEALTH_PATH__{Encode("catalog")}= /health");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path));

        Assert.Contains("without its endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//evil.example/health")]
    [InlineData("/\\evil.example/health")]
    public void Create_rejects_a_health_check_that_can_replace_the_endpoint_authority(string healthPath)
    {
        var resourceName = Encode("catalog");
        var endpointName = Encode("http");
        using var file = TemporaryFile.Create($$"""
            ASPIRE_TEST_ENDPOINT__{{resourceName}}__{{endpointName}}=http://localhost:5101/
            ASPIRE_TEST_ENDPOINT_HEALTH_PATH__{{resourceName}}__{{endpointName}}={{healthPath}}
            """);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path));

        Assert.Contains("root-relative URI path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_rejects_an_invalid_encoded_name()
    {
        using var file = TemporaryFile.Create("ASPIRE_TEST_VALUE__NOT_HEX=value");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path));

        Assert.Contains("invalid encoded name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateFromEnvironment_uses_the_configured_file()
    {
        using var file = TemporaryFile.Create(
            $"ASPIRE_TEST_VALUE__{Encode("Feature:Enabled")}=true");
        using var environment = EnvironmentVariable.Set(
            DockerComposeDeploymentTestingBuilder.FilePathEnvironmentVariableName,
            file.Path);

        var builder = DockerComposeDeploymentTestingBuilder
            .CreateFromEnvironment<DockerComposeDeploymentTestingBuilderTests>();

        Assert.Equal("true", builder.Configuration["Feature:Enabled"]);
        await builder.DisposeAsync();
    }

    [Fact]
    public void CreateFromEnvironment_requires_a_configured_file()
    {
        using var environment = EnvironmentVariable.Set(
            DockerComposeDeploymentTestingBuilder.FilePathEnvironmentVariableName,
            null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder
                .CreateFromEnvironment<DockerComposeDeploymentTestingBuilderTests>());

        Assert.Contains(
            DockerComposeDeploymentTestingBuilder.FilePathEnvironmentVariableName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_rejects_an_environment_name_that_cannot_be_an_env_file_suffix()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            DockerComposeDeploymentTestingBuilder.DeployAsync<DockerComposeDeploymentTestingBuilderTests>(
                "../CI",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("environmentName", exception.ParamName);
    }

    [Fact]
    public async Task DeployAsync_rejects_non_positive_timeouts()
    {
        var options = new DockerComposeDeploymentOptions { DeploymentTimeout = TimeSpan.Zero };

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DockerComposeDeploymentTestingBuilder.DeployAsync<DockerComposeDeploymentTestingBuilderTests>(
                options,
                TestContext.Current.CancellationToken));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task DeployAsync_rejects_a_negative_port_conflict_retry_count()
    {
        var options = new DockerComposeDeploymentOptions { PortConflictRetryCount = -1 };

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DockerComposeDeploymentTestingBuilder.DeployAsync<DockerComposeDeploymentTestingBuilderTests>(
                options,
                TestContext.Current.CancellationToken));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Default_cli_resolution_prefers_a_restored_local_tool_manifest()
    {
        using var repository = TemporaryDirectory.Create();
        var manifestDirectory = Path.Combine(repository.Path, ".config");
        var appHostPath = Path.Combine(repository.Path, "src", "AppHost", "AppHost.csproj");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(appHostPath)!);
        File.WriteAllText(
            Path.Combine(manifestDirectory, "dotnet-tools.json"),
            """
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "aspire.cli": {
                  "version": "13.4.6",
                  "commands": [ "aspire" ]
                }
              }
            }
            """);

        var invocation = DockerComposeDeploymentTestingBuilder.ResolveAspireCliInvocation("aspire", appHostPath);

        Assert.Equal("dotnet", invocation.Executable);
        Assert.Equal(["tool", "run", "aspire", "--"], invocation.PrefixArguments);
        var explicitInvocation = DockerComposeDeploymentTestingBuilder.ResolveAspireCliInvocation(
            "custom-aspire",
            appHostPath);
        Assert.Equal("custom-aspire", explicitInvocation.Executable);
        Assert.Empty(explicitInvocation.PrefixArguments);
    }

    [Fact]
    public void Unrestored_local_cli_detection_only_falls_back_for_the_manifest_invocation()
    {
        var localInvocation = new AspireCliInvocation("dotnet", ["tool", "run", "aspire", "--"]);
        var explicitInvocation = new AspireCliInvocation("custom-aspire", []);

        Assert.True(DockerComposeDeploymentTestingBuilder.ShouldFallBackToAspireOnPath(
            localInvocation,
            "Run \"dotnet tool restore\" to make the \"aspire\" command available."));
        Assert.False(DockerComposeDeploymentTestingBuilder.ShouldFallBackToAspireOnPath(
            localInvocation,
            "Deployment failed for another reason."));
        Assert.False(DockerComposeDeploymentTestingBuilder.ShouldFallBackToAspireOnPath(
            explicitInvocation,
            "Run \"dotnet tool restore\" to make the \"aspire\" command available."));
    }

    [Fact]
    public void Deployment_options_use_unique_environment_names_by_default()
    {
        var first = new DockerComposeDeploymentOptions().EnvironmentName;
        var second = new DockerComposeDeploymentOptions().EnvironmentName;

        Assert.StartsWith(
            DockerComposeDeploymentTestingBuilder.DefaultDeploymentEnvironmentName + "-",
            first,
            StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ResolveAppHostPath_supports_project_file_and_directory_metadata()
    {
        var projectFile = Path.Combine(Path.GetTempPath(), "Sample.AppHost.csproj");
        var projectAssembly = CreateAssembly(("AppHostProjectPath", projectFile));
        Assert.Equal(projectFile, DockerComposeDeploymentTestingBuilder.ResolveAppHostPath(projectAssembly));

        var projectDirectory = Path.Combine(Path.GetTempPath(), "sample-apphost");
        var directoryAssembly = CreateAssembly(
            ("AppHostProjectPath", projectDirectory),
            ("AppHostProjectName", "Sample.AppHost"));
        Assert.Equal(
            Path.Combine(projectDirectory, "Sample.AppHost.csproj"),
            DockerComposeDeploymentTestingBuilder.ResolveAppHostPath(directoryAssembly));

        var explicitNameAssembly = CreateAssembly(
            ("AppHostProjectPath", projectDirectory),
            ("AppHostProjectName", "Explicit.AppHost.csproj"));
        Assert.Equal(
            Path.Combine(projectDirectory, "Explicit.AppHost.csproj"),
            DockerComposeDeploymentTestingBuilder.ResolveAppHostPath(explicitNameAssembly));
    }

    [Fact]
    public void ResolveAppHostPath_reports_missing_project_metadata()
    {
        var missingPath = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder.ResolveAppHostPath(CreateAssembly()));
        Assert.Contains("does not identify an Aspire AppHost project path", missingPath.Message, StringComparison.Ordinal);

        var missingName = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeDeploymentTestingBuilder.ResolveAppHostPath(CreateAssembly(
                ("AppHostProjectPath", Path.GetTempPath()))));
        Assert.Contains("does not identify an Aspire AppHost project name", missingName.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_owns_a_temporary_deployment_until_idempotent_disposal()
    {
        var runner = new RecordingAspireCommandRunner(WriteConfigurationOnDeploy);
        var builder = await DeployAsync(
            runner,
            cancellationToken: TestContext.Current.CancellationToken);
        var deploy = Assert.Single(runner.Invocations);

        Assert.Equal("deploy", deploy.Command);
        Assert.Equal("CI", deploy.EnvironmentName);
        Assert.Equal("aspire-under-test", deploy.AspireCliPath);
        Assert.Equal(GetAppHostPath(), deploy.AppHostPath);
        Assert.True(Directory.Exists(deploy.OutputPath));

        await builder.DisposeAsync();
        await builder.DisposeAsync();

        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.False(Directory.Exists(deploy.OutputPath));
    }

    [Fact]
    public async Task Concurrent_disposal_callers_share_cleanup_and_completion()
    {
        var destroyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDestroy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingAspireCommandRunner(async invocation =>
        {
            await WriteConfigurationOnDeploy(invocation);
            if (invocation.Command == "destroy")
            {
                destroyStarted.SetResult();
                await releaseDestroy.Task;
            }
        });
        var builder = await DeployAsync(
            runner,
            cancellationToken: TestContext.Current.CancellationToken);

        var first = builder.DisposeAsync().AsTask();
        await destroyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var second = builder.DisposeAsync().AsTask();

        Assert.Same(first, second);
        Assert.False(second.IsCompleted);
        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));

        releaseDestroy.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Synchronous_dispose_requires_the_async_disposal_contract()
    {
        var runner = new RecordingAspireCommandRunner(WriteConfigurationOnDeploy);
        var builder = await DeployAsync(
            runner,
            cancellationToken: TestContext.Current.CancellationToken);
        var exception = Assert.Throws<InvalidOperationException>(() => ((IDisposable)builder).Dispose());

        Assert.Contains("DisposeAsync", exception.Message, StringComparison.Ordinal);
        await builder.DisposeAsync();
    }

    [Fact]
    public async Task DeployAsync_retains_an_explicit_output_directory_after_destroy()
    {
        using var output = TemporaryDirectory.Create();
        var runner = new RecordingAspireCommandRunner(WriteConfigurationOnDeploy);
        var builder = await DeployAsync(
            runner,
            output.Path,
            TestContext.Current.CancellationToken);

        await builder.DisposeAsync();

        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.True(Directory.Exists(output.Path));
        Assert.True(File.Exists(Path.Combine(output.Path, ".env.CI")));
    }

    [Fact]
    public async Task DeployAsync_preserves_a_deploy_failure_while_destroying_and_deleting_temporary_output()
    {
        var failure = new InvalidOperationException("deploy failed");
        var runner = new RecordingAspireCommandRunner(invocation =>
            invocation.Command == "deploy" ? Task.FromException(failure) : Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeployAsync(runner, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.False(Directory.Exists(runner.Invocations[0].OutputPath));
    }

    [Fact]
    public async Task DeployAsync_retries_after_cleaning_up_a_host_port_conflict()
    {
        var deployAttempts = 0;
        var runner = new RecordingAspireCommandRunner(invocation =>
        {
            if (invocation.Command == "deploy" && deployAttempts++ == 0)
            {
                return Task.FromException(new InvalidOperationException(
                    "Bind for 0.0.0.0:5101 failed: port is already allocated"));
            }

            return WriteConfigurationOnDeploy(invocation);
        });

        var builder = await DeployAsync(runner, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["deploy", "destroy", "deploy"], runner.Invocations.Select(invocation => invocation.Command));
        await builder.DisposeAsync();
        Assert.Equal(
            ["deploy", "destroy", "deploy", "destroy"],
            runner.Invocations.Select(invocation => invocation.Command));
    }

    [Fact]
    public async Task DeployAsync_cleans_up_when_deployment_does_not_create_the_environment_file()
    {
        var runner = new RecordingAspireCommandRunner(_ => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            DeployAsync(runner, cancellationToken: TestContext.Current.CancellationToken));

        Assert.EndsWith(".env.CI", exception.FileName, StringComparison.Ordinal);
        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.False(Directory.Exists(runner.Invocations[0].OutputPath));
    }

    [Fact]
    public async Task DeployAsync_forwards_cancellation_and_uses_a_fresh_token_for_cleanup()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var runner = new RecordingAspireCommandRunner(invocation =>
        {
            if (invocation.Command == "deploy")
            {
                invocation.CancellationToken.ThrowIfCancellationRequested();
            }

            Assert.False(invocation.CancellationToken.IsCancellationRequested);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeployAsync(runner, cancellationToken: cancellation.Token));

        Assert.True(runner.Invocations[0].CancellationToken.IsCancellationRequested);
        Assert.Equal("destroy", runner.Invocations[1].Command);
        Assert.False(Directory.Exists(runner.Invocations[0].OutputPath));
    }

    [Fact]
    public async Task DeployAsync_applies_the_deployment_timeout_and_still_cleans_up()
    {
        var runner = new RecordingAspireCommandRunner(invocation =>
            invocation.Command == "deploy"
                ? Task.Delay(Timeout.InfiniteTimeSpan, invocation.CancellationToken)
                : Task.CompletedTask);
        var options = CreateOptions();
        options.DeploymentTimeout = TimeSpan.FromMilliseconds(20);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            DeployAsync(runner, options, TestContext.Current.CancellationToken));

        Assert.Contains("aspire deploy", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["deploy", "destroy"], runner.Invocations.Select(invocation => invocation.Command));
        Assert.False(Directory.Exists(runner.Invocations[0].OutputPath));
    }

    [Fact]
    public async Task DisposeAsync_retains_temporary_output_when_destroy_fails()
    {
        var failure = new InvalidOperationException("destroy failed");
        var runner = new RecordingAspireCommandRunner(async invocation =>
        {
            await WriteConfigurationOnDeploy(invocation);
            if (invocation.Command == "destroy")
            {
                throw failure;
            }
        });
        var builder = await DeployAsync(
            runner,
            cancellationToken: TestContext.Current.CancellationToken);
        var outputPath = runner.Invocations[0].OutputPath;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.DisposeAsync().AsTask());

        Assert.Same(failure, exception);
        Assert.True(Directory.Exists(outputPath));
        Directory.Delete(outputPath, recursive: true);
    }

    [Fact]
    public async Task DeployAsync_reports_deploy_and_destroy_failures_and_retains_recovery_state()
    {
        var deployFailure = new InvalidOperationException("deploy failed");
        var destroyFailure = new InvalidOperationException("destroy failed");
        var runner = new RecordingAspireCommandRunner(invocation =>
            Task.FromException(invocation.Command == "deploy" ? deployFailure : destroyFailure));

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            DeployAsync(runner, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(deployFailure, exception.InnerExceptions);
        Assert.Contains(destroyFailure, exception.InnerExceptions);
        var outputPath = runner.Invocations[0].OutputPath;
        Assert.Contains(outputPath, exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputPath));
        Directory.Delete(outputPath, recursive: true);
    }

    [Fact]
    public async Task BuildAsync_honors_pre_cancellation_without_building_the_application()
    {
        using var file = TemporaryFile.Create(string.Empty);
        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            builder.BuildAsync(cancellation.Token));

        await builder.DisposeAsync();
    }

    [Fact]
    public async Task Builder_can_only_build_the_distributed_application_once()
    {
        using var file = TemporaryFile.Create(string.Empty);
        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);
        await using var application = await builder.BuildAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_does_not_build_an_unbuilt_application_and_is_idempotent()
    {
        using var file = TemporaryFile.Create(string.Empty);
        var builder = DockerComposeDeploymentTestingBuilder
            .Create<DockerComposeDeploymentTestingBuilderTests>(file.Path);

        await builder.DisposeAsync();
        await builder.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => builder.Build());
    }

    private static Task<DockerComposeDeploymentTestingBuilder> DeployAsync(
        IAspireCommandRunner runner,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        var options = CreateOptions();
        options.OutputPath = outputPath;
        return DeployAsync(runner, options, cancellationToken);
    }

    private static Task<DockerComposeDeploymentTestingBuilder> DeployAsync(
        IAspireCommandRunner runner,
        DockerComposeDeploymentOptions options,
        CancellationToken cancellationToken = default) =>
        DockerComposeDeploymentTestingBuilder.DeployAsync<DockerComposeDeploymentTestingBuilderTests>(
            options,
            GetAppHostPath(),
            runner,
            cancellationToken);

    private static Assembly CreateAssembly(params (string Key, string Value)[] metadata)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"TestAppHost{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
        foreach (var (key, value) in metadata)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [key, value]));
        }

        return assembly;
    }

    private static DockerComposeDeploymentOptions CreateOptions() => new()
    {
        EnvironmentName = "CI",
        AspireCliPath = "aspire-under-test"
    };

    private static Task WriteConfigurationOnDeploy(AspireCommandInvocation invocation)
    {
        if (invocation.Command == "deploy")
        {
            Directory.CreateDirectory(invocation.OutputPath);
            File.WriteAllText(
                Path.Combine(invocation.OutputPath, $".env.{invocation.EnvironmentName}"),
                $"ASPIRE_TEST_VALUE__{Encode("Deployment:Ready")}=true");
        }

        return Task.CompletedTask;
    }

    private static string GetAppHostPath() => Path.GetFullPath(
        "Test.AppHost.csproj",
        AppContext.BaseDirectory);

    private static string Encode(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private sealed class RecordingAspireCommandRunner(
        Func<AspireCommandInvocation, Task> run) : IAspireCommandRunner
    {
        public List<AspireCommandInvocation> Invocations { get; } = [];

        public Task RunAsync(
            string aspireCliPath,
            string command,
            string appHostPath,
            string outputPath,
            string environmentName,
            CancellationToken cancellationToken)
        {
            var invocation = new AspireCommandInvocation(
                aspireCliPath,
                command,
                appHostPath,
                outputPath,
                environmentName,
                cancellationToken);
            Invocations.Add(invocation);
            return run(invocation);
        }
    }

    private sealed record AspireCommandInvocation(
        string AspireCliPath,
        string Command,
        string AppHostPath,
        string OutputPath,
        string EnvironmentName,
        CancellationToken CancellationToken);

    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path) => Path = path;

        public string Path { get; }

        public static TemporaryFile Create(string content)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-compose-test-{Guid.NewGuid():N}.env");
            File.WriteAllText(path, content);
            return new TemporaryFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-compose-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        private EnvironmentVariable(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static EnvironmentVariable Set(string name, string? value) => new(name, value);

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }
}
