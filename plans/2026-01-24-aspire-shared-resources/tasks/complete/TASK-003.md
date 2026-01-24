# TASK-003: SharedResourceConfiguration Class

## Status
pending

## Dependencies
- TASK-001

## Description
Implement the `SharedResourceConfiguration` class for binding shared resource settings from IConfiguration. This class defines how repository paths and behavior can be configured via user-secrets, environment variables, or appsettings.

The class must:
1. Define `SectionName` constant as "SharedResources"
2. Define `RepositoriesBasePath` property
3. Define `PromptForMissingPaths` property with default `true`
4. Define `RepositoryPaths` dictionary for explicit path mappings
5. Include comprehensive XML documentation

## Contract Reference
- docs/contracts/lib-shared-resource-annotation.md (SharedResourceConfiguration section)

## Inputs
- TASK-001: Project structure

## Outputs
- `src/SharedResources/Configuration/SharedResourceConfiguration.cs`
- Unit tests in test project

## Test Cases
- Test case 1: Default values are correct (`PromptForMissingPaths` = true, empty dictionary)
- Test case 2: `SectionName` constant is "SharedResources"
- Test case 3: Configuration can be bound from IConfiguration
- Test case 4: `RepositoryPaths` dictionary stores and retrieves values correctly
- Test case 5: All properties are settable (for options binding)

## Test Filter
SharedResourceConfigurationTests

## Definition of Done
- [ ] Class has `SectionName` constant = "SharedResources"
- [ ] `RepositoriesBasePath` property defined as nullable string
- [ ] `PromptForMissingPaths` defaults to true
- [ ] `RepositoryPaths` dictionary initialized
- [ ] XML documentation complete
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "SharedResourceConfigurationTests"
```

## Complexity
S

## Notes
- This class is used with `IOptions<SharedResourceConfiguration>` pattern
- Configuration priority documented in contract
- Environment variable format: `SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}`
