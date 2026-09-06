# Shirubasoft.Aspire.Extensions.TestProjects

Run test projects against resources owned by your AppHost. Each suite appears in the Aspire dashboard with **Run tests** and **Test results** commands. A completed run opens a Markdown result dialog with colored status indicators, counts, test durations, and failure details.

| 🟢 Passed | 🔴 Failed / errors | 🟡 Skipped | Total |
| ---: | ---: | ---: | ---: |
| 12 | 1 | 2 | 15 |

The package targets .NET 10 and the repository's pinned Aspire 13.5.3 APIs. It uses Aspire's experimental process-command API internally for process execution and cancellation.

## Register a test project

Add the package to your AppHost and reference your test project:

```bash
dotnet add package Shirubasoft.Aspire.Extensions.TestProjects
```

```csharp
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var api = builder.AddProject<Projects.Api>("api");

builder.AddTestProject<Projects.Api_Tests>("api-tests")
    .WithReference(api)
    .WaitFor(api);

await builder.Build().RunAsync();
```

For a project without an AppHost `ProjectReference`, use the path overload. Relative paths are resolved from the AppHost directory:

```csharp
builder.AddTestProject("api-tests", "../Api.Tests/Api.Tests.csproj")
    .WithReference(api)
    .WaitFor(api);
```

A test suite is a command-driven resource. It does not run automatically at startup. Each run waits for its declared dependencies and resolves environment variables and arguments again through Aspire. Standard `WithReference`, `WithEnvironment`, `WithArgs`, `WaitFor`, and `WaitForCompletion` annotations apply. Test resources are excluded from published manifests.

The AppHost owns the environment. Its managed test projects must consume configuration rather than reference or instantiate that same AppHost through `DistributedApplicationTestingBuilder`. This keeps the build graph acyclic and prevents recursive AppHost startup. Separate fixture tests can still use the testing builder to start this AppHost, execute its test commands through `ResourceCommandService`, and assert the returned outcome.

## Consume references in tests

Load normal .NET configuration with the environment-variable provider. A database reference supplies `ConnectionStrings:<name>`; a service reference supplies service-discovery settings such as `services:api:http:0`. Configure .NET service discovery for HTTP clients, or read an injected endpoint through configuration. The sample uses `IConfiguration` and an HTTP endpoint supplied by `WithReference(api)`.

Tests share the running application and its data. Use independent data or explicit cleanup when suites run against the same database. A failed or canceled test run leaves the AppHost and its services available.

## Select a runner

`TestProjectOptions.Runner` defaults to `VSTest`. The command builds and runs `dotnet test` with a TRX logger. The dashboard offers an optional VSTest filter expression, for example `FullyQualifiedName~Greeting`. The effective `global.json` must use VSTest mode for this runner.

For an executable using Microsoft.Testing.Platform:

```csharp
builder.AddTestProject<Projects.Api_MtpTests>("mtp-tests", new TestProjectOptions
{
    Runner = TestProjectRunner.MicrosoftTestingPlatform,
    Configuration = "Release",
    ResultsDirectory = "TestResults",
    Timeout = TimeSpan.FromMinutes(5),
})
.WithReference(api)
.WaitFor(api);
```

This runner uses `dotnet run --project` with launch profiles disabled, followed by MTP's `--report-trx` arguments. The test project must enable its MTP executable entry point and reference a compatible `Microsoft.Testing.Extensions.TrxReport` package. The checked-in MTP sample demonstrates xUnit's `UseMicrosoftTestingPlatformRunner` setting. This runner expects a single target framework.

Use `WithArgs` for runner-specific options. For MTP, these are executable arguments after `--`; for VSTest, they are `dotnet test` arguments. For example, xUnit's MTP runner accepts `.WithArgs("--filter-method", "*Greeting*")`. Reporting arguments and output directories are owned by the extension; do not override them. MTP filters are framework-specific, so the generic VSTest filter input is only offered for VSTest resources.

