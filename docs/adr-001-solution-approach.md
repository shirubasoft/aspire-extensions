# ADR-001: Solution Approach for Aspire Extensions

**Status**: Accepted
**Date**: 2026-01-24
**Decision Makers**: Technical Team
**Context**: Initial architecture decisions for aspire-extensions project

## Context

During the initial planning phase, we evaluated multiple approaches for implementing Aspire extensions that support external repository integration with Docker containerization. The evaluation focused on lifecycle management, resource handling, subprocess execution, and testing frameworks to ensure the solution is maintainable, consistent, and aligned with modern Aspire patterns.

## Alternatives Considered

### 1. Lifecycle Hook Mechanism

**Option A: IDistributedApplicationLifecycleHook (Rejected)**
- Legacy interface from earlier Aspire versions
- Marked as obsolete in current Aspire releases
- Would require maintenance burden as Aspire evolves

**Option B: Subscribe<BeforeStartEvent> (Selected)**
- Modern event-based lifecycle management
- Aligned with current Aspire architecture patterns
- Better integration with Aspire's eventing infrastructure

### 2. Docker Resource Management

**Option A: Aspire Built-in AddDockerfile (Rejected)**
- Available as standard Aspire functionality
- Limited customization for external repository workflows
- Does not support SHA-based tagging for external repos
- Insufficient for managing cloned external repositories

**Option B: Custom SharedResource Approach (Selected)**
- Provides fine-grained control over external repository workflows
- Supports SHA-based Docker image tagging for reproducibility
- Enables custom build workflows for repositories cloned from external sources
- Better suited for managing git clone, build, and containerization pipeline

### 3. Subprocess and Git Operations

**Option A: LibGit2Sharp (Rejected)**
- Native Git operations through library bindings
- Additional dependency with native interop complexity
- Potential platform compatibility concerns

**Option B: Docker.DotNet (Rejected)**
- Direct Docker API integration
- Adds complexity for simple Docker CLI operations
- Requires managing API client lifecycle

**Option C: CliWrap (Selected)**
- Consistent approach for all subprocess operations (git, docker)
- Well-maintained library with robust process management
- Simplifies testing and mocking of external commands
- Unified error handling across git and docker operations

### 4. Testing Framework

**Option A: xUnit/NUnit (Rejected)**
- Traditional testing frameworks
- Adequate but less modern feature set

**Option B: TUnit 1.12.43 (Selected)**
- Modern testing framework with improved capabilities
- Better async support and test isolation
- Enhanced diagnostic features

## Decision

We will implement the aspire-extensions project using:

1. **Subscribe<BeforeStartEvent>** for lifecycle management
2. **Custom SharedResource approach** for Docker resource handling with external repositories
3. **CliWrap** for all subprocess execution (git clone, docker build/tag operations)
4. **TUnit 1.12.43** as the testing framework

## Consequences

### Positive

- **Modern Architecture**: Using current Aspire patterns ensures long-term compatibility and reduces technical debt
- **Workflow Flexibility**: Custom SharedResource approach enables sophisticated external repository workflows with SHA tagging
- **Consistency**: CliWrap provides unified subprocess handling for git and docker operations
- **Maintainability**: Single subprocess library reduces complexity and testing surface area
- **Future-Proof**: Alignment with modern Aspire patterns minimizes breaking changes in future updates

### Negative

- **Custom Code**: SharedResource approach requires more implementation code than using built-in AddDockerfile
- **Subprocess Dependency**: Reliance on external git and docker CLI tools instead of native libraries
- **Error Handling**: CLI-based approach requires robust parsing of command output for error scenarios

### Risks and Mitigation

- **Risk**: External CLI tools (git, docker) must be present on host system
  - **Mitigation**: Clear documentation of prerequisites and validation checks during startup

- **Risk**: Custom SharedResource code increases maintenance burden
  - **Mitigation**: Comprehensive test coverage and clear separation of concerns

- **Risk**: TUnit adoption may have smaller community than xUnit
  - **Mitigation**: TUnit is actively maintained and provides migration path if needed

## Notes

This ADR establishes the foundational technical approach for the aspire-extensions project. Future ADRs may address specific implementation patterns, security considerations, or additional extension types as the project evolves.
