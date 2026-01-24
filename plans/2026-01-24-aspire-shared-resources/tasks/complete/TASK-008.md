# TASK-008: IContainerImageService Interface and ContainerImageService Implementation

## Status
pending

## Dependencies
- TASK-001
- TASK-005
- TASK-007

## Description
Implement the `IContainerImageService` interface and `ContainerImageService` class for container image operations using the Docker CLI.

The implementation must:
1. Use CliWrap to execute Docker CLI commands
2. Implement `ImageExistsLocallyAsync()` - checks if image:tag exists
3. Implement `BuildImageAsync()` - executes build command with streaming output
4. Implement `TagImageAsync()` - tags an existing image
5. Implement `IsDockerAvailableAsync()` - validates Docker daemon
6. Implement `GetImageInfoAsync()` - gets image metadata
7. Handle command parsing for build operations
8. Include proper error handling with container exceptions
9. Include cancellation token support
10. Include logging with real-time build output streaming

## Contract Reference
- docs/contracts/lib-container-image-service.md

## Inputs
- TASK-001: Project structure with CliWrap reference
- TASK-005: ContainerOperationException, ContainerBuildException
- TASK-007: ContainerImageInfo class

## Outputs
- `src/SharedResources/Services/IContainerImageService.cs`
- `src/SharedResources/Services/ContainerImageService.cs`
- Unit tests with mocked Docker behavior
- Integration tests (marked for manual Docker availability)

## Test Cases
- Test case 1: `ImageExistsLocallyAsync` returns true for existing image
- Test case 2: `ImageExistsLocallyAsync` returns false for non-existent image
- Test case 3: `BuildImageAsync` executes build command successfully
- Test case 4: `BuildImageAsync` streams output to logger
- Test case 5: `BuildImageAsync` throws `ContainerBuildException` on failure with output
- Test case 6: `TagImageAsync` creates new tag successfully
- Test case 7: `IsDockerAvailableAsync` returns true when Docker running
- Test case 8: `IsDockerAvailableAsync` returns false when Docker not available
- Test case 9: `GetImageInfoAsync` returns info for existing image
- Test case 10: `GetImageInfoAsync` returns null for non-existent image
- Test case 11: Throws `ContainerOperationException` when Docker not installed
- Test case 12: Command parsing handles `dotnet publish` and `docker build`

## Test Filter
ContainerImageServiceTests

## Definition of Done
- [ ] Interface defined with all methods per contract
- [ ] Implementation uses CliWrap for Docker CLI execution
- [ ] Build command parsing extracts executable and arguments
- [ ] Build output streamed in real-time via logger
- [ ] Image existence check uses `docker image inspect`
- [ ] Docker availability check uses `docker info`
- [ ] Error handling with meaningful exceptions
- [ ] Cancellation token respected
- [ ] Timeouts configured (30s for quick ops, 10min for builds)
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "ContainerImageServiceTests"
```

## Complexity
L

## Notes
- Docker commands used:
  - `docker image inspect {name}:{tag}`
  - `docker tag {source} {target}`
  - `docker info`
  - `docker image inspect --format "..." {name}`
- Build command is generic (dotnet publish, docker build, etc.)
- Handle Win32Exception (error code 2) for docker not found
- ParseCommand helper extracts executable from first space-separated token
