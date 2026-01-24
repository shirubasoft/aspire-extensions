# ready_tasks_iter2

kind: let

source:
```prose
let ready_tasks = session: task_coordinator
  prompt: "Identify ready tasks iteration 2..."
```

---

## Ready Tasks (Dependencies Met)

### TASK-002: SharedResourceAnnotation Class
- **File path:** `/home/danielreis/code/aspire-extensions/plans/2026-01-24-aspire-shared-resources/tasks/TASK-002.md`
- **Component:** SharedResourceAnnotation
- **Description:** Implement the `SharedResourceAnnotation` class that marks container resources as coming from external repositories. Contains metadata for locating source code and building container images.
- **Dependencies:** TASK-001 (completed)
- **Test filter:** SharedResourceAnnotationTests
- **Verification command:** `dotnet test --filter "SharedResourceAnnotationTests"`

### TASK-003: SharedResourceConfiguration Class
- **File path:** `/home/danielreis/code/aspire-extensions/plans/2026-01-24-aspire-shared-resources/tasks/TASK-003.md`
- **Component:** SharedResourceConfiguration
- **Description:** Implement the `SharedResourceConfiguration` class for binding shared resource settings from IConfiguration. Defines repository paths and behavior configuration via user-secrets, environment variables, or appsettings.
- **Dependencies:** TASK-001 (completed)
- **Test filter:** SharedResourceConfigurationTests
- **Verification command:** `dotnet test --filter "SharedResourceConfigurationTests"`

### TASK-004: SharedResourceOptions Class
- **File path:** `/home/danielreis/code/aspire-extensions/plans/2026-01-24-aspire-shared-resources/tasks/TASK-004.md`
- **Component:** SharedResourceOptions
- **Description:** Implement the `SharedResourceOptions` class used with the `WithSharedResourceMetadata` extension method to provide optional configuration in a fluent manner.
- **Dependencies:** TASK-001 (completed)
- **Test filter:** SharedResourceOptionsTests
- **Verification command:** `dotnet test --filter "SharedResourceOptionsTests"`

### TASK-005: Custom Exception Classes
- **File path:** `/home/danielreis/code/aspire-extensions/plans/2026-01-24-aspire-shared-resources/tasks/TASK-005.md`
- **Component:** Exceptions (RepositoryNotFoundException, GitOperationException, ContainerOperationException, ContainerBuildException, SharedResourceBuildException, SharedResourceError)
- **Description:** Implement all custom exception classes needed by the library including factory methods, GetDetailedMessage() methods, and serialization support.
- **Dependencies:** TASK-001 (completed)
- **Test filter:** *ExceptionTests
- **Verification command:** `dotnet test --filter "ExceptionTests"`

### TASK-007: ContainerImageInfo Class
- **File path:** `/home/danielreis/code/aspire-extensions/plans/2026-01-24-aspire-shared-resources/tasks/TASK-007.md`
- **Component:** ContainerImageInfo
- **Description:** Implement the `ContainerImageInfo` class that holds information about a container image. A simple data class with Id, FullName, Created, Size properties and FormattedSize computed property.
- **Dependencies:** TASK-001 (completed)
- **Test filter:** ContainerImageInfoTests
- **Verification command:** `dotnet test --filter "ContainerImageInfoTests"`

---

## Blocked Tasks

| Task ID | Missing Dependencies |
|---------|---------------------|
| TASK-006 | TASK-005 |
| TASK-008 | TASK-005, TASK-007 |
| TASK-009 | TASK-003, TASK-005 |
| TASK-010 | TASK-002, TASK-005, TASK-006, TASK-008, TASK-009 |
| TASK-011 | TASK-002, TASK-003, TASK-004, TASK-006, TASK-008, TASK-009, TASK-010 |
| TASK-012 | TASK-011 |
| TASK-013 | TASK-011, TASK-012 |

---

## Summary

- **Ready tasks:** 5 (TASK-002, TASK-003, TASK-004, TASK-005, TASK-007)
- **Blocked tasks:** 7
- **Completed tasks:** 1 (TASK-001)
