# Shirubasoft.Aspire.Extensions.TestDiagnostics

Save an Aspire test application's console logs, structured logs, and distributed traces before teardown. The core package works with any test runner. It uses Aspire's console-log service and the dashboard HTTP telemetry API directly, so parallel tests export from their own application instance.

## Install

Add the package to your distributed test project and reference your AppHost project:

```bash
dotnet add package Shirubasoft.Aspire.Extensions.TestDiagnostics
```

The package targets .NET 10 and the repository's pinned Aspire 13.5.3 APIs. The AppHost SDK supplies the dashboard and orchestrator needed to run distributed tests.

## Export failed tests

Create the builder with `DiagnosticsTestingBuilder.CreateAsync<TEntryPoint>`. It returns Aspire's `IDistributedApplicationTestingBuilder` with the dashboard enabled during construction. You can configure resources and services on this builder as usual.

Wrap startup and assertions with `RunWithDiagnosticsAsync`:

```csharp
using Aspire.Hosting.Testing;

await using var builder = await DiagnosticsTestingBuilder
    .CreateAsync<Projects.MyApp_AppHost>(cancellationToken: cancellationToken);
await using var app = await builder.BuildAsync(cancellationToken);

await app.RunWithDiagnosticsAsync(async token =>
{
    await app.StartAsync(token);
    await app.ResourceNotifications.WaitForResourceHealthyAsync("api", token);
    using var client = app.CreateHttpClient("api");
    using var response = await client.GetAsync("/health", token);
    response.EnsureSuccessStatusCode();
}, new TestDiagnosticsOptions
{
    OutputDirectory = "TestResults/aspire-diagnostics"
}, cancellationToken);
```

On failure, the wrapper exports before rethrowing the original exception. `exception.Data["Aspire.TestDiagnostics"]` contains the `TestDiagnosticsExport`, including its absolute directory path and collection errors. If the export itself throws, `exception.Data["Aspire.TestDiagnostics.ExportError"]` contains that exception. A canceled test still gets a separate, bounded export attempt. `ExportTimeout` defaults to 30 seconds.

The caller owns the builder and application. Keep their disposal outside the wrapper so diagnostics remain available while it runs. Builder creation and `BuildAsync` failures happen before an application exists and cannot be captured by the application wrapper.

## Keep the dashboard available

The factory enables Aspire's dashboard before Aspire registers its services. It preserves the testing builder's randomized ports and authentication settings. After startup, use Aspire's endpoint API to obtain its address:

```csharp
var dashboardAddress = app.GetEndpoint("aspire-dashboard", "http");
```

Use `"https"` when your AppHost configures an HTTPS frontend. Aspire's startup output includes the browser login link. The dashboard lives until the caller disposes the test application.

For an existing `DistributedApplicationTestingBuilder` workflow, enable the dashboard in its creation callback:

```csharp
await using var builder = await DistributedApplicationTestingBuilder
    .CreateAsync<Projects.MyApp_AppHost>([],
        (options, settings) => options.DisableDashboard = false,
        cancellationToken);
```

The export and operation extension methods also work with that builder's application.

## Let CI choose what to publish

Set `ExportMode = TestDiagnosticsExportMode.Always` to export after successful operations too:

```csharp
var export = await app.RunWithDiagnosticsAsync(RunAssertionsAsync,
    new TestDiagnosticsOptions
    {
        OutputDirectory = "TestResults/aspire-diagnostics",
        ExportMode = TestDiagnosticsExportMode.Always
    }, cancellationToken);
```

A successful operation returns its export. Individual collection failures appear in `export.Errors`; failures creating the directory or writing the manifest throw. CI can upload the directory only when the test job fails:

```yaml
- name: Upload Aspire diagnostics
  if: failure()
  uses: actions/upload-artifact@v7
  with:
    name: aspire-test-diagnostics
    path: '**/TestResults/aspire-diagnostics/**'
    if-no-files-found: warn
```

## Use a runner's teardown hook

Call `ExportDiagnosticsAsync` from a runner's asynchronous teardown or fixture hook, after reading that runner's test outcome and before disposing the application:

```csharp
if (testFailed || alwaysExport)
{
    using var exportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var export = await app.ExportDiagnosticsAsync(
        "TestResults/aspire-diagnostics", exportTimeout.Token);
    // Attach export.DirectoryPath through your runner's artifact API.
}
```

The explicit export method propagates cancellation. Its caller decides how to report an export exception without replacing a test failure. This also supports one AppHost shared by several tests. Exports contain the application's retained history, so a shared fixture's output includes earlier tests.

## Read an export

Every call creates a unique directory beneath `OutputDirectory`:

| File | Contents |
| --- | --- |
| `consolelogs/<resource>.txt` | Resource console output, including hidden resources and combined replica output. |
| `logs.json` | Dashboard response containing OTLP structured logs and record counts. |
| `traces.json` | Dashboard response containing OTLP traces and record counts. |
| `manifest.json` | Absolute export directory and errors from individual sources. |

Collection continues after an individual source fails. For example, console output remains available when the dashboard is missing. A timeout or explicit cancellation can leave partial files. Credentials used to query the dashboard are kept out of the manifest; application log and trace contents are exported as received.

Exports are snapshots of data already received and retained by Aspire. Flush or wait for your application's telemetry before exporting if the last operation must be included. Dashboard retention and application sampling still apply. The JSON files preserve the dashboard API envelope, including `totalCount` and `returnedCount`; they are diagnostic artifacts rather than dashboard-import ZIP archives.

The implementation uses the same [dashboard HTTP telemetry API](https://aspire.dev/fundamentals/telemetry/) used by CLI telemetry commands. The [Aspire export command](https://aspire.dev/reference/cli/commands/aspire-export/) describes the related CLI archive format.

## Run the sample

From the repository root:

```bash
dotnet test extensions/Shirubasoft.Aspire.Extensions.TestDiagnostics/tests/Shirubasoft.Aspire.Extensions.TestDiagnostics.Tests --filter FullyQualifiedName~DistributedDiagnosticsTests
```

The distributed tests start the sample API, emit logs and traces, and prove that concurrent AppHosts export only their own telemetry. They exercise manual exports, successful always-export operations, and a simulated test failure that preserves its original exception.
