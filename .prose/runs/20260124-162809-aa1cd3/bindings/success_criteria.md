# success_criteria

kind: let

source:
```prose
let success_criteria = do validate-plan-section("Success Criteria", ["testable conditions", "acceptance criteria"])
```

---

## Validation Results

### Field: Testable Conditions
**Status: AMBIGUOUS**

The plan lacks an explicit "Success Criteria" or "Testable Conditions" section. The closest equivalents are:

1. **"Expectations" section** - High-level goals but not testable:
   - "Library with CI/CD and ability to publish nuget package locally" - What constitutes "ability"? What CI/CD provider?
   - "Sample AppHost demonstrating usage" - What should the sample demonstrate specifically?
   - "Comprehensive documentation" - What makes documentation "comprehensive"?

2. **"Expected Behaviors" section** - Describes flow but lacks measurable criteria

**Questions that MUST be answered:**
- Q1: What specific tests must pass to consider the library complete?
- Q2: What is the minimum test coverage requirement (if any)?
- Q3: What constitutes a "working" sample AppHost? (e.g., must it start containers? run specific endpoints?)
- Q4: What CI/CD provider should be configured (GitHub Actions, Azure DevOps, etc.)?
- Q5: What does "publish nuget package locally" mean - to a local folder, local NuGet feed, or both?
- Q6: How do we measure "comprehensive documentation"? What topics must be covered?

---

### Field: Acceptance Criteria
**Status: AMBIGUOUS**

The plan has implicit acceptance criteria scattered across multiple sections but no formal acceptance criteria list.

**From "User Interaction Requirements" (implicit acceptance criteria):**
- First run should prompt for repository path
- Subsequent runs should use cached configuration
- Non-interactive mode should fail with helpful error messages

**From "Expected Behaviors" (implicit acceptance criteria):**
- Build subscriber must run before containers start
- Image tagging must use commit SHA (7 characters)
- Configuration resolution must follow priority order (user-secrets > env vars > base path > prompt)

**From "Edge Cases and Error Handling" (implicit acceptance criteria):**
- Must handle 8 specific edge cases with defined behaviors

**Questions that MUST be answered:**
- Q7: Should all 8 edge cases be tested, or are some optional?
- Q8: What happens when `PromptForMissingPaths = true` but stdin is not available (e.g., CI/CD)?
- Q9: What is the expected behavior for dirty working tree? The plan says "Log warning" but also shows only clean state examples in the Image Tagging Strategy table.
- Q10: Should dirty working tree append a suffix to the tag (e.g., `abc1234-dirty`)? The table only shows clean state.
- Q11: What constitutes "build command fails"? Non-zero exit code? Missing image after build?
- Q12: How long should the system wait for a build before timing out?
- Q13: What is "concurrent builds" scope - per-repository or per-image?
- Q14: Is the "lock per-repository" a file lock, in-memory lock, or distributed lock?

---

### Additional Missing Information

**API Contract Ambiguities:**

- Q15: The `ImageName` in `SharedResourceOptions` is non-nullable but `ImageName` in `SharedResourceAnnotation` is nullable. Which is correct?
- Q16: What happens if `ProjectPath` is null but `{ProjectPath}` placeholder is used in the build command?
- Q17: Should `IRepositoryPathResolver.ResolveRepositoryPathAsync` validate that the path is actually a git repository?
- Q18: What format should `GetCurrentCommitShaAsync` return - with or without newline? Trimmed?

**Build Process Ambiguities:**

- Q19: Should the build command be executed via shell (bash/cmd) or directly? This affects placeholder escaping.
- Q20: What working directory should be used for the build command - repo root or project directory?
- Q21: Should build output be streamed in real-time or captured and shown on failure?

**Configuration Ambiguities:**

- Q22: The user-secrets key uses `servicename` but the annotation has both `ServiceName` and `GitHubRepository`. Which identifier is used as the key?
- Q23: Is the service name case-sensitive in configuration lookups?
- Q24: Can `RepositoriesBasePath` contain `~` for home directory expansion?

---

## Summary

| Field | Status | Critical Questions |
|-------|--------|-------------------|
| Testable Conditions | AMBIGUOUS | Q1-Q6 |
| Acceptance Criteria | AMBIGUOUS | Q7-Q14 |
| API Contracts | AMBIGUOUS | Q15-Q18 |
| Build Process | AMBIGUOUS | Q19-Q21 |
| Configuration | AMBIGUOUS | Q22-Q24 |

**Recommendation:** Do NOT proceed with implementation until at minimum Q1, Q7, Q9-Q10, Q15-Q16, Q19-Q20, and Q22 are answered. These directly impact the core implementation decisions.
