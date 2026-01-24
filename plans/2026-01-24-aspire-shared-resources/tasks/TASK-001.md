# TASK-001: Project Setup and Infrastructure

## Status
pending

## Dependencies
- none

## Description
Create the project structure for the SharedResources library including:
1. Create the SharedResources.csproj project targeting .NET 10
2. Configure project for NuGet packaging
3. Add package references (Aspire.Hosting 13.1.0, CliWrap 3.10.0)
4. Create directory structure (Annotations/, Configuration/, Extensions/, Services/, Eventing/, Exceptions/)
5. Configure nullable reference types and implicit usings
6. Create test project SharedResources.Tests with TUnit 1.12.43
7. Configure solution file to include both projects

## Contract Reference
- PLAN.md: Dependencies section and Files to Create section

## Inputs
- None (first task)

## Outputs
- `src/SharedResources/SharedResources.csproj` - Main library project
- `tests/SharedResources.Tests/SharedResources.Tests.csproj` - Test project
- Directory structure for source files
- Solution file updated

## Test Cases
- Test case 1: `dotnet build` succeeds with no errors or warnings
- Test case 2: `dotnet test` runs (even with zero tests initially)
- Test case 3: All package references resolve correctly
- Test case 4: Nullable reference types are enabled

## Test Filter
N/A - Infrastructure task

## Definition of Done
- [ ] SharedResources.csproj created with correct target framework (.NET 10)
- [ ] Package references added: Aspire.Hosting 13.1.0, CliWrap 3.10.0
- [ ] Test project created with TUnit 1.12.43
- [ ] Directory structure created per plan
- [ ] `dotnet build` succeeds with no warnings
- [ ] `dotnet test` executes successfully

## Verification Command
```bash
dotnet build && dotnet test
```

## Complexity
S

## Notes
- Ensure the namespace is `Aspire.Hosting.SharedResources`
- Project should be configured for library packaging
- Use latest C# language features (nullable, file-scoped namespaces)
