# tunit_version

kind: let

source:
```prose
let tunit_version = session "Research TUnit version for .NET 10"
```

---

## Research Results: TUnit for .NET 10

### Package Information

**Package Name (NuGet ID):** `TUnit`

**Latest Stable Version:** `1.12.43` (Released: January 24, 2026)

**Compatibility with .NET 10:** ✅ **YES - Fully Compatible**

TUnit version 1.12.43 explicitly targets .NET 10.0 (net10.0) as one of its supported frameworks.

### Target Frameworks Supported

TUnit 1.12.43 includes builds for:
- .NET 10.0 (net10.0)
- .NET 9.0 (net9.0)
- .NET 8.0 (net8.0)
- .NET Standard 2.0 (netstandard2.0)

The package is also compatible with:
- .NET 5.0 through .NET 7.0
- .NET Framework 4.6.1 through 4.8.1
- Various platform-specific variants (Android, iOS, macOS, tvOS, etc.)

### Required Packages

TUnit requires these companion packages (all at version 1.12.43):

1. **TUnit.Assertions** (≥ 1.12.43) - Assertion library
2. **TUnit.Engine** (≥ 1.12.43) - Test execution engine

### Additional Dependencies

TUnit also requires these Microsoft Testing Platform packages:
- Microsoft.Testing.Extensions.CodeCoverage (≥ 18.3.2)
- Microsoft.Testing.Extensions.Telemetry (≥ 2.0.2)
- Microsoft.Testing.Extensions.TrxReport (≥ 2.0.2)
- System.Threading.Tasks.Extensions (≥ 4.6.3) - for .NET Standard 2.0 only

### Installation

To install TUnit for a .NET 10 project:

```bash
dotnet add package TUnit --version 1.12.43
```

Or add to your `.csproj` file:

```xml
<ItemGroup>
  <PackageReference Include="TUnit" Version="1.12.43" />
</ItemGroup>
```

The TUnit.Assertions and TUnit.Engine packages are automatically included as dependencies.

### About TUnit

TUnit is a modern, fast, and flexible .NET testing framework that offers:
- **Source Generation**: Uses source generators instead of reflection for test discovery
- **Parallel Execution**: Tests run in parallel by default
- **Native AOT Support**: AOT compatible due to source generator approach
- **Modern Platform**: Built on Microsoft.Testing.Platform (not VSTest)

### Sources

- [NuGet Gallery - TUnit 1.12.15](https://www.nuget.org/packages/TUnit/)
- [GitHub - thomhurst/TUnit](https://github.com/thomhurst/TUnit)
- [TUnit Official Website](https://tunit.dev/)
- [NuGet Gallery - TUnit.Engine](https://www.nuget.org/packages/TUnit.Engine/0.25.21)
- [NuGet Gallery - TUnit.Assertions](https://www.nuget.org/packages/TUnit.Assertions/0.1.817)

---

**Summary:** TUnit version 1.12.43 is the latest stable version and is fully compatible with .NET 10. The main package automatically includes TUnit.Assertions and TUnit.Engine as dependencies.
