# TASK-012: Sample AppHost Project

## Status
pending

## Dependencies
- TASK-011

## Description
Create a sample AppHost project that demonstrates the SharedResources library functionality. This project serves as both documentation and integration test.

The sample must:
1. Create a new Aspire AppHost project
2. Reference the SharedResources library
3. Configure shared resource support via `AddSharedResourceSupport()`
4. Add container resources with `WithSharedResourceMetadata()`
5. Include sample configuration (appsettings.json, user-secrets example)
6. Demonstrate multiple shared resources
7. Include README with usage instructions

## Contract Reference
- PLAN.md: Expected Behaviors, User Interaction Requirements
- docs/contracts/lib-shared-resource-extensions.md: Usage Examples

## Inputs
- TASK-011: Complete SharedResourceExtensions

## Outputs
- `samples/SampleAppHost/SampleAppHost.csproj`
- `samples/SampleAppHost/Program.cs`
- `samples/SampleAppHost/appsettings.json`
- `samples/SampleAppHost/appsettings.Development.json`
- `samples/SampleAppHost/README.md`

## Test Cases
- Test case 1: Project builds successfully
- Test case 2: `dotnet run` starts without configuration errors (may prompt for paths)
- Test case 3: With valid configuration, resources are processed
- Test case 4: Image caching works (second run shows "Image exists")
- Test case 5: Dirty working tree warning is logged when applicable
- Test case 6: Configuration via user-secrets works
- Test case 7: Configuration via environment variables works

## Test Filter
N/A - Integration sample

## Definition of Done
- [ ] Sample project created and builds
- [ ] Demonstrates `AddSharedResourceSupport()` usage
- [ ] Demonstrates `WithSharedResourceMetadata()` for multiple resources
- [ ] Configuration examples in appsettings files
- [ ] User-secrets setup documented
- [ ] README explains how to run the sample
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
cd samples/SampleAppHost && dotnet build
```

## Complexity
M

## Notes
- Sample should work with mock repositories for demonstration
- Include comments explaining each configuration option
- Show both parameter-based and annotation-based usage patterns
- Consider creating a simple test repository structure in the sample
