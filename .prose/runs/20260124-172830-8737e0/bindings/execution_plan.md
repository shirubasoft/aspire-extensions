# execution_plan

kind: let

source:
```prose
let execution_plan = session "Read {plan_folder}/EXECUTION-PLAN.md. Report if missing."
```

---

# Execution Plan: Aspire Shared Resources

This document outlines the optimal execution order for implementing the Aspire Shared Resources library, maximizing parallelism while respecting dependencies.

---

## Phase 0: Foundation (no dependencies)

**Tasks that can start immediately:**

| Task | Title | Complexity | Estimated Time |
|------|-------|------------|----------------|
| TASK-001 | Project Setup and Infrastructure | S | 1-2 hours |

**Objective:** Create the project structure, configure packages, and establish the test framework.

**Exit Criteria:**
- `dotnet build` succeeds
- `dotnet test` executes (even with zero tests)
- Directory structure created

---

## Phase 1: Data Models and Exceptions (after Phase 0)

**Tasks unlocked when TASK-001 completes:**

| Task | Title | Complexity | Can Parallel |
|------|-------|------------|--------------|
| TASK-002 | SharedResourceAnnotation Class | S | Yes |
| TASK-003 | SharedResourceConfiguration Class | S | Yes |
| TASK-004 | SharedResourceOptions Class | S | Yes |
| TASK-005 | Custom Exception Classes | M | Yes |
| TASK-007 | ContainerImageInfo Class | S | Yes |

**Maximum Parallelism:** 5 tasks

**Objective:** Implement all data models and exception classes that other components depend on.

**Exit Criteria:**
- All data model classes implemented with tests
- All exception classes implemented with tests
- No compilation errors

---

## Phase 2: Core Services (after Phase 1)

**Tasks unlocked when Phase 1 completes:**

| Task | Title | Complexity | Blocks | Can Parallel |
|------|-------|------------|--------|--------------|
| TASK-006 | IGitOperations + GitOperations | M | TASK-005 | Yes |
| TASK-008 | IContainerImageService + ContainerImageService | L | TASK-005, TASK-007 | Yes |
| TASK-009 | IRepositoryPathResolver + RepositoryPathResolver | L | TASK-003, TASK-005 | Yes |

**Maximum Parallelism:** 3 tasks

**Objective:** Implement the three core service interfaces and their implementations.

**Exit Criteria:**
- Git operations work with real git repositories
- Container operations work with Docker daemon
- Path resolution works with configuration and user prompts
- All unit tests pass

---

## Phase 3: Integration Service (after Phase 2)

**Tasks unlocked when Phase 2 completes:**

| Task | Title | Complexity | Blocks |
|------|-------|------------|--------|
| TASK-010 | SharedResourceBuildService | L | TASK-002, TASK-006, TASK-008, TASK-009 |

**Maximum Parallelism:** 1 task

**Objective:** Implement the orchestration service that ties all components together.

**Exit Criteria:**
- BeforeStartEvent processing works
- Resource discovery works
- Full build pipeline executes
- Error aggregation works
- All unit tests pass

---

## Phase 4: Public API (after Phase 3)

**Tasks unlocked when TASK-010 completes:**

| Task | Title | Complexity | Blocks |
|------|-------|------------|--------|
| TASK-011 | SharedResourceExtensions | M | All prior tasks |

**Maximum Parallelism:** 1 task

**Objective:** Implement the public extension methods for library consumers.

**Exit Criteria:**
- `AddSharedResourceSupport()` registers all services
- `WithSharedResourceMetadata()` configures resources correctly
- Input validation works
- All unit tests pass

---

## Phase 5: Sample and Documentation (after Phase 4)

**Tasks unlocked when TASK-011 completes:**

| Task | Title | Complexity | Can Parallel |
|------|-------|------------|--------------|
| TASK-012 | Sample AppHost Project | M | Yes |
| TASK-013 | Documentation and README | M | Yes |

**Maximum Parallelism:** 2 tasks

**Objective:** Create sample application and comprehensive documentation.

**Exit Criteria:**
- Sample AppHost builds and demonstrates functionality
- README provides quick start guide
- All configuration options documented
- Troubleshooting guide covers common issues

---

## Execution Timeline

```
Phase 0     | TASK-001
            | --------
            |
Phase 1     | TASK-002  TASK-003  TASK-004  TASK-005  TASK-007
            | --------  --------  --------  --------  --------
            |
Phase 2     | TASK-006  TASK-008  TASK-009
            | --------  ------------------  ------------------
            |
Phase 3     | TASK-010
            | ------------------
            |
Phase 4     | TASK-011
            | --------
            |
Phase 5     | TASK-012  TASK-013
            | --------  --------
            |
Time -------+------------------------------------------------------►
```

---

## Critical Path Analysis

The critical path determines the minimum time to complete all tasks:

```
TASK-001 → TASK-005 → TASK-006 → TASK-010 → TASK-011 → TASK-012
                                                    └→ TASK-013 (parallel)
```

**Critical Path Tasks:** 6
**Minimum Phases Required:** 5

---

## Parallelism Summary

| Phase | Tasks | Max Parallel | Sequential Bottleneck |
|-------|-------|--------------|----------------------|
| 0 | 1 | 1 | Yes (foundation) |
| 1 | 5 | 5 | No |
| 2 | 3 | 3 | No |
| 3 | 1 | 1 | Yes (integration) |
| 4 | 1 | 1 | Yes (public API) |
| 5 | 2 | 2 | No |

**Total Tasks:** 13
**Average Parallelism:** 2.2 tasks per phase
**Theoretical Speedup:** 2.2x vs fully sequential

---

## Recommended Execution Strategy

### For Single Developer
Execute phases sequentially. Within each phase, complete tasks in order of dependency:
1. Phase 1: TASK-005 first (other tasks depend on exceptions)
2. Phase 2: Start with TASK-006 (fewer dependencies)

### For Team (2-3 Developers)
- **Developer A:** TASK-001 → TASK-005 → TASK-006 → TASK-010 (critical path)
- **Developer B:** TASK-002, TASK-003, TASK-004 → TASK-009 → TASK-011
- **Developer C:** TASK-007 → TASK-008 → TASK-012 → TASK-013

### For Larger Team (4+ Developers)
- Maximize Phase 1 parallelism (5 tasks)
- Assign service tasks in Phase 2 to separate developers
- Coordinate integration in Phases 3-4

---

## Risk Areas

1. **TASK-008 (ContainerImageService):** Largest task, Docker dependency
2. **TASK-009 (RepositoryPathResolver):** Complex configuration logic
3. **TASK-010 (BuildService):** Integration complexity, depends on 6 tasks

**Mitigation:** Start critical path tasks early, add buffer time for Large tasks

---

## Progress Tracking

Use this checklist to track completion:

- [ ] Phase 0: TASK-001
- [ ] Phase 1: TASK-002, TASK-003, TASK-004, TASK-005, TASK-007
- [ ] Phase 2: TASK-006, TASK-008, TASK-009
- [ ] Phase 3: TASK-010
- [ ] Phase 4: TASK-011
- [ ] Phase 5: TASK-012, TASK-013

---

## Definition of Done Reference

See `DEFINITION-OF-DONE.md` for:
- Component-level completion criteria
- Feature-level completion criteria
- Project-level completion criteria
- Runnable verification commands
