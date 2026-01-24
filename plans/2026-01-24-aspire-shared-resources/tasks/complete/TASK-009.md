# TASK-009: IRepositoryPathResolver Interface and RepositoryPathResolver Implementation

## Status
pending

## Dependencies
- TASK-001
- TASK-003
- TASK-005

## Description
Implement the `IRepositoryPathResolver` interface and `RepositoryPathResolver` class for resolving local filesystem paths to external repositories based on configuration, environment variables, or user interaction.

The implementation must:
1. Implement `ResolveRepositoryPathAsync()` with priority-based resolution
2. Implement `IsRepositoryAvailableAsync()` for pre-check without prompting
3. Implement `SaveRepositoryPathAsync()` to persist paths to user secrets
4. Support resolution priority: explicit config > env var > base path > user prompt
5. Extract repository name from GitHubRepository (org/repo -> repo)
6. Use `IInteractionService` for user prompts
7. Use `IConfiguration` for reading config values
8. Use `IOptions<SharedResourceConfiguration>` for typed config
9. Include proper error handling with `RepositoryNotFoundException`

## Contract Reference
- docs/contracts/lib-repository-path-resolver.md

## Inputs
- TASK-001: Project structure
- TASK-003: SharedResourceConfiguration
- TASK-005: RepositoryNotFoundException

## Outputs
- `src/SharedResources/Services/IRepositoryPathResolver.cs`
- `src/SharedResources/Services/RepositoryPathResolver.cs`
- Unit tests with mocked dependencies

## Test Cases
- Test case 1: Resolves path from explicit `RepositoryPaths[serviceName]` config
- Test case 2: Resolves path from environment variable
- Test case 3: Resolves path from `RepositoriesBasePath` + repo name derivation
- Test case 4: Prompts user when no config and `PromptForMissingPaths` is true
- Test case 5: Throws `RepositoryNotFoundException` when no config and prompting disabled
- Test case 6: Validates resolved path exists (throws if not)
- Test case 7: `IsRepositoryAvailableAsync` returns true when path configured
- Test case 8: `IsRepositoryAvailableAsync` returns false when path not available
- Test case 9: `SaveRepositoryPathAsync` persists to user secrets
- Test case 10: Extracts repo name correctly from "org/repo" format
- Test case 11: User prompt saves path when user confirms

## Test Filter
RepositoryPathResolverTests

## Definition of Done
- [ ] Interface defined with all methods per contract
- [ ] Resolution priority correctly implemented
- [ ] Environment variable format: `SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}`
- [ ] Base path derivation extracts repo name from `org/repo`
- [ ] User prompting via `IInteractionService`
- [ ] Path validation (directory must exist)
- [ ] User secrets persistence via CLI
- [ ] Logging at appropriate levels
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "RepositoryPathResolverTests"
```

## Complexity
L

## Notes
- Config key: `SharedResources:RepositoryPaths:{ServiceName}`
- Env var: `SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME.ToUpperInvariant()}`
- Use `Path.GetFullPath()` to normalize paths
- User secrets persistence uses `dotnet user-secrets set` via CliWrap
- `IInteractionService` is from Aspire.Hosting
