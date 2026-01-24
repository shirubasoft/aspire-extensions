# TASK-010: SharedResourceBuildService

## Status
pending

## Dependencies
- TASK-001
- TASK-002
- TASK-005
- TASK-006
- TASK-008
- TASK-009

## Description
Implement the `SharedResourceBuildService` class that ensures all shared resource container images are built before the AppHost starts. This is the core orchestration service that processes `BeforeStartEvent`.

The implementation must:
1. Subscribe to and handle `BeforeStartEvent` via `OnBeforeStartAsync()`
2. Discover all `ContainerResource` instances with `SharedResourceAnnotation`
3. For each shared resource:
   - Resolve repository path
   - Validate it's a git repository
   - Get current commit SHA
   - Check for uncommitted changes (warning only)
   - Determine image name and tag
   - Check if image exists locally
   - Build image if missing
   - Update container resource with correct tag
4. Aggregate errors and throw `SharedResourceBuildException` if any fail
5. Include comprehensive logging for user feedback

## Contract Reference
- docs/contracts/lib-shared-resource-build-service.md

## Inputs
- TASK-001: Project structure
- TASK-002: SharedResourceAnnotation
- TASK-005: SharedResourceBuildException, SharedResourceError
- TASK-006: IGitOperations
- TASK-008: IContainerImageService
- TASK-009: IRepositoryPathResolver

## Outputs
- `src/SharedResources/Eventing/SharedResourceBuildService.cs`
- Unit tests with mocked dependencies

## Test Cases
- Test case 1: No shared resources found - completes without error
- Test case 2: Single resource - image exists - skips build
- Test case 3: Single resource - image missing - builds successfully
- Test case 4: Multiple resources - all built/skipped correctly
- Test case 5: Resource with dirty working tree - logs warning, continues
- Test case 6: Repository path not found - adds to errors
- Test case 7: Git operation fails - adds to errors
- Test case 8: Build fails - adds to errors
- Test case 9: Docker not available - throws immediately
- Test case 10: Placeholder substitution works correctly ({RepoPath}, {ProjectPath}, {ImageName}, {ImageTag})
- Test case 11: Container resource tag is updated after build
- Test case 12: Multiple failures aggregated in `SharedResourceBuildException`

## Test Filter
SharedResourceBuildServiceTests

## Definition of Done
- [ ] `OnBeforeStartAsync` processes `BeforeStartEvent`
- [ ] Discovers resources via `DistributedApplicationModel.Resources`
- [ ] Filters for `ContainerResource` with `SharedResourceAnnotation`
- [ ] Full processing flow per contract
- [ ] Placeholder substitution: {RepoPath}, {ProjectPath}, {ImageName}, {ImageTag}
- [ ] Error aggregation with `SharedResourceBuildException`
- [ ] Logging: Info for status, Warning for dirty tree, Error for failures
- [ ] Docker availability check before processing
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "SharedResourceBuildServiceTests"
```

## Complexity
L

## Notes
- Processing is sequential per event (no parallel builds by default)
- `DistributedApplicationModel` from BeforeStartEvent provides resources
- Update container image tag may require reflection or Aspire internal APIs
- Log format matches contract examples for consistency
