# Library Research Summary

Research completed for the aspire-extensions project. Full documentation written to `/home/danielreis/code/aspire-extensions/docs/libraries.md`.

---

## Libraries Researched

### 1. Aspire.Hosting 13.1.0

**Status**: Fully compatible with .NET 10

**Key Findings for This Project**:

- **Eventing API**: Use `builder.Eventing.Subscribe<BeforeStartEvent>()` to hook into AppHost startup
- **Service Access**: Access DI services via `@event.Services.GetRequiredService<T>()`
- **Custom Annotations**: Implement `IResourceAnnotation` for `SharedResourceAnnotation`
- **Resource Iteration**: Use `resource.TryGetAnnotationsOfType<T>()` to find annotated resources

**Critical Pattern for BeforeStartEvent**:
```csharp
builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
{
    var service = @event.Services.GetRequiredService<SharedResourceBuildService>();
    await service.OnBeforeStartAsync(@event, ct);
});
```

**Gotchas**:
- Event handlers must return `Task` or `ValueTask`
- Always respect cancellation tokens
- `BeforeStartEvent` fires before any resources start (ideal timing)

---

### 2. CliWrap 3.10.0

**Status**: Fully compatible with .NET 10, no native dependencies

**Key Findings for This Project**:

- **Buffered Execution**: Use `ExecuteBufferedAsync()` for simple commands (git rev-parse, docker inspect)
- **Streaming Output**: Use `ListenAsync()` for long-running builds with real-time output
- **Working Directory**: Always set `WithWorkingDirectory()` for git/docker commands
- **Validation**: Use `WithValidation(CommandResultValidation.None)` when non-zero exit codes are expected

**Patterns Used**:

1. **Git SHA Retrieval**:
```csharp
var result = await Cli.Wrap("git")
    .WithArguments(["rev-parse", "--short", "HEAD"])
    .WithWorkingDirectory(repoPath)
    .ExecuteBufferedAsync();
var sha = result.StandardOutput.Trim();
```

2. **Docker Image Check** (expects failures):
```csharp
var result = await Cli.Wrap("docker")
    .WithArguments(["image", "inspect", $"{imageName}:{tag}"])
    .WithValidation(CommandResultValidation.None)
    .ExecuteBufferedAsync();
return result.ExitCode == 0;
```

3. **Streaming Build Output**:
```csharp
await foreach (var cmdEvent in cmd.ListenAsync(ct))
{
    switch (cmdEvent)
    {
        case StandardOutputCommandEvent stdOut:
            logger.LogInformation("Build: {Output}", stdOut.Text);
            break;
    }
}
```

**Gotchas**:
- Pass arguments as string array to avoid shell escaping issues
- Docker inspect returns non-zero when image doesn't exist (expected behavior)
- Always pass cancellation tokens for long operations

---

### 3. TUnit 1.12.43

**Status**: Fully compatible with .NET 10, source generator-based

**Key Findings for This Project**:

- **Async Assertions**: All assertions MUST be awaited (`await Assert.That(...)`)
- **Test Output**: Inject `TestContext` as method parameter for logging
- **Lifecycle Hooks**: Use `[Before(HookType.Test)]` for per-test setup, `[Before(HookType.Class)]` for shared setup
- **Exception Testing**: Use `Assert.ThrowsAsync<T>()` for async exception verification

**Critical Patterns**:

1. **Basic Async Test**:
```csharp
[Test]
public async Task MyTest()
{
    var result = await service.DoSomethingAsync();
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Value).IsEqualTo(expected);
}
```

2. **Test with Output**:
```csharp
[Test]
public async Task MyTest(TestContext context)
{
    context.OutputWriter.WriteLine("Starting test");
    // test code
}
```

3. **Exception Assertions**:
```csharp
await Assert.ThrowsAsync<RepositoryNotFoundException>(
    () => resolver.ResolveRepositoryPathAsync("invalid/repo"));
```

4. **Lifecycle Hooks**:
```csharp
[Before(HookType.Test)]
public async Task Setup()
{
    _service = new MyService();
}

[After(HookType.Test)]
public async Task Cleanup()
{
    _service?.Dispose();
}
```

**Gotchas**:
- Forgetting `await` on assertions causes tests to pass incorrectly
- `[Before/After(HookType.Class)]` must be static methods
- TestContext is injected as method parameter, not via constructor

---

## Version Compatibility Matrix

| Library | Version | .NET 10 Support | Notes |
|---------|---------|-----------------|-------|
| Aspire.Hosting | 13.1.0 | Yes | Requires .NET 8+ |
| CliWrap | 3.10.0 | Yes | No native dependencies |
| TUnit | 1.12.43 | Yes | Source generator-based |

---

## Files Created

1. **Full Documentation**: `/home/danielreis/code/aspire-extensions/docs/libraries.md`
   - Comprehensive API documentation
   - Code examples for all key patterns
   - Gotchas and best practices
   - Event flow diagram

---

## Recommendations for Implementation

1. **Aspire Eventing**: Register `SharedResourceBuildService` in DI and subscribe to `BeforeStartEvent`

2. **CliWrap Usage**:
   - Use buffered execution for git/docker inspection commands
   - Use event streaming for build commands to show progress
   - Handle non-zero exit codes explicitly for docker inspect

3. **TUnit Testing**:
   - Structure tests with `[Before(HookType.Test)]` for per-test isolation
   - Use `[Before(HookType.Class)]` for expensive Docker setup
   - Always await assertions
   - Use TestContext for test output logging
