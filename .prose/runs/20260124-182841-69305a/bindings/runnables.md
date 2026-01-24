# runnables

kind: let

source:
```prose
let runnables = session: usability_verifier
  prompt: "Extract ALL runnable verifications from DoD..."
```

---

## Tests

| Name | Command | Expected Behavior |
|------|---------|-------------------|
| Run all tests | `dotnet test` | All unit and integration tests pass with exit code 0 |
| Run specific test project | `dotnet test tests/SharedResources.Tests/SharedResources.Tests.csproj` | All tests in the SharedResources.Tests project pass |
| Run tests with verbosity | `dotnet test --verbosity normal` | All tests pass with detailed output visible |

## Samples

| Name | Command | Expected Behavior |
|------|---------|-------------------|
| Run Sample AppHost | `cd samples/SampleAppHost && dotnet run` | 1. SharedResourceBuildService logs "Checking shared resources..." 2. For each shared resource: repository path resolved, commit SHA retrieved, image existence checked, if missing image is built. 3. All containers start successfully |
| Verify caching (restart) | Stop AppHost, then `cd samples/SampleAppHost && dotnet run` | Should log "Image exists" - no rebuild occurs |

## APIs

### SharedResourceAnnotation

| Name | Verification | Expected Behavior |
|------|--------------|-------------------|
| Annotation construction | Create `SharedResourceAnnotation` with GitHubRepository, ServiceName, ImageBuildCommand, ProjectPath, DefaultBranch | Must compile and instantiate without errors |
| GetEffectiveImageName | `annotation.GetEffectiveImageName()` where ImageName is null | Returns ServiceName value (e.g., "api-1") |

### Extension Methods

| Name | Verification | Expected Behavior |
|------|--------------|-------------------|
| AddSharedResourceSupport | `builder.AddSharedResourceSupport()` | Must compile and register services |
| WithSharedResourceMetadata | `builder.AddContainer(...).WithSharedResourceMetadata(...).WithEnvironment(...).WithHttpEndpoint(...)` | Must compile and chain correctly with Aspire builder pattern |

### IRepositoryPathResolver

| Name | Verification | Expected Behavior |
|------|--------------|-------------------|
| ResolveRepositoryPathAsync | `await resolver.ResolveRepositoryPathAsync("myorg/repo-1", "api-1", ct)` | Returns valid directory path; `Directory.Exists(path)` returns true |

### IGitOperations

| Name | Verification | Expected Behavior |
|------|--------------|-------------------|
| GetCurrentCommitShaAsync | `await gitOps.GetCurrentCommitShaAsync("/path/to/repo", ct)` | Returns 7-character short SHA |
| GetCurrentBranchAsync | `await gitOps.GetCurrentBranchAsync("/path/to/repo", ct)` | Returns non-empty branch name |
| HasUncommittedChangesAsync | `await gitOps.HasUncommittedChangesAsync("/path/to/repo", ct)` | Returns bool without exception |
| IsGitRepositoryAsync | `await gitOps.IsGitRepositoryAsync("/path/to/repo", ct)` | Returns true for valid git repository |

### IContainerImageService

| Name | Verification | Expected Behavior |
|------|--------------|-------------------|
| IsDockerAvailableAsync | `await containerService.IsDockerAvailableAsync(ct)` | Returns true when Docker daemon is running |
| ImageExistsLocallyAsync | `await containerService.ImageExistsLocallyAsync("image", "tag", ct)` | Returns bool without exception |
| BuildImageAsync | `await containerService.BuildImageAsync("dotnet publish ...", "/working/dir", ct)` | Completes successfully or throws ContainerBuildException |

## CLI Tools

| Name | Command | Expected Behavior |
|------|---------|-------------------|
| Build solution | `dotnet build` | Solution builds with no errors and no warnings |
| Verify Docker available | `docker info` | Must succeed; Docker daemon is running |
| Check built images | `docker images \| grep api-1` | After sample runs, shows the api-1 image with commit SHA tag |
| Verify git SHA retrieval | `git rev-parse --short=7 HEAD` | Returns 7-character SHA |
| Verify git dirty check | `git status --porcelain` | Returns empty for clean, non-empty for dirty |

## Other

### Integration Test Scenarios

| Scenario | Verification Method | Expected Behavior |
|----------|---------------------|-------------------|
| Image missing | Run sample with no pre-existing image, then `docker image inspect image:tag` | Build executes, image created, inspect succeeds |
| Image exists | Run sample twice, check logs | Second run logs "Image exists", build skipped |
| Dirty working tree | Make uncommitted changes, run sample, check logs | Warning logged, uses HEAD SHA |
| Invalid repo path | Configure invalid path, run sample | RepositoryNotFoundException with instructions in message |
| Git not installed | Remove git from PATH, run sample | GitOperationException with "Git is not installed" message |
| Docker not running | Stop Docker daemon, run sample | ContainerOperationException with "Docker is not available" message |
| Build fails | Configure invalid build command, run sample | ContainerBuildException with full build output |

### Performance Verification

| Name | Verification Method | Expected Behavior |
|------|---------------------|-------------------|
| Image existence check speed | Time the `ImageExistsLocallyAsync` call | Completes in < 1 second |
| No unnecessary rebuilds | Run sample twice with same commit | Second run does not rebuild |
| Build output streaming | Run sample with missing image, observe logs | Build output streams in real-time during build |

### Code Quality Verification

| Name | Verification Method | Expected Behavior |
|------|---------------------|-------------------|
| No compiler warnings | `dotnet build` | Zero warnings in build output |
| Nullable reference types | Inspect code for nullable annotations | All nullable references properly handled |
| XML documentation | Inspect public APIs | All public types and members have XML comments |
| Async/CancellationToken | Inspect async methods | All async methods accept and respect CancellationToken |
