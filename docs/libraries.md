# Library Documentation

This document provides comprehensive documentation for the key libraries used in the aspire-extensions project for automatic container building via Aspire eventing.

---

## Table of Contents

1. [Aspire.Hosting 13.1.0](#aspirehosting-1310)
2. [CliWrap 3.10.0](#cliwrap-3100)
3. [TUnit 1.12.43](#tunit-11243)

---

## Aspire.Hosting 13.1.0

**.NET Aspire** is an opinionated, cloud-ready stack for building observable, production-ready, distributed applications.

### Version Compatibility

- **Target Framework**: .NET 10
- **Aspire Version**: 13.1.0 (latest stable as of January 2026)
- Aspire 9.x+ requires .NET 8 or later; .NET 10 is fully supported

### Key APIs for This Project

#### 1. Eventing System - Subscribe to AppHost Events

The eventing API allows subscribing to lifecycle events that fire during AppHost startup.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Subscribe to BeforeStartEvent - fires before any resources start
builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
{
    // Access services from the DI container
    var logger = @event.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("BeforeStartEvent fired - checking shared resources");

    // Access the distributed application model
    var model = @event.Services.GetRequiredService<DistributedApplicationModel>();

    // Iterate resources with specific annotations
    foreach (var resource in model.Resources)
    {
        if (resource.TryGetAnnotationsOfType<SharedResourceAnnotation>(out var annotations))
        {
            // Process each annotated resource
        }
    }
});

builder.Build().Run();
```

**Available Built-in Events:**

| Event | When It Fires |
|-------|---------------|
| `BeforeStartEvent` | Before any resources start (ideal for pre-build checks) |
| `AfterResourcesCreatedEvent` | After all resources are created in the model |
| `ResourceEndpointsAllocatedEvent` | After endpoints are allocated to a resource |

#### 2. Resource Lifecycle Events (Fluent API)

For resource-specific events, use the fluent extension methods:

```csharp
var api = builder.AddProject<Projects.Api>("api")
    .OnInitializeResource(async (resource, evt, ct) =>
    {
        var logger = evt.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Initializing resource {Name}", resource.Name);
    })
    .OnBeforeResourceStarted(async (resource, evt, ct) =>
    {
        // Pre-startup validation or configuration
    })
    .OnResourceEndpointsAllocated(async (resource, evt, ct) =>
    {
        // React to endpoint allocation
    })
    .OnResourceReady(async (resource, evt, ct) =>
    {
        // Resource is fully ready
    });
```

#### 3. Custom Annotations

Define custom annotations to attach metadata to resources:

```csharp
using Aspire.Hosting.ApplicationModel;

public sealed class SharedResourceAnnotation : IResourceAnnotation
{
    public required string GitHubRepository { get; init; }
    public required string ServiceName { get; init; }
    public required string ImageBuildCommand { get; init; }
    public string DefaultBranch { get; init; } = "main";
    public string? ProjectPath { get; init; }
    public string? ImageName { get; init; }
}
```

Create fluent extension methods:

```csharp
public static class SharedResourceExtensions
{
    public static IResourceBuilder<T> WithSharedResourceMetadata<T>(
        this IResourceBuilder<T> builder,
        string gitHubRepository,
        string serviceName,
        string imageBuildCommand)
        where T : ContainerResource
    {
        builder.Resource.Annotations.Add(new SharedResourceAnnotation
        {
            GitHubRepository = gitHubRepository,
            ServiceName = serviceName,
            ImageBuildCommand = imageBuildCommand
        });
        return builder;
    }
}
```

#### 4. Accessing Services from Events

The `@event.Services` property provides access to the DI container:

```csharp
builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
{
    // Get services from the DI container
    var logger = @event.Services.GetRequiredService<ILogger<Program>>();
    var configuration = @event.Services.GetRequiredService<IConfiguration>();
    var customService = @event.Services.GetRequiredService<IMyCustomService>();

    // Use services...
});
```

#### 5. Health Checks

Configure health checks for container resources:

```csharp
var catalogApi = builder.AddContainer("catalog-api", "catalog-api")
    .WithHttpEndpoint(targetPort: 8080)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.WebApp>("webapp")
    .WithReference(catalogApi)
    .WaitFor(catalogApi); // Waits for /health to return HTTP 200
```

### Gotchas and Best Practices

1. **Event Handler Return Type**: Event handlers must return `Task` or `ValueTask`. Always use `ValueTask.CompletedTask` for synchronous handlers.

2. **Cancellation Token**: Always respect the cancellation token passed to event handlers.

3. **Service Lifetime**: Services accessed via `@event.Services` follow their registered lifetime. Singletons persist; scoped/transient are created per access.

4. **Event Order**: `BeforeStartEvent` fires before any resources start - this is the ideal place for image builds.

5. **Annotation Access Pattern**:
   ```csharp
   // Correct way to check for annotations
   if (resource.TryGetAnnotationsOfType<SharedResourceAnnotation>(out var annotations))
   {
       foreach (var annotation in annotations)
       {
           // Process annotation
       }
   }
   ```

6. **Resource Builder Chaining**: Always return the builder from extension methods to enable fluent chaining.

---

## CliWrap 3.10.0

**CliWrap** is a library for running command-line processes with a fluent API, supporting streaming, piping, and various execution models.

### Version Compatibility

- **Version**: 3.10.0
- Fully compatible with .NET 10
- No native dependencies

### Key APIs for This Project

#### 1. Basic Buffered Execution

For simple commands where you need the complete output:

```csharp
using CliWrap;
using CliWrap.Buffered;

var result = await Cli.Wrap("git")
    .WithArguments(["rev-parse", "--short", "HEAD"])
    .WithWorkingDirectory("/path/to/repo")
    .ExecuteBufferedAsync();

var commitSha = result.StandardOutput.Trim();
var exitCode = result.ExitCode;
var stdErr = result.StandardError;
```

#### 2. Working Directory Management

Set the working directory for command execution:

```csharp
var result = await Cli.Wrap("docker")
    .WithArguments(["image", "inspect", "myimage:abc1234"])
    .WithWorkingDirectory("/path/to/repo")
    .ExecuteBufferedAsync();
```

#### 3. Streaming Output with Event Stream (Pull-based)

For long-running commands with real-time output (like builds):

```csharp
using CliWrap;
using CliWrap.EventStream;

var cmd = Cli.Wrap("dotnet")
    .WithArguments(["publish", "Api.csproj", "--os", "linux", "/t:PublishContainer"])
    .WithWorkingDirectory("/path/to/repo");

await foreach (var cmdEvent in cmd.ListenAsync(cancellationToken))
{
    switch (cmdEvent)
    {
        case StartedCommandEvent started:
            logger.LogInformation("Build started (PID: {ProcessId})", started.ProcessId);
            break;
        case StandardOutputCommandEvent stdOut:
            logger.LogInformation("Build: {Output}", stdOut.Text);
            break;
        case StandardErrorCommandEvent stdErr:
            logger.LogWarning("Build stderr: {Error}", stdErr.Text);
            break;
        case ExitedCommandEvent exited:
            logger.LogInformation("Build completed with exit code: {ExitCode}", exited.ExitCode);
            break;
    }
}
```

#### 4. Push-based Observable Stream (Rx.NET)

For reactive scenarios:

```csharp
using System.Reactive;
using CliWrap;
using CliWrap.EventStream;

var cmd = Cli.Wrap("docker").WithArguments(["build", "."]);

await cmd.Observe().ForEachAsync(cmdEvent =>
{
    switch (cmdEvent)
    {
        case StandardOutputCommandEvent stdOut:
            Console.WriteLine($"Out> {stdOut.Text}");
            break;
        case StandardErrorCommandEvent stdErr:
            Console.WriteLine($"Err> {stdErr.Text}");
            break;
    }
});
```

#### 5. Error Handling and Validation

Configure exit code validation and handle exceptions:

```csharp
// Default: throws on non-zero exit code
var cmd = Cli.Wrap("git")
    .WithValidation(CommandResultValidation.ZeroExitCode);

// Disable validation (handle exit codes manually)
var cmd = Cli.Wrap("docker")
    .WithArguments(["image", "inspect", "myimage:tag"])
    .WithValidation(CommandResultValidation.None);

var result = await cmd.ExecuteBufferedAsync();
if (result.ExitCode != 0)
{
    // Image doesn't exist - this is expected
    return false;
}
```

Handle execution exceptions:

```csharp
try
{
    await Cli.Wrap("git").ExecuteAsync();
}
catch (CommandExecutionException ex)
{
    // Preserve original exception details
    throw new BuildException($"Git command failed: {ex.Message}", ex);
}
```

#### 6. Timeout and Cancellation

Implement timeout with cancellation:

```csharp
using var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromMinutes(10)); // Build timeout

