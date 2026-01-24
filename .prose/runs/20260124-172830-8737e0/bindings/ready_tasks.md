# ready_tasks

kind: let

source:
```prose
let ready_tasks = session: task_coordinator
  prompt: "Read {plan_folder}/tasks/ and identify ready tasks..."
```

---

## Summary

- **Total tasks:** 13
- **Completed tasks:** 0
- **Ready tasks:** 1
- **Blocked tasks:** 12 (waiting for TASK-001 to complete)

---

## Ready Tasks

### TASK-001: Project Setup and Infrastructure

- **Task ID:** TASK-001
- **File path:** `/home/danielreis/code/aspire-extensions/plans/2026-01-24-aspire-shared-resources/tasks/TASK-001.md`
- **Component name:** Project Infrastructure
- **Complexity:** S (Small)

#### Description

Create the project structure for the SharedResources library including:
1. Create the SharedResources.csproj project targeting .NET 10
2. Configure project for NuGet packaging
3. Add package references (Aspire.Hosting 13.1.0, CliWrap 3.10.0)
4. Create directory structure (Annotations/, Configuration/, Extensions/, Services/, Eventing/, Exceptions/)
5. Configure nullable reference types and implicit usings
6. Create test project SharedResources.Tests with TUnit 1.12.43
7. Configure solution file to include both projects

#### Test Cases

- Test case 1: `dotnet build` succeeds with no errors or warnings
- Test case 2: `dotnet test` runs (even with zero tests initially)
- Test case 3: All package references resolve correctly
- Test case 4: Nullable reference types are enabled

#### Test Filter

N/A - Infrastructure task

#### Verification Command

```bash
dotnet build && dotnet test
```

---

## Dependency Graph

```
TASK-001 (READY)
├── TASK-002 (blocked by TASK-001)
├── TASK-003 (blocked by TASK-001)
├── TASK-004 (blocked by TASK-001)
├── TASK-005 (blocked by TASK-001)
├── TASK-006 (blocked by TASK-001, TASK-005)
├── TASK-007 (blocked by TASK-001)
├── TASK-008 (blocked by TASK-001, TASK-005, TASK-007)
├── TASK-009 (blocked by TASK-001, TASK-003, TASK-005)
├── TASK-010 (blocked by TASK-001, TASK-002, TASK-005, TASK-006, TASK-008, TASK-009)
├── TASK-011 (blocked by TASK-001, TASK-002, TASK-003, TASK-004, TASK-006, TASK-008, TASK-009, TASK-010)
├── TASK-012 (blocked by TASK-011)
└── TASK-013 (blocked by TASK-011, TASK-012)
```

## Next Wave (after TASK-001 completes)

The following tasks will become ready once TASK-001 is completed:
- TASK-002: SharedResourceAnnotation Class
- TASK-003: SharedResourceConfiguration Class
- TASK-004: SharedResourceOptions Class
- TASK-005: Custom Exception Classes
- TASK-007: ContainerImageInfo Class

These 5 tasks can be executed in parallel.
