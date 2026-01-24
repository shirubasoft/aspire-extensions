# TASK-011: SharedResourceExtensions

## Status
pending

## Dependencies
- TASK-001
- TASK-002
- TASK-003
- TASK-004
- TASK-006
- TASK-008
- TASK-009
- TASK-010

## Description
Implement the `SharedResourceExtensions` static class providing extension methods for configuring shared resources:

1. `WithSharedResourceMetadata` overload with parameters and optional configure action
2. `WithSharedResourceMetadata` overload with pre-built annotation
3. `AddSharedResourceSupport` extension for `IDistributedApplicationBuilder`

The extension methods must:
- Validate inputs (null checks, format validation)
- Create and attach `SharedResourceAnnotation` to container resources
- Register all services and event subscriber via `AddSharedResourceSupport`
- Support fluent chaining
- Include comprehensive XML documentation with examples

## Contract Reference
- docs/contracts/lib-shared-resource-extensions.md

## Inputs
- All prior tasks (this integrates everything)

## Outputs
- `src/SharedResources/Extensions/SharedResourceExtensions.cs`
- Unit tests with mocked Aspire builder

## Test Cases
- Test case 1: `WithSharedResourceMetadata` with parameters creates correct annotation
- Test case 2: `WithSharedResourceMetadata` with annotation attaches it correctly
- Test case 3: Configure action is invoked and options applied
- Test case 4: Returns builder for chaining
- Test case 5: Validates GitHubRepository format (must be "org/repo")
- Test case 6: Throws `ArgumentNullException` for null builder
- Test case 7: Throws `ArgumentNullException` for null required parameters
- Test case 8: Throws `ArgumentException` for invalid GitHubRepository format
- Test case 9: `AddSharedResourceSupport` registers all services
- Test case 10: `AddSharedResourceSupport` subscribes to `BeforeStartEvent`
- Test case 11: `AddSharedResourceSupport` binds configuration

## Test Filter
SharedResourceExtensionsTests

## Definition of Done
- [ ] Both `WithSharedResourceMetadata` overloads implemented
- [ ] `AddSharedResourceSupport` extension implemented
- [ ] Input validation with appropriate exceptions
- [ ] GitHubRepository format validation (exactly one `/`)
- [ ] Service registration: IRepositoryPathResolver, IGitOperations, IContainerImageService, SharedResourceBuildService
- [ ] Configuration binding for SharedResourceConfiguration
- [ ] Event subscription for BeforeStartEvent
- [ ] Fluent chaining supported
- [ ] XML documentation with examples
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "SharedResourceExtensionsTests"
```

## Complexity
M

## Notes
- Use `builder.WithAnnotation()` to attach annotation
- Use `builder.Services.TryAddSingleton<>()` for service registration
- Use `builder.Services.Configure<>()` for configuration binding
- Use `builder.Eventing.Subscribe<BeforeStartEvent>()` for event subscription
- Validation helper: `ValidateGitHubRepository(string)`