try
{
    var result = await Cli.Wrap("dotnet")
        .WithArguments(["publish", "..."])
        .ExecuteBufferedAsync(cts.Token);
}
catch (OperationCanceledException)
{
    logger.LogError("Build timed out after 10 minutes");
    throw;
}
```

Graceful + forceful cancellation pattern:

```csharp
using var forcefulCts = new CancellationTokenSource();
using var gracefulCts = new CancellationTokenSource();

// Forceful kill after 10 seconds (fallback)
forcefulCts.CancelAfter(TimeSpan.FromSeconds(10));

// Graceful interrupt (Ctrl+C) after 7 seconds
gracefulCts.CancelAfter(TimeSpan.FromSeconds(7));

var result = await Cli.Wrap("build-script")
    .ExecuteAsync(forcefulCts.Token, gracefulCts.Token);
```

#### 7. Custom Pipe Targets

Capture output to custom targets:

```csharp
var stdOutBuffer = new StringBuilder();
var stdErrBuffer = new StringBuilder();

var result = await Cli.Wrap("docker")
    .WithArguments(["build", "."])
    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
    .ExecuteAsync();

var fullOutput = stdOutBuffer.ToString();
var errors = stdErrBuffer.ToString();
```

### Gotchas and Best Practices

1. **Always Set Working Directory**: For git/docker commands, always explicitly set `WithWorkingDirectory()`.

2. **Use Buffered for Simple Commands**: For commands that complete quickly and you need the full output, use `ExecuteBufferedAsync()`.

3. **Use Event Stream for Builds**: For long-running builds, use `ListenAsync()` to stream output in real-time.

4. **Validation for Expected Failures**: When checking if an image exists, use `WithValidation(CommandResultValidation.None)` since non-zero exit codes are expected.

5. **Arguments as Array**: Pass arguments as a string array to avoid shell escaping issues:
   ```csharp
   // Good
   .WithArguments(["rev-parse", "--short", "HEAD"])

   // Avoid (shell interpretation issues)
   .WithArguments("rev-parse --short HEAD")
   ```

6. **Cancellation Tokens**: Always pass cancellation tokens for long-running operations.

7. **Error Preservation**: When catching `CommandExecutionException`, preserve it as inner exception for debugging.

8. **Exit Code Semantics**:
   - `docker image inspect`: Exit 0 = image exists, non-zero = doesn't exist
   - `git rev-parse`: Exit 0 = success, non-zero = not a git repo or other error

---

## TUnit 1.12.43

**TUnit** is a modern, fast, and flexible .NET testing framework with native async support and a fluent assertion API.

### Version Compatibility

- **Version**: 1.12.43
- Fully compatible with .NET 10
- Source generator-based (fast discovery)

### Key APIs for This Project

#### 1. Basic Test Structure

```csharp
using TUnit.Core;
using TUnit.Assertions;

