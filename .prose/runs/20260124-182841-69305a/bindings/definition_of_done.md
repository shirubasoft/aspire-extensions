# definition_of_done

kind: let

source:
```prose
let definition_of_done = session "Read {plan_folder}/DEFINITION-OF-DONE.md"
  context: plan_folder
```

---

# Definition of Done: Aspire Shared Resources

This document defines the criteria that must be met for components, features, and the project as a whole to be considered complete.

---

## Component-Level DoD

For each component to be considered "done":

### Code Quality
- [ ] All acceptance criteria from task are met
- [ ] Code follows C# coding conventions and project style
- [ ] No compiler warnings
- [ ] Code is properly documented with XML comments
- [ ] Nullable reference types are properly handled

### Testing
- [ ] All unit tests pass (`dotnet test`)
- [ ] All integration tests pass
- [ ] New code has corresponding test coverage
- [ ] Edge cases identified in contracts are tested

### Functionality
- [ ] Component runs without errors
- [ ] Component handles errors gracefully with meaningful messages
- [ ] Logging is implemented at appropriate levels (Debug, Info, Warning, Error)
- [ ] Async/await patterns are properly implemented with CancellationToken support

### Documentation
- [ ] API documentation (XML comments) is complete
- [ ] Contract documentation in `/docs/contracts/` is updated if API changes
- [ ] Any new configuration options are documented

### Version Control
- [ ] Code is committed with meaningful message
- [ ] No unrelated changes in commit
- [ ] Branch is up-to-date with main

---

## Feature-Level DoD

For each feature to be considered "done":

### Completeness
- [ ] All component tasks for the feature are complete
- [ ] Feature can be demonstrated end-to-end
- [ ] All edge cases identified in PLAN.md are handled:
  - Repository path doesn't exist
  - Git not installed
  - Docker not running
  - Build command fails
  - Image name collision
  - Network issues
  - Concurrent builds
  - Uncommitted changes

### Integration
- [ ] Feature integrates correctly with other components
- [ ] No breaking changes to existing APIs
- [ ] Extension methods chain properly with Aspire builder pattern

### Performance
- [ ] Image existence check completes in < 1 second
- [ ] No unnecessary rebuilds when image already exists
- [ ] Build output streams in real-time

### User Experience
- [ ] Error messages provide actionable guidance
- [ ] Configuration instructions are clear when paths are missing
- [ ] Logging provides appropriate visibility into operations

---

## Project-Level DoD

For the project to be considered "done":

### All Features Complete
- [ ] SharedResourceAnnotation and options classes implemented
- [ ] SharedResourceExtensions (WithSharedResourceMetadata, AddSharedResourceSupport) implemented
- [ ] IRepositoryPathResolver and implementation complete
- [ ] IGitOperations and implementation complete
- [ ] IContainerImageService and implementation complete
- [ ] SharedResourceBuildService (event subscriber) complete
- [ ] All exceptions (RepositoryNotFoundException, GitOperationException, etc.) implemented

### All Tests Pass
- [ ] All unit tests pass: `dotnet test`
- [ ] Integration tests verify end-to-end flows
- [ ] Tests cover all contracts in `/docs/contracts/`

### Sample/Demo Works
- [ ] Sample AppHost runs without errors
- [ ] Sample demonstrates container building from external repos
- [ ] Sample shows caching behavior (image reuse on same commit)

### Documentation Complete
- [ ] README.md provides quick start guide
- [ ] `/docs/` folder contains comprehensive documentation
- [ ] API contracts fully documented in `/docs/contracts/`
- [ ] Configuration options documented (user-secrets, env vars, appsettings)

### Success Criteria from PLAN.md Verified
- [ ] Container resources can be annotated with `SharedResourceAnnotation`
- [ ] System automatically builds images before AppHost starts
- [ ] Image tags use commit SHA for cache invalidation
- [ ] Repository paths can be configured via user-secrets, env vars, or prompts
- [ ] Existing images are reused (no unnecessary rebuilds)
- [ ] Dirty working tree logs warning but continues
- [ ] Build output is visible during builds
- [ ] Errors provide clear troubleshooting guidance

---

## Runnable Verification

### Tests

All tests must pass with the following command:

```bash
# Run all tests from solution root
dotnet test

# Run specific test projects (if organized by component)
dotnet test tests/SharedResources.Tests/SharedResources.Tests.csproj

# Run with verbosity for debugging
dotnet test --verbosity normal
```

