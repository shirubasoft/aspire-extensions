# TASK-004: SharedResourceOptions Class

## Status
pending

## Dependencies
- TASK-001

## Description
Implement the `SharedResourceOptions` class used with the `WithSharedResourceMetadata` extension method to provide optional configuration in a fluent manner.

The class must:
1. Define `DefaultBranch` property with default "main"
2. Define `ProjectPath` nullable property
3. Define `ImageName` nullable property
4. Include comprehensive XML documentation

## Contract Reference
- docs/contracts/lib-shared-resource-annotation.md (SharedResourceOptions section)

## Inputs
- TASK-001: Project structure

## Outputs
- `src/SharedResources/Configuration/SharedResourceOptions.cs`
- Unit tests in test project

## Test Cases
- Test case 1: Default `DefaultBranch` is "main"
- Test case 2: `ProjectPath` defaults to null
- Test case 3: `ImageName` defaults to null
- Test case 4: All properties are settable
- Test case 5: Can be used with Action<SharedResourceOptions> pattern

## Test Filter
SharedResourceOptionsTests

## Definition of Done
- [ ] `DefaultBranch` property with default "main"
- [ ] `ProjectPath` nullable property
- [ ] `ImageName` nullable property
- [ ] All properties use standard get/set accessors
- [ ] XML documentation complete
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "SharedResourceOptionsTests"
```

## Complexity
S

## Notes
- This is a simple POCO class for the options pattern
- Used by `WithSharedResourceMetadata` extension method