public class GitOperationsTests
{
    [Test]
    public async Task GetCurrentCommitSha_ReturnsValidSha()
    {
        var gitOps = new GitOperations();
        var sha = await gitOps.GetCurrentCommitShaAsync("/path/to/repo");

        await Assert.That(sha).IsNotNull();
        await Assert.That(sha).HasLength().EqualTo(7);
    }
}
```

#### 2. Async Assertions (Critical Pattern)

All TUnit assertions are async and must be awaited:

```csharp
[Test]
public async Task MyTest()
{
    var result = Calculate();

    // MUST await assertions
    await Assert.That(result).IsEqualTo(42);
    await Assert.That(result).IsGreaterThan(0);
    await Assert.That(result).IsNotNull();
}
```

**Common Assertion Patterns:**

```csharp
// Equality
await Assert.That(actual).IsEqualTo(expected);

// Null checks
await Assert.That(value).IsNull();
await Assert.That(value).IsNotNull();

// Boolean
await Assert.That(condition).IsTrue();
await Assert.That(condition).IsFalse();

// String assertions
await Assert.That(text).Contains("substring");
await Assert.That(text).StartsWith("prefix");
await Assert.That(text).IsEmpty();

// Collection assertions
await Assert.That(collection).IsNotEmpty();
await Assert.That(collection).HasCount().EqualTo(5);
await Assert.That(collection).Contains(item);

