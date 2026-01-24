# runnable_sample

kind: let

source:
```prose
let runnable_sample = do verify-runnable("sample-apphost", ...)
```

---

## Verification Results

### 1. Check Sample AppHost Exists

**Status:** PASS

**Command run:**
```bash
ls -la /home/danielreis/code/aspire-extensions/samples/SampleAppHost/
```

**Exit code:** 0

**Actual output:**
```
total 20
drwxr-xr-x 1 danielreis danielreis  198 jan 24 18:18 .
drwxr-xr-x 1 danielreis danielreis   26 jan 24 18:15 ..
-rw-r--r-- 1 danielreis danielreis   94 jan 24 18:16 appsettings.Development.json
-rw-r--r-- 1 danielreis danielreis  189 jan 24 18:16 appsettings.json
drwxr-xr-x 1 danielreis danielreis   10 jan 24 18:17 bin
drwxr-xr-x 1 danielreis danielreis  302 jan 24 18:18 obj
-rw-r--r-- 1 danielreis danielreis 1183 jan 24 18:16 Program.cs
drwxr-xr-x 1 danielreis danielreis   38 jan 24 18:16 Properties
-rw-r--r-- 1 danielreis danielreis 3775 jan 24 18:16 README.md
-rw-r--r-- 1 danielreis danielreis  394 jan 24 18:18 SampleAppHost.csproj
```

**Match with expected:** YES

**Notes:** All expected project files exist:
- `Program.cs` (1183 bytes)
- `SampleAppHost.csproj` (394 bytes)
- Additional files: appsettings.json, appsettings.Development.json, README.md, Properties directory

---

### 2. Build Sample AppHost

**Status:** PASS

**Command run:**
```bash
dotnet build /home/danielreis/code/aspire-extensions/samples/SampleAppHost/
```

**Exit code:** 0

**Actual output:**
```
  Determining projects to restore...
  Restored /home/danielreis/code/aspire-extensions/samples/SampleAppHost/SampleAppHost.csproj (in 244 ms).
  1 of 2 projects are up-to-date for restore.
  SharedResources -> /home/danielreis/code/aspire-extensions/src/SharedResources/bin/Debug/net10.0/SharedResources.dll
  SampleAppHost -> /home/danielreis/code/aspire-extensions/samples/SampleAppHost/bin/Debug/net10.0/SampleAppHost.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.31
```

**Match with expected:** YES

**Notes:** Build completed successfully with:
- 0 warnings
- 0 errors
- Both SharedResources and SampleAppHost compiled successfully
- Output: `SampleAppHost.dll` in `bin/Debug/net10.0/`

---

### 3. Check Sample AppHost References SharedResources

**Status:** PASS

**Command run:** Read SampleAppHost.csproj file

**File content:**
```xml
<Project Sdk="Aspire.AppHost.Sdk/13.1.0">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SharedResources\SharedResources.csproj" IsAspireProjectResource="false" />
  </ItemGroup>

</Project>
```

**Match with expected:** YES

**Notes:**
- Contains ProjectReference to `../../src/SharedResources/SharedResources.csproj`
- Uses `IsAspireProjectResource="false"` to indicate this is a library reference, not a deployable resource
- Uses Aspire.AppHost.Sdk version 13.1.0
- Targets .NET 10.0

---

### 4. Additional Verification: Program.cs Structure

**Status:** PASS (bonus verification)

**File content:**
```csharp
using Aspire.Hosting.SharedResources;

var builder = DistributedApplication.CreateBuilder(args);

// Enable shared resource support
builder.AddSharedResourceSupport();

// Example 1: Using inline parameters with options
builder.AddContainer("api-service", "api-service")
    .WithSharedResourceMetadata(
        gitHubRepository: "example-org/api-service",
        serviceName: "api-service",
        imageBuildCommand: "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}",
        options =>
        {
            options.ProjectPath = "src/Api/Api.csproj";
            options.DefaultBranch = "main";
        })
    .WithHttpEndpoint(5001, name: "http");

// Example 2: Using pre-built annotation
var workerAnnotation = new SharedResourceAnnotation
{
    GitHubRepository = "example-org/worker-service",
    ServiceName = "worker-service",
    ImageBuildCommand = "docker build -t {ImageName}:{ImageTag} -f {RepoPath}/Dockerfile {RepoPath}",
    DefaultBranch = "main"
};

builder.AddContainer("worker-service", "worker-service")
    .WithSharedResourceMetadata(workerAnnotation);

builder.Build().Run();
```

**Notes:**
- Correctly imports `Aspire.Hosting.SharedResources` namespace
- Calls `AddSharedResourceSupport()` as documented
- Demonstrates two usage patterns:
  1. Inline parameters with options callback
  2. Pre-built SharedResourceAnnotation object
- Uses documented placeholder syntax: `{ProjectPath}`, `{ImageName}`, `{ImageTag}`, `{RepoPath}`

---

## Summary

| Check | Status | Match |
|-------|--------|-------|
| Sample AppHost exists | PASS | YES |
| Build succeeds | PASS | YES |
| References SharedResources | PASS | YES |
| Program.cs structure correct | PASS | YES |

**Overall Result:** PASS

The Sample AppHost is correctly structured and compiles successfully. It properly references the SharedResources library and demonstrates the expected API usage patterns.