## Run and inspect results

Select **Run tests** in the dashboard. The progress dialog supports cancellation. The extension rejects overlapping runs on the same resource and applies a timeout covering dependency waits, build, and execution. The default is ten minutes. AppHost shutdown also cancels active runs.

The result is an Aspire `ExecuteCommandResult` with `Data.Format = Markdown` and `Data.DisplayImmediately = true`. Both successful and failed runs open their report. **Test results** reopens the latest completed result; before the first run, it explains how to start the suite.

The Markdown uses 🟢 passed, 🔴 failed or error, and 🟡 skipped labels. Failures appear first in the test table and include assertion messages and stack traces. The dialog shows up to 200 tests and details for up to 20 failures, with long details truncated. TRX artifacts retain the complete runner report. Names are escaped as Markdown text, and failure output appears as code.

A nonzero runner exit code, failed test, incomplete report, missing or malformed TRX report, or zero reported tests makes the command unsuccessful. Unknown TRX outcomes are treated as errors. Cancellation is shown in yellow and retained as the latest result. Build or runner failures are shown in red with available diagnostic output.

Commands also work through the Aspire CLI:

```bash
aspire resource api-tests run-tests --apphost samples/TestProjects.AppHost/TestProjects.AppHost.csproj --non-interactive
aspire resource api-tests test-results --apphost samples/TestProjects.AppHost/TestProjects.AppHost.csproj --non-interactive
aspire resource api-tests run-tests --apphost samples/TestProjects.AppHost/TestProjects.AppHost.csproj --non-interactive -- --filter "FullyQualifiedName~Greeting"
```

A failed `run-tests` command reports failure to CLI and API clients. CI should execute and check that command's outcome; starting the AppHost alone does not run or validate the suite. A `test-results` command preserves the recorded run's success or failure.

## Retained artifacts

Each run creates a unique directory beneath `<ResultsDirectory>/<resource-name>/<run-id>`. The default root is `TestResults` relative to the AppHost directory.

| Artifact | Contents |
| --- | --- |
| `*.trx` | Test runner reports, including separate VSTest target-framework reports. |
| `results.md` | The Markdown returned to the dashboard. |
| `runner-output.txt` | The retained tail of combined stdout and stderr, up to 200 lines. |

Canceled or interrupted runs may have partial TRX files and no runner-output file. A bounded save attempt retains the cancellation or timeout Markdown. Reports remain after AppHost teardown; callers manage retention. Artifacts contain runner-provided test output, which may include application data.

## Run the sample

From this extension folder:

```bash
aspire run --project samples/TestProjects.AppHost/TestProjects.AppHost.csproj
```

The sample starts an HTTP API and registers VSTest and MTP suites. Both suites test the API through injected configuration and demonstrate skipped tests. Select either suite's **Run tests** command to open its report.

For the failure and cancellation demonstrations, supply these AppHost configuration values:

```json
{
  "TestDemo": {
    "Fail": true,
    "Slow": true
  }
}
```

Set them in the sample AppHost's `appsettings.Development.json` or through standard .NET configuration providers. `TestDemo:Fail` makes the VSTest suite's `FailsWhenRequested` test fail deliberately. Run with the filter `FullyQualifiedName!~FailsWhenRequested&Category!=Slow` to see a successful rerun with a skipped test. `TestDemo:Slow` enables a test that waits on the API for 30 seconds; run `FullyQualifiedName~CanBeCanceled` and cancel it in the progress dialog. The MTP suite keeps its default passing and skipped scenarios.

Running the VSTest sample with `dotnet test`, or the MTP sample with `dotnet run --project samples/TestProjects.MtpTests`, without injected endpoints skips the API checks. The shared solution uses VSTest; the MTP sample is built through its AppHost project reference and executed by the integration fixture. Their test projects have no reference to the AppHost. The extension's integration test uses a separate fixture project to verify real runner execution, failure, cancellation, artifact retention, and rerunning against the same application.
