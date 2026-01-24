# library_status

kind: let

source:
```prose
let library_status = do identify-missing-libraries()
```

---

## Analysis Summary

**libraries_specified:** PARTIAL
**research_required:** YES

## Specified Libraries

The PLAN.md explicitly specifies:
- .NET 10
- Aspire 13.1.0
- TUnit (testing framework - no version specified)

## Libraries Needed But Not Specified

### 1. Git Operations
**Capability:** Get current commit SHA, get current branch name, detect dirty working tree
**Options to research:**
- **LibGit2Sharp** - Native .NET library for Git operations (no external dependencies)
- **CLI shelling** - Execute `git` commands via Process (requires git installed)

**Recommendation:** Research LibGit2Sharp for a cleaner implementation without external CLI dependency.

### 2. Docker/Container Operations
**Capability:** Check if image exists locally (`docker image inspect`), execute build commands
**Options to research:**
- **Docker.DotNet** - Official Docker SDK for .NET
- **CLI shelling** - Execute `docker` commands via Process

**Recommendation:** Research Docker.DotNet for image inspection. Build commands may still need CLI execution since they involve `dotnet publish` not just Docker API calls.

### 3. Testing Framework Version
**Capability:** Unit and integration testing
**Issue:** TUnit is mentioned but no version is specified

**Recommendation:** Research latest TUnit version compatible with .NET 10.

### 4. Mocking Library (Optional)
**Capability:** Mocking dependencies in unit tests
**Options:**
- NSubstitute
- Moq
- FakeItEasy

**Recommendation:** Research which mocking library works best with TUnit.

## Capabilities Analysis

| Capability | Library Needed | Specified | Notes |
|------------|---------------|-----------|-------|
| Aspire Hosting | Aspire.Hosting 13.1.0 | YES | Core dependency |
| Git operations | LibGit2Sharp OR CLI | NO | Need to get commit SHA, branch |
| Docker image check | Docker.DotNet OR CLI | NO | Check if image exists locally |
| Build execution | Process/CLI | IMPLICIT | Via System.Diagnostics.Process |
| Configuration | Microsoft.Extensions.* | IMPLICIT | Comes with Aspire |
| User Secrets | Microsoft.Extensions.* | IMPLICIT | Comes with Aspire |
| Testing | TUnit | PARTIAL | No version specified |
| Mocking | Not specified | NO | Needed for unit tests |

## Research Tasks

1. **LibGit2Sharp** - Verify compatibility with .NET 10, check latest stable version
2. **Docker.DotNet** - Verify compatibility with .NET 10, check if it supports image inspection
3. **TUnit** - Find latest stable version for .NET 10
4. **Mocking library** - Determine best option for TUnit compatibility

## Decision Points

The plan shows CLI commands in the expected behaviors section:
- `git rev-parse --short HEAD`
- `docker image inspect {imageName}:{tag}`

This suggests the original design may lean toward CLI shelling. However, using native libraries would:
- Remove external tool dependencies
- Provide better error handling
- Enable easier unit testing (mockable interfaces)

**Research should determine:** Should we use native libraries or CLI shelling for Git and Docker operations?