// Numeric comparisons
await Assert.That(value).IsGreaterThan(0);
await Assert.That(value).IsLessThanOrEqualTo(100);
```

#### 3. Exception Assertions

```csharp
[Test]
public async Task BuildImage_WhenDockerNotRunning_ThrowsException()
{
    var service = new ContainerImageService();

    await Assert.ThrowsAsync<DockerNotRunningException>(
        () => service.BuildImageAsync("docker build .", "/path"));
}

// With message verification
[Test]
public async Task GetCommitSha_WhenNotGitRepo_ThrowsWithMessage()
{
    await Assert.That(async () => await gitOps.GetCurrentCommitShaAsync("/tmp"))
        .Throws<GitException>()
        .WithMessageContaining("not a git repository");
}
```

#### 4. Test Lifecycle Hooks

```csharp
public class ContainerImageServiceTests
{
    private ContainerImageService _service = null!;
    private string _testRepoPath = null!;

    // Runs once before all tests in this class
    [Before(HookType.Class)]
    public static async Task SetupClass()
    {
        // One-time expensive setup
        await InitializeDockerTestEnvironment();
    }

    // Runs before each test method
    [Before(HookType.Test)]
    public async Task SetupTest()
    {
        _testRepoPath = CreateTempGitRepo();
        _service = new ContainerImageService();
    }

    // Runs after each test method
    [After(HookType.Test)]
    public async Task CleanupTest()
    {
        CleanupTempRepo(_testRepoPath);
    }

    // Runs once after all tests in this class
    [After(HookType.Class)]
    public static async Task TeardownClass()
    {
        await CleanupDockerTestEnvironment();
    }

    [Test]
    public async Task ImageExists_WhenImagePresent_ReturnsTrue()
    {
        // Test implementation
    }
}
```

**Hook Types:**

| Hook Type | Scope | Use Case |
|-----------|-------|----------|
| `HookType.Assembly` | Entire test assembly | Global setup (test containers) |
| `HookType.Class` | All tests in class | Shared fixtures |
| `HookType.Test` | Individual test | Per-test isolation |

#### 5. Dependency Injection in Tests

```csharp
[Test]
public async Task MyTest()
{
    // Get optional service (returns null if not registered)
    var logger = TestContext.Current?.GetService<ILogger<MyTests>>();
    logger?.LogInformation("Test starting");

    // Get required service (throws if not registered)
    var service = TestContext.Current!.GetRequiredService<IMyService>();

    var result = await service.DoSomethingAsync();
    await Assert.That(result).IsNotNull();
}
```

#### 6. Test Output and Logging

```csharp
[Test]
public async Task BuildImage_LogsProgress(TestContext context)
{
    context.OutputWriter.WriteLine("Starting build test");

    var service = new ContainerImageService();
    await service.BuildImageAsync("docker build .", "/path");

    context.OutputWriter.WriteLine("Build completed successfully");
}
```

#### 7. Parameterized Tests

```csharp
// Using [Arguments]
[Test]
[Arguments("abc1234", true)]
[Arguments("invalid", false)]
[Arguments("", false)]
public async Task ValidateSha_ReturnsExpected(string sha, bool expected)
{
    var result = GitOperations.IsValidShortSha(sha);
    await Assert.That(result).IsEqualTo(expected);
}

