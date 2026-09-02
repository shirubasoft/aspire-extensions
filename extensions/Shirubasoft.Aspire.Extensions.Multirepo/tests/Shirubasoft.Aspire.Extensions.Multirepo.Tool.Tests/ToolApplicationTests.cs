using ActionsToolkit.Core.Services;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shirubasoft.Aspire.Extensions.Multirepo.Tool;
using System.ComponentModel;
using System.Text.Json;
using Xunit;

namespace Shirubasoft.Aspire.Extensions.Multirepo.Tool.Tests;

public sealed class ToolApplicationTests
{
    [Fact]
    public async Task Dispatch_passes_manifest_as_json_waits_and_emits_the_external_result()
    {
        using var directory = TestDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "images.json");
        await CreateManifest().SaveAsync(manifestPath, TestContext.Current.CancellationToken);
        var runner = new FakeProcessRunner((invocation, _) => Task.FromResult(
            invocation.Arguments.Take(2).ToArray() switch
            {
                ["workflow", "run"] => new ProcessExecutionResult(
                    0,
                    "https://github.com/acme/repo-a/actions/runs/12345\n",
                    string.Empty),
                ["run", "watch"] => new ProcessExecutionResult(0, string.Empty, string.Empty),
                _ => throw new InvalidOperationException("Unexpected process invocation.")
            }));
        var githubActions = Substitute.For<ICoreService>();

        var (exitCode, output, error, _) = await RunAsync(
            directory,
            [
                "workflow", "dispatch",
                "--repository", "acme/repo-a",
                "--workflow", "external-e2e.yml",
                "--ref", "main",
                "--workflow-document", manifestPath,
                "--input", "repo-a-ref=candidate"
            ],
            runner,
            new Dictionary<string, string?>
            {
                ["GITHUB_OUTPUT"] = Path.Combine(directory.Path, "github-output"),
                [$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] = "configured-gh"
            },
            githubActions);

