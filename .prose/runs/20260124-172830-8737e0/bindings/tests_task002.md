# tests_task002

kind: let
execution_id: 2

source:
```prose
let tests = session: tester
  prompt: "Write tests for SharedResourceAnnotation..."
```

---

## Test Implementation

Created comprehensive tests for `SharedResourceAnnotation` class at:
`/home/danielreis/code/aspire-extensions/tests/SharedResources.Tests/Annotations/SharedResourceAnnotationTests.cs`

### Test Cases Implemented

1. **CreatingAnnotationWithAllRequiredPropertiesSucceeds**
   - Verifies that a `SharedResourceAnnotation` can be created with all required properties
   - Tests: `GitHubRepository`, `ServiceName`, `ImageBuildCommand`

2. **GetEffectiveImageName_ReturnsImageName_WhenImageNameIsSet**
   - Verifies `GetEffectiveImageName()` returns `ImageName` when explicitly set
   - Tests the primary case of the method

3. **GetEffectiveImageName_ReturnsServiceName_WhenImageNameIsNull**
   - Verifies `GetEffectiveImageName()` falls back to `ServiceName` when `ImageName` is null
   - Tests the fallback behavior

4. **DefaultBranch_DefaultsToMain**
   - Verifies the `DefaultBranch` property has correct default value of "main"
   - Tests default initialization

5. **AnnotationImplementsIResourceAnnotation**
   - Verifies the class implements `IResourceAnnotation` interface
   - Tests the type contract

### TUnit Patterns Applied

- All assertions use `await Assert.That()` syntax
- Used `[Test]` attribute on all test methods
- Followed Arrange/Act/Assert pattern for clarity
- Descriptive test method names following convention: `MethodName_Scenario_ExpectedBehavior`
- All tests are deterministic with no external dependencies

### Test Structure

- **Namespace**: `SharedResources.Tests.Annotations`
- **Class**: `SharedResourceAnnotationTests`
- **Framework**: TUnit 1.12.43
- **Async assertions**: All assertions properly awaited

All 5 contract requirements covered with comprehensive test coverage.