// Using [MethodDataSource]
[Test]
[MethodDataSource(nameof(GetTestCases))]
public async Task BuildCommand_SubstitutesPlaceholders(
    string template,
    string expected)
{
    var result = CommandBuilder.SubstitutePlaceholders(template);
    await Assert.That(result).IsEqualTo(expected);
}

public static IEnumerable<(string Template, string Expected)> GetTestCases()
{
    yield return ("{ImageName}:tag", "myapp:tag");
    yield return ("{ProjectPath}", "src/Api/Api.csproj");
}
```

#### 8. Class Fixtures (Shared Data Sources)

```csharp
// Define the fixture
public class DockerFixture : IAsyncInitializer, IAsyncDisposable
{
    public string TestNetworkId { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Create Docker test network
        TestNetworkId = await CreateTestNetwork();
    }

    public async ValueTask DisposeAsync()
    {
        await RemoveTestNetwork(TestNetworkId);
    }
}

// Use the fixture
[ClassDataSource<DockerFixture>(Shared = SharedType.PerClass)]
public class ContainerTests(DockerFixture fixture)
{
    [Test]
    public async Task Container_JoinsTestNetwork()
    {
        // Use fixture.TestNetworkId
        await Assert.That(fixture.TestNetworkId).IsNotNull();
    }
}
```

#### 9. Async Operation Assertions

```csharp
[Test]
public async Task BuildImage_CompletesWithinTimeout()
{
    await Assert.That(async () => await service.BuildImageAsync(...))
        .CompletesWithin(TimeSpan.FromMinutes(5));
}

[Test]
public async Task SlowOperation_HandlesTimeout()
{
    await Assert.That(async () => await LongRunningOp())
        .Throws<TimeoutException>();
}
```

### Gotchas and Best Practices

1. **Always Await Assertions**: Forgetting `await` will cause assertions to not execute:
   ```csharp
   // WRONG - assertion never runs!
   Assert.That(result).IsEqualTo(42);

   // CORRECT
   await Assert.That(result).IsEqualTo(42);
   ```

2. **TestContext Injection**: Inject `TestContext` as a method parameter, not via constructor:
   ```csharp
   [Test]
   public async Task MyTest(TestContext context)
   {
       context.OutputWriter.WriteLine("Log message");
   }
   ```

3. **Hook Method Signatures**:
   - `[Before/After(HookType.Class)]` and `[Before/After(HookType.Assembly)]` must be `static`
   - `[Before/After(HookType.Test)]` can be instance methods
   - All can be async

4. **Shared Fixtures**: Use `SharedType.PerClass` for fixtures shared across tests in a class, `SharedType.Globally` for assembly-wide sharing.

5. **Assertion Chaining**: Chain multiple assertions on the same value:
   ```csharp
   await Assert.That(result)
       .IsNotNull()
       .And.HasLength().GreaterThan(0);
   ```

6. **Exception Message Verification**: Use `WithMessageContaining` for partial matches:
   ```csharp
   await Assert.That(() => FailingMethod())
       .Throws<MyException>()
       .WithMessageContaining("specific error");
   ```

7. **Test Isolation**: Prefer `[Before(HookType.Test)]` over constructors for per-test setup to ensure async support.

8. **Expensive Setup**: Move expensive setup (Docker containers, databases) to `[Before(HookType.Class)]` or use class fixtures.

---

## Summary

| Library | Version | Primary Use in Project |
|---------|---------|------------------------|
| Aspire.Hosting | 13.1.0 | AppHost eventing, custom annotations, resource lifecycle |
| CliWrap | 3.10.0 | Git CLI operations, Docker CLI operations, streaming build output |
| TUnit | 1.12.43 | Unit and integration testing with async support |

### Quick Reference: Event Flow

```
AppHost Startup
    |
    v
[BeforeStartEvent] <-- SharedResourceBuildService subscribes here
    |
    +-> For each resource with SharedResourceAnnotation:
    |       |
    |       +-> CliWrap: git rev-parse --short HEAD (get commit SHA)
    |       +-> CliWrap: docker image inspect (check if image exists)
    |       +-> CliWrap: dotnet publish /t:PublishContainer (build if needed)
    |
    v
[AfterResourcesCreatedEvent]
    |
    v
Resources Start (containers with built images)
```