### Sample AppHost

The sample AppHost must run and demonstrate the feature:

```bash
# Navigate to sample AppHost
cd samples/SampleAppHost

# Run the AppHost (will trigger container builds)
dotnet run

# Expected behavior:
# 1. SharedResourceBuildService logs "Checking shared resources..."
# 2. For each shared resource:
#    - Repository path is resolved
#    - Commit SHA is retrieved
#    - Image existence is checked
#    - If missing, image is built
# 3. All containers start successfully
```

### API Verification Checklist

#### SharedResourceAnnotation
```csharp
// Must compile and work
var annotation = new SharedResourceAnnotation
{
    GitHubRepository = "myorg/repo-1",
    ServiceName = "api-1",
    ImageBuildCommand = "dotnet publish {ProjectPath} /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
    ProjectPath = "src/Api/Api.csproj",
    DefaultBranch = "main"
};

// GetEffectiveImageName returns ServiceName when ImageName is null
Assert.Equal("api-1", annotation.GetEffectiveImageName());
```

#### Extension Methods
```csharp
// Must compile and chain correctly
var builder = DistributedApplication.CreateBuilder(args);

builder.AddSharedResourceSupport();

builder.AddContainer("api-1", "api-1")
    .WithSharedResourceMetadata(
        gitHubRepository: "myorg/repo-1",
        serviceName: "api-1",
        imageBuildCommand: "...",
        options => { options.ProjectPath = "src/Api/Api.csproj"; })
    .WithEnvironment("KEY", "value")
    .WithHttpEndpoint(5001);
```

#### IRepositoryPathResolver
```csharp
// Path resolution must work with all priority sources
var path = await resolver.ResolveRepositoryPathAsync("myorg/repo-1", "api-1", ct);
// Must return valid directory path
Assert.True(Directory.Exists(path));
```

#### IGitOperations
```csharp
// Git operations must return expected values
var sha = await gitOps.GetCurrentCommitShaAsync("/path/to/repo", ct);
Assert.Equal(7, sha.Length); // 7-char short SHA

var branch = await gitOps.GetCurrentBranchAsync("/path/to/repo", ct);
Assert.False(string.IsNullOrEmpty(branch));

var hasChanges = await gitOps.HasUncommittedChangesAsync("/path/to/repo", ct);
// Returns bool without exception

var isRepo = await gitOps.IsGitRepositoryAsync("/path/to/repo", ct);
Assert.True(isRepo);
```

#### IContainerImageService
```csharp
// Container operations must work with Docker daemon
var isAvailable = await containerService.IsDockerAvailableAsync(ct);
Assert.True(isAvailable);

var exists = await containerService.ImageExistsLocallyAsync("image", "tag", ct);
// Returns bool without exception

await containerService.BuildImageAsync("dotnet publish ...", "/working/dir", ct);
// Must complete or throw ContainerBuildException
```

### Integration Test Scenarios

The following scenarios must be testable:

| Scenario | Expected Behavior | Verification |
|----------|-------------------|--------------|
| Image missing | Build executes, image created | `docker image inspect image:tag` succeeds |
| Image exists | Build skipped | Log shows "Image exists" |
| Dirty working tree | Warning logged, uses HEAD SHA | Warning in logs |
| Invalid repo path | RepositoryNotFoundException | Exception message includes instructions |
| Git not installed | GitOperationException | "Git is not installed" message |
| Docker not running | ContainerOperationException | "Docker is not available" message |
| Build fails | ContainerBuildException | Includes full build output |

---

## Verification Commands Summary

```bash
# 1. Build the solution
dotnet build

# 2. Run all tests
dotnet test

# 3. Run the sample AppHost
cd samples/SampleAppHost && dotnet run

# 4. Verify Docker integration manually
docker info  # Must succeed
docker images | grep api-1  # After sample runs

# 5. Verify git integration manually
git rev-parse --short=7 HEAD  # Must return 7-char SHA
git status --porcelain  # For dirty check

# 6. Clean rebuild (verify caching works)
# Stop AppHost, restart - should say "Image exists"
```

---

## Sign-off Checklist

Before marking the project as complete:

- [ ] All component DoD checklists are satisfied
- [ ] All feature DoD checklists are satisfied
- [ ] All project DoD checklists are satisfied
- [ ] All runnable verifications pass
- [ ] Code has been reviewed
- [ ] Documentation reviewed for accuracy
- [ ] Sample demonstrates all key scenarios
