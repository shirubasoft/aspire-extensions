# alternatives

kind: let

source:
```prose
let alternatives = session: solution_challenger
  prompt: "Critically analyze the plan and research alternatives..."
```

---

## Current Plan Analysis

### Strengths

1. **Clear Architecture**: The plan provides a well-documented architecture with explicit flow diagrams, API contracts, and expected behaviors. The eventing-based approach aligns with Aspire's design philosophy.

2. **Comprehensive API Design**: The API surface (annotations, extension methods, services) follows .NET conventions and provides appropriate extensibility points.

3. **Configuration Flexibility**: The multi-tier configuration resolution (user-secrets, environment variables, base path, interactive prompt) covers various development scenarios.

4. **Reasonable Technology Choices**: CliWrap for CLI execution is a sound choice - it's actively maintained, provides better async/cancellation support than raw `Process`, and avoids native dependencies that LibGit2Sharp would introduce.

5. **Commit SHA Tagging**: Using git commit SHA for image tags provides deterministic builds and effective caching.

### Potential Concerns

1. **Deprecated API Usage**: The plan uses `IDistributedApplicationLifecycleHook` which is now marked as obsolete in Aspire. The [official documentation](https://learn.microsoft.com/en-us/dotnet/aspire/app-host/eventing) recommends using `IDistributedApplicationEventingSubscriber` or the fluent `On*` extension methods instead.

2. **Reinventing Built-in Capabilities**: Aspire already has `AddDockerfile` and `WithDockerfile` methods that build containers at startup from Dockerfiles. The plan creates custom build infrastructure rather than leveraging these native capabilities.

3. **Complexity for the Problem Domain**: The custom annotation-based approach adds abstractions (SharedResourceAnnotation, IRepositoryPathResolver, IGitOperations, IContainerImageService) when Aspire's native container building might suffice with simpler patterns.

4. **Existing Solutions**: [Aspire.PolyRepo](https://github.com/Dutchskull/Aspire.PolyRepo) already addresses multi-repo support with repository cloning, project integration, and dependency management.

5. **Scope Ambiguity**: The plan conflates two concerns:
   - Repository path resolution and management
   - Container image building with SHA-based tagging

6. **Build Command Flexibility vs Safety**: Allowing arbitrary `imageBuildCommand` strings introduces complexity (placeholder substitution) and potential security concerns.

---

## Alternative Approaches Researched

### Alternative 1: Use Native Aspire `AddDockerfile`/`WithDockerfile`

**Description**: Leverage Aspire's built-in Dockerfile building capabilities instead of custom eventing infrastructure.

```csharp
// Instead of custom SharedResourceAnnotation:
var api1 = builder.AddDockerfile("api-1", "../external-repos/repo-1")
    .WithBuildArg("COMMIT_SHA", GetGitSha("../external-repos/repo-1"));
```

**Pros**:
- Uses officially supported, tested Aspire APIs
- No custom eventing infrastructure needed
- Automatic rebuild when Dockerfile changes
- Built-in build context management
- Future Aspire updates automatically benefit the solution

**Cons**:
- Requires Dockerfiles in source repos (may not exist for .NET container publishing workflows)
- Less control over image naming/tagging conventions
- Still requires manual path configuration

**Trade-offs**:
| Aspect | Current Plan | This Alternative |
|--------|--------------|------------------|
| Complexity | High (custom services, eventing) | Low (use built-in) |
| Maintenance | Custom code to maintain | Aspire team maintains |
| Flexibility | Full control | Limited to Dockerfile-based builds |
| Risk | Medium (new code) | Low (proven API) |

---

### Alternative 2: Adopt Aspire.PolyRepo Package

**Description**: Use the existing [Aspire.PolyRepo](https://github.com/Dutchskull/Aspire.PolyRepo) community package that already solves multi-repo management.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var repo1 = builder.AddRepository("repo-1")
    .WithUrl("https://github.com/myorg/repo-1")
    .WithDefaultBranch("main")
    .WithTargetPath("../repos/repo-1");

var api1 = builder.AddProjectFromRepository<Api1>(repo1)
    .WithReference(database);
```

**Pros**:
- Already exists and works
- Handles git cloning automatically
- Supports npm/node apps as well as .NET
- Community-tested

**Cons**:
- Limited to project references (no container mode support)
- Less active maintenance (small community project)
- May not support SHA-based container tagging
- Debugging is project-based, not container-based

**Trade-offs**:
| Aspect | Current Plan | This Alternative |
|--------|--------------|------------------|
| Time to implement | Weeks | Hours |
| Container support | Yes | No (project refs only) |
| SHA-based tagging | Yes | No |
| Maturity | New | Existing |

---

### Alternative 3: Simplified Eventing with Native Container Building

**Description**: Reduce scope to only the unique value-add (SHA-based tagging) and use Aspire's native container building for the rest.

```csharp
// Minimal custom extension
public static IResourceBuilder<ContainerResource> WithGitShaTag(
    this IResourceBuilder<ContainerResource> builder,
    string repositoryPath)
{
    var sha = GetCurrentCommitSha(repositoryPath);
    return builder.WithImage($"{builder.Resource.Name}:{sha}");
}

// Usage
var api1 = builder.AddDockerfile("api-1", "../repos/repo-1")
    .WithGitShaTag("../repos/repo-1");
```

**Pros**:
- Minimal custom code
- Leverages native Aspire building
- Focused scope on unique value (SHA tagging)
- Easy to maintain

**Cons**:
- Requires Dockerfiles
- No repository path discovery/prompting
- No build caching based on SHA

**Trade-offs**:
| Aspect | Current Plan | This Alternative |
|--------|--------------|------------------|
| Features | Full | Minimal |
| Complexity | High | Very Low |
| Unique value | All-in-one | SHA tagging only |
| Adoption effort | Medium | Very Low |

---

### Alternative 4: Use Modern Aspire Eventing API

**Description**: If proceeding with the custom approach, update to use the recommended eventing patterns instead of the deprecated `IDistributedApplicationLifecycleHook`.

```csharp
// Instead of IDistributedApplicationLifecycleHook:
builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
{
    foreach (var resource in @event.Model.Resources
        .Where(r => r.Annotations.OfType<SharedResourceAnnotation>().Any()))
    {
        await EnsureImageBuiltAsync(resource, ct);
    }
});

// Or per-resource using fluent API:
builder.AddContainer("api-1", "api-1")
    .OnBeforeResourceStarted(async (resource, evt, ct) =>
    {
        await EnsureImageBuiltAsync(resource, ct);
    });
```

**Pros**:
- Uses non-deprecated APIs
- Better type safety
- Aligns with Aspire 9.4+ best practices

**Cons**:
- Still requires custom services
- Same overall complexity

**Trade-offs**:
| Aspect | Current Plan (Lifecycle Hook) | Modern Eventing |
|--------|------------------------------|-----------------|
| API stability | Deprecated | Current |
| Migration effort | None | Small refactor |
| Future compatibility | Risk | Good |

---

### Alternative 5: Windows 365 Pattern - MicroserviceResource

**Description**: Follow the pattern used by Windows 365 team (mentioned in [Aspire Blog](https://devblogs.microsoft.com/aspire/aspire-windows-365/)) with a `MicroserviceResource` abstraction.

```csharp
// Each repo defines its own AppHost components
// Central AppHost references them as microservices
var api1 = builder.AddMicroservice("api-1")
    .WithRepository("myorg/repo-1")
    .WithMode(MicroserviceMode.Container) // or .Project
    .WithBuildCommand("dotnet publish ...");
```

**Pros**:
- Enterprise-proven pattern (Windows 365 uses it)
- Clean separation between project/container modes
- Explicit multi-repo coordination

**Cons**:
- Less documented (internal Microsoft pattern)
- Requires deeper understanding of Aspire internals
- Higher initial design effort

**Trade-offs**:
| Aspect | Current Plan | Microservice Pattern |
|--------|--------------|---------------------|
| Proven at scale | Unknown | Yes (Windows 365) |
| Documentation | Custom | Limited public docs |
| Abstraction level | Low (annotations) | High (resource type) |

---

## What the Current Plan Does Well

1. **Comprehensive Documentation**: The plan includes diagrams, tables, code samples, and edge case handling that would serve as excellent implementation guidance.

2. **User Experience Consideration**: The interactive prompting for missing paths, with save-to-secrets option, shows thoughtful UX design.

3. **Pragmatic Decisions**: Choosing CliWrap over LibGit2Sharp, accepting dirty working tree with warning (not error), and using short SHA are pragmatic choices.

4. **Extensibility**: The service interfaces (IRepositoryPathResolver, IGitOperations, IContainerImageService) allow for testing and customization.

---

## Recommendation

**Consider Hybrid Approach: Alternative 3 + Alternative 4**

The current plan has significant value but may be over-engineered for the core problem. I recommend:

1. **Reduce scope initially**: Start with Alternative 3 (minimal SHA tagging extension) to validate the approach with minimal investment.

2. **Use modern eventing (Alternative 4)**: If custom eventing is needed, use `Subscribe<BeforeStartEvent>` instead of the deprecated lifecycle hook.

3. **Defer repository management**: The repository path resolution logic adds complexity. Consider if simpler approaches (environment variables + documentation) suffice before building IRepositoryPathResolver.

4. **Evaluate Aspire.PolyRepo**: Before implementing from scratch, test if [Aspire.PolyRepo](https://github.com/Dutchskull/Aspire.PolyRepo) meets some requirements.

**Proceed as-is if**:
- The full feature set (interactive prompts, user-secrets persistence, multiple configuration sources) is confirmed as required
- Container-mode execution (not project references) is mandatory
- SHA-based image caching is a hard requirement
- The deprecated API concern is addressed by switching to modern eventing

**Reconsider if**:
- Project references (debugging source) are acceptable instead of containers
- Dockerfile-based builds are possible in the source repos
- The interactive prompting can be replaced with documentation + env vars

---

## Uncertainty Acknowledgment

- I could not find specific benchmarks comparing CliWrap overhead vs raw Process for the expected use case (few CLI calls at startup)
- The Windows 365 MicroserviceResource pattern is referenced in blogs but not publicly documented in detail
- Aspire.PolyRepo's maintenance status and compatibility with Aspire 13.1 is uncertain
- The exact deprecation timeline for `IDistributedApplicationLifecycleHook` is not specified in the documentation

---

## Sources

- [Eventing in Aspire - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/aspire/app-host/eventing)
- [Add Dockerfiles to your app model - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/aspire/app-host/withdockerfile)
- [Aspire.PolyRepo - GitHub](https://github.com/Dutchskull/Aspire.PolyRepo)
- [Aspire Multi-Repo Microservices - Windows 365 Integration Journey](https://devblogs.microsoft.com/aspire/aspire-windows-365/)
- [dotnet/aspire-samples container-build sample](https://github.com/dotnet/aspire-samples/tree/main/samples/container-build)
- [Aspire Roadmap 2025-2026 Discussion](https://github.com/dotnet/aspire/discussions/10644)
- [Multi-repo support Discussion](https://github.com/dotnet/aspire/discussions/1137)
