# runnable_summary

kind: let

source:
```prose
let runnable_summary = session: usability_verifier
  prompt: "Summarize runnable verification..."
```

---

## Summary

| Metric | Count |
|--------|-------|
| **Total Verified** | 15 |
| **Passed** | 14 |
| **Failed** | 1 |
| **Pass Rate** | 93.3% |

---

## By Category

### Tests (3 runnables)

| Runnable | Status |
|----------|--------|
| Build Solution | PASS |
| Run All Tests | PASS |
| Build Warnings Check | PASS |

**Details:**
- Build: 0 errors, 0 warnings
- Tests: 148/148 passed (100%)
- Warnings: 11 nullable reference warnings in test code only (CS8602)

---

### Samples (4 runnables)

| Runnable | Status |
|----------|--------|
| Sample AppHost Exists | PASS |
| Build Succeeds | PASS |
| References SharedResources | PASS |
| Program.cs Structure | PASS |

**Details:**
- All project files present
- Build completed with 0 errors, 0 warnings
- Correctly references SharedResources library
- Demonstrates both API usage patterns

---

### APIs (5 runnables)

| Runnable | Status |
|----------|--------|
| SharedResourceAnnotation | PASS |
| IRepositoryPathResolver | PASS |
| IGitOperations | PASS |
| IContainerImageService | PASS |
| Extension Methods | PASS |

**Details:**
- All expected API contracts exist and compile
- All interfaces have concrete implementations
- Extension methods properly registered in DI

---

### CLI Tools (3 runnables)

| Runnable | Status |
|----------|--------|
| Docker Availability | FAIL |
| Git SHA Retrieval | PASS |
| Git Status Check | PASS |

**Details:**
- Docker client installed (v29.1.3), daemon not responding
- Git commands working correctly

---

## Failed Runnables

### 1. Docker Availability

| Field | Value |
|-------|-------|
| **Category** | CLI Tools |
| **Command** | `docker info` |
| **Exit Code** | 1 |
| **Reason** | Docker daemon not responding (HTTP 500 Internal Server Error) |

**Root Cause:**
The Docker client is installed and configured (v29.1.3 with buildx, compose, scout plugins), but the Docker daemon/server is not running or is in an error state. Docker Desktop appears to be configured (`desktop-linux` context) but the server component is unresponsive.

**Impact:**
- Container image builds will fail
- Sample AppHost runtime execution will fail (requires Docker for container operations)
- Build and test verification unaffected (no Docker dependency)

**Resolution:**
Start Docker Desktop or the Docker daemon service:
```bash
# Option 1: Start Docker Desktop
systemctl --user start docker-desktop

# Option 2: Start Docker daemon directly
sudo systemctl start docker
```

---

## Overall Assessment

The SharedResources library is **functionally complete** with all core components verified:

1. **Build System**: Fully operational (0 errors, 0 warnings in production code)
2. **Test Suite**: All 148 tests passing
3. **API Contracts**: All interfaces and implementations present and correct
4. **Sample Code**: Properly structured and demonstrates API usage

**Blocking Issue**: Docker daemon must be started for full runtime verification of container operations.
