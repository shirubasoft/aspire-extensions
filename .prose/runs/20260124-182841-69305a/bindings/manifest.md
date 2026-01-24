# manifest

kind: let

source:
```prose
let manifest = session "Read {plan_folder}/MANIFEST.md"
  context: plan_folder
```

---

# Task Manifest: Aspire Shared Resources

## Overview

This manifest contains all tasks required to implement the Aspire Shared Resources library for automatic container building via Aspire eventing.

**Total Tasks:** 13
**Parallelizable Tasks:** Up to 5 in Phase 1
**Critical Path Length:** 6 tasks
**Estimated Phases:** 5

---

## Task List

| ID | Title | Complexity | Dependencies | Status |
|----|-------|------------|--------------|--------|
| TASK-001 | Project Setup and Infrastructure | S | none | pending |
| TASK-002 | SharedResourceAnnotation Class | S | TASK-001 | pending |
| TASK-003 | SharedResourceConfiguration Class | S | TASK-001 | pending |
| TASK-004 | SharedResourceOptions Class | S | TASK-001 | pending |
| TASK-005 | Custom Exception Classes | M | TASK-001 | pending |
| TASK-006 | IGitOperations + GitOperations | M | TASK-001, TASK-005 | pending |
| TASK-007 | ContainerImageInfo Class | S | TASK-001 | pending |
| TASK-008 | IContainerImageService + ContainerImageService | L | TASK-001, TASK-005, TASK-007 | pending |
| TASK-009 | IRepositoryPathResolver + RepositoryPathResolver | L | TASK-001, TASK-003, TASK-005 | pending |
| TASK-010 | SharedResourceBuildService | L | TASK-001, TASK-002, TASK-005, TASK-006, TASK-008, TASK-009 | pending |
| TASK-011 | SharedResourceExtensions | M | All TASK-001 through TASK-010 | pending |
| TASK-012 | Sample AppHost Project | M | TASK-011 | pending |
| TASK-013 | Documentation and README | M | TASK-011, TASK-012 | pending |

---

## Dependency Graph

```
                                    TASK-001 (Project Setup)
                                           │
                 ┌─────────────┬───────────┼───────────┬─────────────┐
                 │             │           │           │             │
                 ▼             ▼           ▼           ▼             ▼
            TASK-002      TASK-003    TASK-004    TASK-005      TASK-007
         (Annotation)    (Config)    (Options)  (Exceptions)  (ImageInfo)
                 │             │           │           │             │
                 │             │           │           ├─────────────┤
                 │             │           │           │             │
                 │             │           │           ▼             │
                 │             │           │      TASK-006           │
                 │             │           │    (GitOperations)      │
                 │             │           │           │             │
                 │             └───────────┼───────────┼─────────────┤
                 │                         │           │             │
                 │                         ▼           │             ▼
                 │                    TASK-009         │        TASK-008
                 │               (PathResolver)        │   (ContainerService)
                 │                         │           │             │
                 │                         └───────────┼─────────────┘
                 │                                     │
                 └─────────────────────────────────────┤
                                                       │
                                                       ▼
                                                  TASK-010
                                            (BuildService)
                                                       │
                                                       ▼
                                                  TASK-011
                                              (Extensions)
                                                       │
                                                       ▼
                                                  TASK-012
                                             (Sample AppHost)
                                                       │
                                                       ▼
                                                  TASK-013
                                             (Documentation)
```

---

## Critical Path

The longest dependency chain that determines minimum completion time:

```
TASK-001 → TASK-005 → TASK-006 → TASK-010 → TASK-011 → TASK-012 → TASK-013
   │           │           │          │           │           │
   S           M           M          L           M           M
```

**Critical Path Length:** 6 tasks (excluding TASK-013 which can be parallelized with TASK-012)

---

## Task Categories

### Infrastructure (1 task)
- TASK-001: Project Setup and Infrastructure

### Data Models (4 tasks)
- TASK-002: SharedResourceAnnotation Class
- TASK-003: SharedResourceConfiguration Class
- TASK-004: SharedResourceOptions Class
- TASK-007: ContainerImageInfo Class

### Exceptions (1 task)
- TASK-005: Custom Exception Classes

### Services (4 tasks)
- TASK-006: IGitOperations + GitOperations
- TASK-008: IContainerImageService + ContainerImageService
- TASK-009: IRepositoryPathResolver + RepositoryPathResolver
- TASK-010: SharedResourceBuildService

### Integration (1 task)
- TASK-011: SharedResourceExtensions

### Samples & Documentation (2 tasks)
- TASK-012: Sample AppHost Project
- TASK-013: Documentation and README

---

## Complexity Distribution

| Complexity | Count | Tasks |
|------------|-------|-------|
| Small (S) | 5 | TASK-001, TASK-002, TASK-003, TASK-004, TASK-007 |
| Medium (M) | 5 | TASK-005, TASK-006, TASK-011, TASK-012, TASK-013 |
| Large (L) | 3 | TASK-008, TASK-009, TASK-010 |

---

## Contract References

| Task | Contract File |
|------|---------------|
| TASK-002 | docs/contracts/lib-shared-resource-annotation.md |
| TASK-003 | docs/contracts/lib-shared-resource-annotation.md |
| TASK-004 | docs/contracts/lib-shared-resource-annotation.md |
| TASK-005 | Multiple contracts (exceptions defined in each) |
| TASK-006 | docs/contracts/lib-git-operations.md |
| TASK-007 | docs/contracts/lib-container-image-service.md |
| TASK-008 | docs/contracts/lib-container-image-service.md |
| TASK-009 | docs/contracts/lib-repository-path-resolver.md |
| TASK-010 | docs/contracts/lib-shared-resource-build-service.md |
| TASK-011 | docs/contracts/lib-shared-resource-extensions.md |

---

## Notes

1. **Tests are Part of Each Task**: Each implementation task includes writing and running tests. There are no separate test tasks.

2. **Parallel Development**: After TASK-001 completes, up to 5 tasks can be developed in parallel (TASK-002, TASK-003, TASK-004, TASK-005, TASK-007).

3. **Service Dependencies**: The three main services (GitOperations, ContainerImageService, RepositoryPathResolver) can be developed in parallel once their dependencies are met.

4. **Integration Point**: TASK-010 (SharedResourceBuildService) is the main integration point where all services come together.

5. **Final Integration**: TASK-011 (Extensions) ties everything together for public API consumption.
