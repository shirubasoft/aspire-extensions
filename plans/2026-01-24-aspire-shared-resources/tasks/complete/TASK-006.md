# TASK-006: IGitOperations Interface and GitOperations Implementation

## Status
pending

## Dependencies
- TASK-001
- TASK-005

## Description
Implement the `IGitOperations` interface and `GitOperations` class for git operations needed to determine image tags based on repository commit state.

The implementation must:
1. Use CliWrap to execute git CLI commands
2. Implement `GetCurrentCommitShaAsync()` - returns 7-char short SHA
3. Implement `GetCurrentBranchAsync()` - returns branch name or "HEAD" if detached
4. Implement `HasUncommittedChangesAsync()` - checks for dirty working tree
5. Implement `IsGitRepositoryAsync()` - validates path is a git repo
6. Include proper error handling with `GitOperationException`
7. Include cancellation token support
8. Include logging at appropriate levels

## Contract Reference
- docs/contracts/lib-git-operations.md

## Inputs
- TASK-001: Project structure with CliWrap reference
- TASK-005: GitOperationException class

## Outputs
- `src/SharedResources/Services/IGitOperations.cs`
- `src/SharedResources/Services/GitOperations.cs`
- Unit tests with mocked git behavior
- Integration tests with actual git repos (using test fixtures)

## Test Cases
- Test case 1: `GetCurrentCommitShaAsync` returns 7-character SHA
- Test case 2: `GetCurrentBranchAsync` returns branch name on normal branch
- Test case 3: `GetCurrentBranchAsync` returns "HEAD" in detached HEAD state
- Test case 4: `HasUncommittedChangesAsync` returns true when dirty
- Test case 5: `HasUncommittedChangesAsync` returns false when clean
- Test case 6: `IsGitRepositoryAsync` returns true for git repo
- Test case 7: `IsGitRepositoryAsync` returns false for non-git directory
- Test case 8: Throws `GitOperationException` when git not installed
- Test case 9: Throws `GitOperationException` for invalid repository path
- Test case 10: Operations respect cancellation token

## Test Filter
GitOperationsTests

## Definition of Done
- [ ] Interface defined with all methods per contract
- [ ] Implementation uses CliWrap for git CLI execution
- [ ] 7-character SHA returned from `GetCurrentCommitShaAsync`
- [ ] Detached HEAD handled correctly
- [ ] Working tree status detection works
- [ ] Git repository validation works
- [ ] Error handling with meaningful exceptions
- [ ] Cancellation token respected
- [ ] Logging at Debug level for operations
- [ ] All test cases pass with zero failures
- [ ] Verification command runs without errors

## Verification Command
```bash
dotnet test --filter "GitOperationsTests"
```

## Complexity
M

## Notes
- Git commands used:
  - `git rev-parse --short=7 HEAD`
  - `git symbolic-ref --short HEAD`
  - `git status --porcelain`
  - `git rev-parse --git-dir`
- Default timeout of 30 seconds per command
- Handle Win32Exception (error code 2) for git not found