        Assert.Equal(ToolExitCode.Success, exitCode);
        Assert.Empty(error);
        Assert.Contains("12345", output, StringComparison.Ordinal);
        Assert.Collection(
            runner.Invocations,
            dispatch =>
            {
                Assert.Equal("configured-gh", dispatch.FileName);
                Assert.Equal(ProcessOutputMode.Capture, dispatch.OutputMode);
                Assert.Equal(
                    [
                        "workflow", "run", "external-e2e.yml",
                        "--repo", "acme/repo-a",
                        "--json", "--ref", "main"
                    ],
                    dispatch.Arguments);
                using var payload = JsonDocument.Parse(Assert.IsType<string>(dispatch.StandardInput));
                Assert.Equal("candidate", payload.RootElement.GetProperty("repo-a-ref").GetString());
                var manifest = ModuleImageWorkflowDocument.Parse(
                    payload.RootElement.GetProperty("image-workflow").GetString()!);
                Assert.Equal(3, manifest.Images.Count);
            },
            watch =>
            {
                Assert.Equal(
                    ["run", "watch", "12345", "--repo", "acme/repo-a", "--compact", "--exit-status"],
                    watch.Arguments);
                Assert.Equal(ProcessOutputMode.Stream, watch.OutputMode);
            });
        var outputs = githubActions.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ICoreService.SetOutputAsync))
            .ToDictionary(
                call => Assert.IsType<string>(call.GetArguments()[0]),
                call => Assert.IsType<string>(call.GetArguments()[1]),
                StringComparer.Ordinal);
        Assert.Equal("12345", outputs["run-id"]);
        Assert.Equal("https://github.com/acme/repo-a/actions/runs/12345", outputs["run-url"]);
    }

    [Fact]
    public async Task Dispatch_returns_the_external_workflow_failure_status()
    {
        using var directory = TestDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "images.json");
        await CreateManifest().SaveAsync(manifestPath, TestContext.Current.CancellationToken);
        var runner = new FakeProcessRunner((invocation, _) => Task.FromResult(
            invocation.Arguments.Take(2).ToArray() switch
            {
                ["workflow", "run"] => new ProcessExecutionResult(
                    0,
                    "https://github.com/acme/repo-a/actions/runs/88",
                    string.Empty),
                ["run", "watch"] => new ProcessExecutionResult(1, string.Empty, string.Empty),
                _ => throw new InvalidOperationException("Unexpected process invocation.")
            }));

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            [
                "workflow", "dispatch",
                "--repository", "acme/repo-a",
                "--workflow", "external-e2e.yml",
                "--workflow-document", manifestPath
            ],
            runner);

        Assert.Equal(1, exitCode);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(1, "", "dispatch rejected")]
    [InlineData(0, "workflow created", "")]
    public async Task Dispatch_reports_operational_failures(
        int dispatchExitCode,
        string standardOutput,
        string standardError)
    {
        using var directory = TestDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "images.json");
        await CreateManifest().SaveAsync(manifestPath, TestContext.Current.CancellationToken);
        var runner = new FakeProcessRunner((_, _) => Task.FromResult(
            new ProcessExecutionResult(dispatchExitCode, standardOutput, standardError)));

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            [
                "workflow", "dispatch",
                "--repository", "acme/repo-a",
                "--workflow", "external-e2e.yml",
                "--workflow-document", manifestPath
            ],
            runner);

        Assert.Equal(ToolExitCode.Failure, exitCode);
        Assert.NotEmpty(error);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task Dispatch_validates_the_complete_GitHub_input_payload()
    {
        using var directory = TestDirectory.Create();
        var manifestPath = Path.Combine(directory.Path, "images.json");
        await CreateManifest().SaveAsync(manifestPath, TestContext.Current.CancellationToken);

        var (exitCode, _, error, runner) = await RunAsync(
            directory,
            [
                "workflow", "dispatch",
                "--repository", "acme/repo-a",
                "--workflow", "external-e2e.yml",
                "--workflow-document", manifestPath,
                "--input", $"extra={new string('x', ModuleImageWorkflowDocument.MaximumJsonLength)}"
            ]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("complete workflow input payload", error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Apply_runs_the_child_with_full_environment_and_tag_precedence()
    {
        using var directory = TestDirectory.Create();
        var document = CreateManifest();
        document.Images[0].Tag = null;
        document.Images[0].Digest = $"sha256:{new string('a', 64)}";
        var githubActions = Substitute.For<ICoreService>();
        var (exitCode, _, error, runner) = await RunAsync(
            directory,
            [
                "images", "apply",
                "--json", document.ToJson(),
                "--tag", "global",
                "--resource-tags", "{\"orders/api\":\"specific\"}",
                "--",
                "dotnet", "test", "tests/Consumer.Tests.csproj", "--configuration", "Release"
            ],
            githubActions: githubActions);

        Assert.Equal(ToolExitCode.Success, exitCode);
        Assert.Empty(error);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("dotnet", invocation.FileName);
        Assert.Equal(
            ["test", "tests/Consumer.Tests.csproj", "--configuration", "Release"],
            invocation.Arguments);
        Assert.Equal(directory.Path, invocation.WorkingDirectory);
        Assert.Equal(ProcessOutputMode.Stream, invocation.OutputMode);
        var values = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            invocation.EnvironmentVariables);
        const string catalog =
            "Aspire__ModularAppHosts__Modules__catalog__Projects__api";
        const string orders =
            "Aspire__ModularAppHosts__Modules__orders__Projects__api";
        Assert.Equal("registry.example.test", values[$"{catalog}__ImageRegistry"]);
        Assert.Equal("acme/catalog-api", values[$"{catalog}__ImageName"]);
        Assert.Equal("global", values[$"{catalog}__ImageTag"]);
        Assert.Equal(string.Empty, values[$"{catalog}__ImageSHA256"]);
        Assert.Equal("specific", values[$"{orders}__ImageTag"]);
        Assert.Equal(bool.FalseString, values[$"{orders}__PublishImage"]);
        Assert.Equal(nameof(ImagePullPolicy.Always), values[$"{orders}__ImagePullPolicy"]);
        Assert.Equal(nameof(ModuleProjectMode.Container), values[$"{orders}__ProjectMode"]);
        Assert.DoesNotContain(
            githubActions.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(ICoreService.ExportVariableAsync));
    }

    [Theory]
    [InlineData()]
    [InlineData("--file", "manifest.json", "--json", "{}")]
    public async Task Apply_requires_exactly_one_manifest_source(params string[] sourceArguments)
    {
        using var directory = TestDirectory.Create();
        var args = new List<string> { "images", "apply" };
        args.AddRange(sourceArguments);
        args.AddRange(["--", "test-command"]);

        var (exitCode, _, error, _) = await RunAsync(directory, [.. args]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("exactly one", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_requires_a_child_command()
    {
        using var directory = TestDirectory.Create();

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["images", "apply", "--json", CreateManifest().ToJson()]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("command", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_requires_the_command_separator()
    {
        using var directory = TestDirectory.Create();

        var (exitCode, _, error, runner) = await RunAsync(
            directory,
            ["images", "apply", "--json", CreateManifest().ToJson(), "test-command"]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("after '--'", error, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"orders\":\"candidate\"}")]
    [InlineData("{\"orders/api\":\"invalid/tag\"}")]
    public async Task Apply_rejects_invalid_resource_tag_maps(string resourceTags)
    {
        using var directory = TestDirectory.Create();

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            [
                "images", "apply",
                "--json", CreateManifest().ToJson(),
                "--resource-tags", resourceTags,
                "--", "test-command"
            ]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task Missing_manifest_file_is_an_operational_failure()
    {
        using var directory = TestDirectory.Create();

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["images", "apply", "--file", "missing.json", "--", "test-command"]);

        Assert.Equal(ToolExitCode.Failure, exitCode);
        Assert.Contains("missing.json", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_returns_the_child_exit_code()
    {
        using var directory = TestDirectory.Create();
        var runner = new FakeProcessRunner((_, _) => Task.FromResult(
            new ProcessExecutionResult(17, string.Empty, string.Empty)));

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            [
                "images", "apply",
                "--json", CreateManifest().ToJson(),
                "--", "test-command", "--failing-option"
            ],
            runner);

        Assert.Equal(17, exitCode);
        Assert.Empty(error);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(["--failing-option"], invocation.Arguments);
    }

    [Fact]
    public async Task Apply_maps_child_launch_failures_and_cancellation()
    {
        using var directory = TestDirectory.Create();
        var failedRunner = new FakeProcessRunner((_, _) =>
            Task.FromException<ProcessExecutionResult>(new Win32Exception("missing executable")));
        var (failedExit, _, failedError, _) = await RunAsync(
            directory,
            [
                "images", "apply",
                "--json", CreateManifest().ToJson(),
                "--", "missing-command"
            ],
            failedRunner);

        Assert.Equal(ToolExitCode.Failure, failedExit);
        Assert.Contains("missing executable", failedError, StringComparison.Ordinal);

        var cancelledRunner = new FakeProcessRunner((_, cancellationToken) =>
            Task.FromCanceled<ProcessExecutionResult>(
                cancellationToken.IsCancellationRequested
                    ? cancellationToken
                    : new CancellationToken(canceled: true)));
        var (cancelledExit, _, _, _) = await RunAsync(
            directory,
            [
                "images", "apply",
                "--json", CreateManifest().ToJson(),
                "--", "test-command"
            ],
            cancelledRunner);

        Assert.Equal(ToolExitCode.Interrupted, cancelledExit);
    }

    [Fact]
    public async Task Publish_requires_explicit_selection()
    {
        using var directory = TestDirectory.Create();

        var (exitCode, _, error, runner) = await RunAsync(
            directory,
            ["images", "publish", "--apphost", directory.Path]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("--module/--resource", error, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Publish_discovers_selects_pushes_and_emits_resolved_manifest_and_github_outputs()
    {
        using var directory = TestDirectory.Create();
        var destination = Path.Combine(directory.Path, "artifacts", "images.json");
        var runner = new FakeProcessRunner(async (invocation, cancellationToken) =>
        {
            var outputPath = GetOption(invocation.Arguments, "--output-path");
            var document = new ModuleImageWorkflowDocument();
            document.Images.Add(CreateManifestImage(
                "orders",
                "worker",
                ModuleResourceKind.Container,
                "global-dirty"));
            document.Images.Add(CreateManifestImage(
                "orders",
                "api",
                ModuleResourceKind.Project,
                "api-tag-dirty"));
            await document.SaveAsync(
                Path.Combine(outputPath!, ModuleImageWorkflowDocument.DefaultFileName),
                cancellationToken);
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });
        var githubActions = Substitute.For<ICoreService>();

        var (exitCode, output, error, _) = await RunAsync(
            directory,
            [
                "images", "publish",
                "--apphost", directory.Path,
                "--module", "orders",
                "--resource", "worker",
                "--tag", "global",
                "--resource-tags", "{\"orders/api\":\"api-tag\"}",
                "--output", destination,
                "--aspire-path", "custom-aspire"
            ],
            runner,
            githubActions: githubActions);

        Assert.Equal(ToolExitCode.Success, exitCode);
        Assert.Empty(error);
        Assert.Contains(destination, output, StringComparison.Ordinal);
        var invocation = Assert.Single(runner.Invocations);
        AssertProducerInvocation(invocation, "workflow-images");
        Assert.Equal("custom-aspire", invocation.FileName);
        Assert.Equal("orders", invocation.EnvironmentVariables![
            "Aspire__ModularAppHosts__Workflow__Modules__0"]);
        Assert.Equal("worker", invocation.EnvironmentVariables[
            "Aspire__ModularAppHosts__Workflow__Resources__0"]);
        Assert.Equal("global", invocation.EnvironmentVariables[
            "Aspire__ModularAppHosts__Workflow__Tag"]);
        Assert.Equal("{\"orders/api\":\"api-tag\"}", invocation.EnvironmentVariables[
            "Aspire__ModularAppHosts__Workflow__ResourceTags"]);

        var written = await ModuleImageWorkflowDocument.LoadAsync(
            destination,
            TestContext.Current.CancellationToken);
        Assert.Equal("global-dirty", written.Images.Single(image => image.Resource == "worker").Tag);
        Assert.Equal("api-tag-dirty", written.Images.Single(image => image.Resource == "api").Tag);
        var outputs = githubActions.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ICoreService.SetOutputAsync))
            .ToDictionary(
                call => Assert.IsType<string>(call.GetArguments()[0]),
                call => Assert.IsType<string>(call.GetArguments()[1]),
                StringComparer.Ordinal);
        Assert.Equal(destination, outputs["workflow-document-path"]);
        var outputManifest = ModuleImageWorkflowDocument.Parse(outputs["workflow-document"]);
        Assert.Equal("api-tag-dirty", outputManifest.Images.Single(image => image.Resource == "api").Tag);
    }

    [Fact]
    public async Task Publish_uses_the_repository_local_aspire_tool_manifest()
    {
        using var directory = TestDirectory.Create();
        var manifestDirectory = Path.Combine(directory.Path, ".config");
        var appHostDirectory = Path.Combine(directory.Path, "src", "AppHost");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(appHostDirectory);
        await File.WriteAllTextAsync(
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
            """,
            TestContext.Current.CancellationToken);
        var runner = new FakeProcessRunner(async (invocation, cancellationToken) =>
        {
            var outputPath = GetOption(invocation.Arguments, "--output-path");
            await CreateManifest().SaveAsync(
                Path.Combine(outputPath!, ModuleImageWorkflowDocument.DefaultFileName),
                cancellationToken);
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["images", "publish", "--apphost", appHostDirectory, "--all"],
            runner);

        Assert.Equal(ToolExitCode.Success, exitCode);
        Assert.Empty(error);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("dotnet", invocation.FileName);
        Assert.Equal(
            ["tool", "run", "aspire", "--", "do", "workflow-images"],
            invocation.Arguments.Take(6));
        Assert.Equal(appHostDirectory, invocation.WorkingDirectory);
    }

    [Fact]
    public async Task Publish_stops_on_aspire_failure_and_maps_interruption()
    {
        using var directory = TestDirectory.Create();
        var failedRunner = new FakeProcessRunner((_, _) =>
            Task.FromResult(new ProcessExecutionResult(17, string.Empty, "failed")));
        var (failedExit, _, failedError, _) = await RunAsync(
            directory,
            ["images", "publish", "--apphost", directory.Path, "--all"],
            failedRunner);

        Assert.Equal(ToolExitCode.Failure, failedExit);
        Assert.Contains("17", failedError, StringComparison.Ordinal);
        Assert.Single(failedRunner.Invocations);

        var cancelledRunner = new FakeProcessRunner((_, cancellationToken) =>
            Task.FromCanceled<ProcessExecutionResult>(
                cancellationToken.IsCancellationRequested
                    ? cancellationToken
                    : new CancellationToken(canceled: true)));
        var (cancelledExit, _, _, _) = await RunAsync(
            directory,
            ["images", "publish", "--apphost", directory.Path, "--all"],
            cancelledRunner);
        Assert.Equal(ToolExitCode.Interrupted, cancelledExit);
    }

    [Fact]
    public async Task Publish_accepts_repeatable_module_and_resource_options()
    {
        using var directory = TestDirectory.Create();
        var runner = new FakeProcessRunner(async (invocation, cancellationToken) =>
        {
            var outputPath = GetOption(invocation.Arguments, "--output-path");
            var document = new ModuleImageWorkflowDocument();
            document.Images.Add(CreateManifestImage(
                "orders",
                "api",
                ModuleResourceKind.Project));
            await document.SaveAsync(
                Path.Combine(outputPath!, ModuleImageWorkflowDocument.DefaultFileName),
                cancellationToken);
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        });

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["images", "publish", "--apphost", directory.Path, "--module", "orders", "--resource", "api"],
            runner);

        Assert.Equal(ToolExitCode.Success, exitCode);
        Assert.Empty(error);
        var invocation = Assert.Single(runner.Invocations);
        Assert.DoesNotContain("--", invocation.Arguments);
        Assert.Equal("orders", invocation.EnvironmentVariables![
            "Aspire__ModularAppHosts__Workflow__Modules__0"]);
        Assert.Equal("api", invocation.EnvironmentVariables[
            "Aspire__ModularAppHosts__Workflow__Resources__0"]);
    }

    [Fact]
    public async Task Publish_delegates_unknown_resource_validation_to_the_single_apphost_invocation()
    {
        using var directory = TestDirectory.Create();
        var runner = new FakeProcessRunner((_, _) =>
            Task.FromResult(new ProcessExecutionResult(1, string.Empty, "ambiguous selector")));

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["images", "publish", "--apphost", directory.Path, "--resource", "api"],
            runner);

        Assert.Equal(ToolExitCode.Failure, exitCode);
        Assert.Contains("workflow image publish", error, StringComparison.OrdinalIgnoreCase);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(
            "api",
            invocation.EnvironmentVariables!["Aspire__ModularAppHosts__Workflow__Resources__0"]);
    }

    [Fact]
    public async Task Parse_errors_use_the_usage_exit_code()
    {
        using var directory = TestDirectory.Create();

        var (exitCode, _, error, _) = await RunAsync(
            directory,
            ["images", "publish", "--all"]);

        Assert.Equal(ToolExitCode.Usage, exitCode);
        Assert.Contains("--apphost", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliWrap_runner_streams_or_captures_output_cross_platform()
    {
        using var directory = TestDirectory.Create();
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CliWrapProcessRunner(Stream.Null, output, error);

        var streamed = await runner.RunAsync(
            new ProcessInvocation("dotnet", ["--version"], directory.Path),
            TestContext.Current.CancellationToken);

        Assert.True(streamed.IsSuccess, streamed.StandardError);
        Assert.Empty(streamed.StandardOutput);
        Assert.False(string.IsNullOrWhiteSpace(output.ToString()));
        Assert.Empty(error.ToString());

        var captured = await runner.RunAsync(
            new ProcessInvocation(
                "dotnet",
                ["--version"],
                directory.Path,
                OutputMode: ProcessOutputMode.Capture),
            TestContext.Current.CancellationToken);

        Assert.True(captured.IsSuccess, captured.StandardError);
        Assert.False(string.IsNullOrWhiteSpace(captured.StandardOutput));
        Assert.Empty(captured.StandardError);
    }

    private static async Task<(
        int ExitCode,
        string Output,
        string Error,
        FakeProcessRunner Runner)> RunAsync(
        TestDirectory directory,
        string[] args,
        FakeProcessRunner? runner = null,
        IReadOnlyDictionary<string, string?>? configurationValues = null,
        ICoreService? githubActions = null,
        CancellationToken? cancellationToken = null)
    {
        runner ??= new FakeProcessRunner((_, _) =>
            Task.FromResult(new ProcessExecutionResult(0, string.Empty, string.Empty)));
        configurationValues ??= new Dictionary<string, string?>
        {
            ["GITHUB_OUTPUT"] = Path.Combine(directory.Path, "github-output")
        };
        githubActions ??= Substitute.For<ICoreService>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await ToolApplication.RunAsync(
            args,
            runner,
            configuration,
            githubActions,
            directory.Path,
            output,
            error,
            cancellationToken ?? TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString(), runner);
    }

    private static ModuleImageWorkflowDocument CreateManifest()
    {
        var document = new ModuleImageWorkflowDocument();
        document.Images.Add(CreateManifestImage("orders", "worker", ModuleResourceKind.Container));
        document.Images.Add(CreateManifestImage("orders", "api", ModuleResourceKind.Project));
        document.Images.Add(CreateManifestImage("catalog", "api", ModuleResourceKind.Project));
        return document;
    }

    private static ModuleImageWorkflowEntry CreateManifestImage(
        string module,
        string resource,
        ModuleResourceKind kind,
        string tag = "original") => new()
        {
            Module = module,
            Resource = resource,
            ResourceKind = kind,
            Registry = "registry.example.test",
            Repository = $"acme/{module}-{resource}",
            Tag = tag
        };

    private static ModuleImageDescriptionDocument CreateDescriptions()
    {
        var document = new ModuleImageDescriptionDocument();
        document.Modules.Add(new ModuleImageModuleDescription { Name = "orders" });
        document.Modules.Add(new ModuleImageModuleDescription { Name = "catalog" });
        document.Images.Add(CreateDescription("orders", "worker", "imported-worker", ModuleResourceKind.Container));
        document.Images.Add(CreateDescription("catalog", "api", "catalog-api", ModuleResourceKind.Project));
        document.Images.Add(CreateDescription("orders", "api", "imported-api", ModuleResourceKind.Project));
        return document;
    }

    private static ModuleImageDescription CreateDescription(
        string module,
        string resource,
        string effectiveResource,
        ModuleResourceKind kind)
    {
        var reference = $"registry.example.test/acme/{module}-{resource}:original";
        return new ModuleImageDescription
        {
            Module = module,
            Resource = resource,
            EffectiveResource = effectiveResource,
            ResourceKind = kind,
            Registry = "registry.example.test",
            Repository = $"acme/{module}-{resource}",
            Tag = "original",
            Reference = reference,
            PullReference = reference,
            Push = new ModuleImagePushDescription
            {
                Registry = "registry.example.test",
                Repository = $"acme/{module}-{resource}",
                Tag = "original"
            },
            Build = new ModuleImageBuildDescription
            {
                Command = "docker",
                WorkingDirectory = "/work",
                Step = $"build-{effectiveResource}"
            }
        };
    }

    private static void AssertProducerInvocation(ProcessInvocation invocation, string step)
    {
        Assert.Equal(step, invocation.Arguments[1]);
        Assert.Equal(ProcessOutputMode.Stream, invocation.OutputMode);
        Assert.DoesNotContain("--", invocation.Arguments);
        Assert.NotNull(invocation.EnvironmentVariables);
        Assert.NotNull(GetOption(invocation.Arguments, "--output-path"));
    }

    private static string? GetOption(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private sealed class FakeProcessRunner(
        Func<ProcessInvocation, CancellationToken, Task<ProcessExecutionResult>> handler) : IProcessRunner
    {
        public IList<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return handler(invocation, cancellationToken);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"modular-apphosts-tool-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
