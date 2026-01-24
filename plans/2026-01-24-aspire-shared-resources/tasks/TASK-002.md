# TASK-002: SharedResourceAnnotation Class

## Status
pending

## Dependencies
- TASK-001

## Description
Implement the `SharedResourceAnnotation` class that marks container resources as coming from external repositories. This annotation contains metadata needed to locate source code and build container images.

The class must:
1. Implement `IResourceAnnotation` from Aspire.Hosting
2. Define all required and optional properties per contract
3. Include `GetEffectiveImageName()` method
4. Include comprehensive XML documentation
5. Implement validation logic for property values

## Contract Reference
- docs/contracts/lib-shared-resource-annotation.md

## Inputs
- TASK-001: Project structure with Aspire.Hosting reference

## Outputs
- `src/SharedResources/Annotations/SharedResourceAnnotation.cs`
- Unit tests in test project

## Test Cases
- Test case 1: Creating annotation with all required properties succeeds
- Test case 2: `GetEffectiveImageName()` returns `ImageName` when set
- Test case 3: `GetEffectiveImageName()` returns `ServiceName` when `ImageName` is null
- Test case 4: Default values are correct (`DefaultBranch` = "main")
- Test case 5: Annotation implements `IResourceAnnotation`

## Test Filter
SharedResourceAnnotationTests

## Definition of Done
- [ ] Class implements `IResourceAnnotation`
- [ ] All required properties defined with `required` modifier
- [ ] All optional properties have correct defaults
- [ ] `GetEffectiveImageName()` method works correctly
- [ ] XML documentation complete for all public members
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "SharedResourceAnnotationTests"
```

## Complexity
S

## Notes
- Use `init` accessors for immutability
- Properties: `GitHubRepository`, `ServiceName`, `DefaultBranch`, `ImageBuildCommand`, `ProjectPath`, `ImageName`
- Placeholder documentation: {ProjectPath}, {ImageName}, {ImageTag}, {RepoPath}
