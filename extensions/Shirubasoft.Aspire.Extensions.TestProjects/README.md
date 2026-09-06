# Shirubasoft.Aspire.Extensions.TestProjects

Run executable Microsoft.Testing.Platform test projects against resources owned by your AppHost. Test suites are native Aspire `ProjectResource` instances registered through `AddProject` with `WithExplicitStart()`. They remain stopped until you choose **Run tests** or **Start**.

**Run tests** starts the project through Aspire and opens a Markdown result dialog when it finishes. **Test results** reopens the latest completed report, including runs started with Aspire's native **Start** command.

| 🟢 Passed | 🔴 Failed / errors | 🟡 Skipped | Total |
| ---: | ---: | ---: | ---: |
| 12 | 1 | 2 | 15 |

The package targets .NET 10 and the repository's pinned Aspire 13.5.3 APIs. Supported test projects are single-target MTP executables with a compatible `Microsoft.Testing.Extensions.TrxReport` package. Classic VSTest projects are outside this package's support boundary.

## Register a test project

Add the package to your AppHost and reference the MTP test project:

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

For a project without an AppHost `ProjectReference`, use the path overload. Relative paths resolve from the AppHost directory:

```csharp
builder.AddTestProject("api-tests", "../Api.Tests/Api.Tests.csproj", new TestProjectOptions
{
    ResultsDirectory = "TestResults",
})
.WithReference(api)
.WaitFor(api);
```

The returned builder is `IResourceBuilder<ProjectResource>`. Aspire owns the project's build configuration, launch profile, environment, dependency waits, process lifetime, console logs, and IDE debugging. Register each test suite as a single instance. Test projects are excluded from published manifests.

## Configure the MTP executable

Enable your test framework's MTP entry point and TRX reporting extension. The runnable xUnit sample uses:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="xunit.v3.mtp-v2" />
  <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" />
</ItemGroup>
```

Package versions are managed centrally in this repository. Supply compatible versions when using the example elsewhere.

Use `WithArgs` for MTP or framework-specific options. For example, the xUnit MTP executable accepts `.WithArgs("--filter-method", "*Greeting*")`. The extension owns the TRX reporting arguments and per-run output directory; do not override those arguments.

## Consume references in tests

Load normal .NET configuration with the environment-variable provider. A database reference supplies `ConnectionStrings:<name>`; a service reference supplies service-discovery settings such as `services:api:http:0`. Use .NET service discovery for HTTP clients, or read an injected endpoint through `IConfiguration` as the sample does.

The AppHost owns the environment. Managed tests consume its configuration and must not instantiate or reference that same AppHost through `DistributedApplicationTestingBuilder`. This keeps the project graph acyclic. Separate fixture tests can use the testing builder to start the AppHost and explicitly invoke test commands.

Tests share the running application and its data. Use independent data or explicit cleanup when suites use the same database.

## Run and debug

Start the AppHost through the Aspire extension in your IDE to enable native project debugging. Set breakpoints in the test project, then explicitly choose **Run tests** or **Start** in the dashboard. Aspire launches the test project through its normal project-debugging path. This workflow was verified in VS Code Insiders with the Aspire extension.

Starting the AppHost alone never executes the tests. The extension imposes no wall-clock timeout, so a debugger breakpoint can remain paused. Use MTP's runner-specific timeout options if your suite needs a time limit.

The **Run tests** progress dialog supports cancellation through Aspire's native Stop command. The native **Stop** command also terminates a running test project. A stopped run is unsuccessful and may have incomplete artifacts. The application under test stays available for investigation and subsequent test runs.

Commands also work through the Aspire CLI:

```bash
aspire resource api-tests run-tests --apphost samples/TestProjects.AppHost/TestProjects.AppHost.csproj --non-interactive
aspire resource api-tests test-results --apphost samples/TestProjects.AppHost/TestProjects.AppHost.csproj --non-interactive
```

The `run-tests` result sets `Data.Format = Markdown` and `Data.DisplayImmediately = true`. Both successful and failed runs open their report. Native **Start** returns Aspire's startup result; use **Test results** to see that run's report. CI should check the `run-tests` command's outcome; starting the AppHost alone does not validate the suite.

## Reports and artifacts

Reports use 🟢 passed, 🔴 failed or error, and 🟡 skipped labels. The summary includes counts and durations, with failures first and assertion details below the test table. Markdown displays up to 200 tests and details for up to 20 failures, with long details truncated. TRX files retain the full runner report.

A nonzero or unknown exit code, failed test, incomplete report, missing or malformed TRX report, or zero reported tests makes the command unsuccessful. Unknown TRX outcomes count as errors. **Test results** preserves the recorded run's success or failure.

Each explicit start creates a unique directory beneath `<ResultsDirectory>/<resource-name>/<run-id>`. The default root is `TestResults` relative to the AppHost directory. It contains `results.trx` from MTP and `results.md` from the extension. Runner output is available through the test resource's native Aspire console logs. Callers manage artifact retention.

## Run the sample

From this extension folder:

```bash
aspire run --project samples/TestProjects.AppHost/TestProjects.AppHost.csproj
```

The sample starts an HTTP API and leaves `api-tests` waiting for an explicit start. Its tests consume the API's injected endpoint and demonstrate skipped tests. Choose **Run tests** to open the report.

Set the AppHost configuration value `TestDemo:Fail` to `true` for a deliberate assertion failure, or `TestDemo:Slow` to `true` for a test that waits on the API for 30 seconds and can be stopped. Use standard .NET configuration providers, such as an `appsettings.Development.json` file in the sample AppHost.

The repository's build tooling uses VSTest for its own library and package tests. The MTP sample is built through its AppHost project reference and executed explicitly by the integration fixture. Running the sample executable directly without injected endpoints skips the API checks.
