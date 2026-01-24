# TASK-005: Custom Exception Classes

## Status
pending

## Dependencies
- TASK-001

## Description
Implement all custom exception classes needed by the library:
1. `RepositoryNotFoundException` - When repository path cannot be resolved
2. `GitOperationException` - When git commands fail
3. `ContainerOperationException` - Base exception for container failures
4. `ContainerBuildException` - When container build fails
5. `SharedResourceBuildException` - When shared resource processing fails
6. `SharedResourceError` - Error information class (not an exception)

Each exception must include:
- Meaningful constructors with relevant context properties
- Helper factory methods where specified in contracts
- `GetDetailedMessage()` methods where specified
- Serialization support with `[Serializable]` attribute

## Contract Reference
- docs/contracts/lib-repository-path-resolver.md (RepositoryNotFoundException)
- docs/contracts/lib-git-operations.md (GitOperationException)
- docs/contracts/lib-container-image-service.md (ContainerOperationException, ContainerBuildException)
- docs/contracts/lib-shared-resource-build-service.md (SharedResourceBuildException, SharedResourceError)

## Inputs
- TASK-001: Project structure

## Outputs
- `src/SharedResources/Exceptions/RepositoryNotFoundException.cs`
- `src/SharedResources/Exceptions/GitOperationException.cs`
- `src/SharedResources/Exceptions/ContainerOperationException.cs`
- `src/SharedResources/Exceptions/ContainerBuildException.cs`
- `src/SharedResources/Exceptions/SharedResourceBuildException.cs`
- `src/SharedResources/Exceptions/SharedResourceError.cs`
- Unit tests for all exceptions

## Test Cases
- Test case 1: `RepositoryNotFoundException.GetDetailedMessage()` includes setup instructions
- Test case 2: `GitOperationException.GitNotInstalled()` factory method creates correct exception
- Test case 3: `GitOperationException.NotARepository()` factory method creates correct exception
- Test case 4: `ContainerOperationException.DockerNotAvailable()` factory method creates correct exception
- Test case 5: `ContainerBuildException.GetDetailedMessage()` includes build output and troubleshooting
- Test case 6: `SharedResourceBuildException` aggregates multiple errors correctly
- Test case 7: All exceptions include inner exception constructor overloads

## Test Filter
*ExceptionTests

## Definition of Done
- [ ] All exception classes implemented per contracts
- [ ] Factory methods implemented where specified
- [ ] `GetDetailedMessage()` methods provide actionable guidance
- [ ] `[Serializable]` attribute on all exceptions
- [ ] Context properties (Command, ExitCode, StandardError, etc.) exposed
- [ ] XML documentation complete
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "ExceptionTests"
```

## Complexity
M

## Notes
- `ContainerBuildException` inherits from `ContainerOperationException`
- `SharedResourceError` is a simple class, not an exception
- Error messages should be user-friendly and actionable
