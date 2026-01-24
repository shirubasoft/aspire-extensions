# requirements

kind: let

source:
```prose
let requirements = do validate-plan-section("Requirements", ["functional requirements", "non-functional requirements"])
```

---

## Validation Results

### Field: Functional Requirements
**Status: AMBIGUOUS**

The plan has "Expectations" and "Expected Behaviors" sections that serve as functional requirements, but several aspects need clarification.

**Questions:**

1. **Image Tag Format**: The plan shows "abc1234" as an example tag - is this always a 7-character short SHA? What if different git versions produce different lengths?

2. **Dirty Working Tree Handling**: The plan says "Log warning if working tree is dirty" but what tag should be used in this case? The image tagging table only shows "Clean" and "Detached HEAD" states, not dirty states.

3. **Build Verification**: "Verify image was created" - what happens if verification fails? Should it retry? How many times?

4. **User Prompt Behavior**: When prompting for missing paths, what happens if the user provides an invalid path? Is there validation and re-prompting?

5. **Save to User Secrets**: The interaction example shows "Save this path for future runs? [Y/n]" - what if the user says 'n'? Does it still work for the current session?

6. **Multiple Resources Same Repository**: What happens if two different container resources reference the same GitHub repository but different ProjectPaths? Should they share the same resolved path?

7. **Image Name Default**: The annotation says "Container image name defaults to ServiceName if not specified" - but in `AddContainer("api-1", "api-1")` the second parameter is already the image name. How does this interact?

8. **Build Command Placeholder Validation**: What happens if the build command uses `{ProjectPath}` but `ProjectPath` is null?

9. **IInteractionService Availability**: What happens if `IInteractionService` is not available (e.g., running in a non-interactive environment) and `PromptForMissingPaths` is true?

10. **Container Runtime**: The plan assumes Docker (`docker image inspect`) - should Podman or other OCI-compatible runtimes be supported?

---

### Field: Non-Functional Requirements
**Status: MISSING**

The "Edge Cases and Error Handling" section covers some error scenarios but does not address standard non-functional requirements.

**Questions:**

1. **Performance**: What is the acceptable startup time impact? Should builds run in parallel for multiple resources?

2. **Timeout**: What is the maximum time allowed for a build operation before it's considered failed?

3. **Logging**: What logging framework/level should be used? What information should be logged at each level (Debug, Info, Warning, Error)?

4. **Concurrency**: The plan mentions "Lock per-repository to prevent conflicts" - what locking mechanism? File-based? In-memory? What happens on lock timeout?

5. **Cancellation**: How should the system respond to cancellation tokens during long-running builds?

6. **Resource Cleanup**: Should partial builds be cleaned up on failure?

7. **Security**: Are there any security considerations for executing user-provided build commands? Should commands be validated/sanitized?

8. **Compatibility**: What minimum Docker version is required? What minimum Git version?

9. **Telemetry/Metrics**: Should build times, success/failure rates be tracked?

10. **Test Coverage Requirements**: What level of test coverage is expected for the library?

---

## Summary

| Field | Status | Questions Count |
|-------|--------|-----------------|
| Functional Requirements | AMBIGUOUS | 10 |
| Non-Functional Requirements | MISSING | 10 |

**Recommendation**: The plan cannot proceed to implementation until these 20 questions are answered. The functional requirements need clarification on edge cases and behavior specifics. The non-functional requirements section needs to be added to the plan entirely.
