# runnable_cli_tools

kind: let

source:
```prose
let runnable_cli_tools = do verify-runnable("cli-tools", ...)
```

---

## Verification Results

### 1. Docker Availability

| Field | Value |
|-------|-------|
| **Status** | FAIL |
| **Command run** | `docker info` |
| **Exit code** | 1 |
| **Actual output** | Client information returned successfully (Version 29.1.3, multiple plugins detected). Server returned: `ERROR: request returned 500 Internal Server Error for API route and version http://%2Fhome%2Fdanielreis%2F.docker%2Fdesktop%2Fdocker.sock/v1.52/info, check if the server supports the requested API version` |
| **Match with expected** | PARTIAL |
| **Notes** | Docker client is installed and configured (v29.1.3 with plugins including buildx, compose, scout, etc.). However, the Docker daemon/server is not responding correctly - returning HTTP 500 errors. Docker Desktop appears to be configured (`desktop-linux` context) but the server component is not running or is in an error state. The Docker socket exists but the daemon is unresponsive. |

**Additional verification with `docker ps`:**
- Exit code: 1
- Output: `request returned 500 Internal Server Error for API route and version http://%2Fhome%2Fdanielreis%2F.docker%2Fdesktop%2Fdocker.sock/v1.52/containers/json`

---

### 2. Git SHA Retrieval

| Field | Value |
|-------|-------|
| **Status** | PASS |
| **Command run** | `git rev-parse --short=7 HEAD` |
| **Exit code** | 0 |
| **Actual output** | `bc3f95e` |
| **Match with expected** | YES |
| **Notes** | Successfully returned a 7-character SHA hash. The SHA `bc3f95e` matches the most recent commit shown in git log ("Replace AGENTS.md with CLAUDE.md"). |

---

### 3. Git Status Check

| Field | Value |
|-------|-------|
| **Status** | PASS |
| **Command run** | `git status --porcelain` |
| **Exit code** | 0 |
| **Actual output** | `?? .prose/runs/20260124-182841-69305a/` |
| **Match with expected** | YES |
| **Notes** | Command executed successfully. Output shows one untracked directory (`.prose/runs/20260124-182841-69305a/`) marked with `??`. This is expected behavior - the current prose run directory is being created as part of this verification process. The working tree is otherwise clean (no modified tracked files). |

---

## Summary

| Tool | Status | Notes |
|------|--------|-------|
| Docker | FAIL | Docker client installed (v29.1.3), but daemon not responding (HTTP 500). Docker Desktop may need to be started. |
| Git SHA | PASS | Returns valid 7-char SHA: `bc3f95e` |
| Git Status | PASS | Returns correct porcelain format output |

**Overall Assessment:** 2 of 3 CLI tools are fully operational. Docker requires daemon to be started before container operations can proceed.
