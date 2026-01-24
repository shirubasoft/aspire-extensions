# TASK-007: ContainerImageInfo Class

## Status
pending

## Dependencies
- TASK-001

## Description
Implement the `ContainerImageInfo` class that holds information about a container image. This is a simple data class returned by `IContainerImageService.GetImageInfoAsync()`.

The class must:
1. Define `Id` property (SHA256 hash)
2. Define `FullName` property (name:tag)
3. Define `Created` property (DateTimeOffset)
4. Define `Size` property (bytes)
5. Implement `FormattedSize` computed property
6. Use `required` modifiers for required properties
7. Include comprehensive XML documentation

## Contract Reference
- docs/contracts/lib-container-image-service.md (ContainerImageInfo section)

## Inputs
- TASK-001: Project structure

## Outputs
- `src/SharedResources/Services/ContainerImageInfo.cs`
- Unit tests in test project

## Test Cases
- Test case 1: All required properties can be set via init
- Test case 2: `FormattedSize` returns "B" for bytes < 1024
- Test case 3: `FormattedSize` returns "KB" for kilobytes
- Test case 4: `FormattedSize` returns "MB" for megabytes
- Test case 5: `FormattedSize` returns "GB" for gigabytes
- Test case 6: `FormattedSize` formats with 2 decimal places

## Test Filter
ContainerImageInfoTests

## Definition of Done
- [ ] Class has all required properties with `required` modifier
- [ ] `init` accessors used for immutability
- [ ] `FormattedSize` correctly formats bytes to human-readable size
- [ ] XML documentation complete
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "ContainerImageInfoTests"
```

## Complexity
S

## Notes
- Size formatting: B, KB, MB, GB, TB
- Use double for size calculation to handle large values
- Format pattern: `{len:0.##} {unit}`
