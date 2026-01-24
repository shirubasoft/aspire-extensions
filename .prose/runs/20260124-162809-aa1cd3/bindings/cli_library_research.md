# cli_library_research

kind: let

source:
```prose
let cli_library_research = do research-library-alternatives("CLI process wrapper library")
```

---

## .NET CLI Wrapper Libraries for Process Execution

Research conducted: January 24, 2026

### Executive Summary

For avoiding direct `Process` class usage when interacting with git and docker CLIs, **CliWrap** is the clear winner, followed by **MedallionShell** as a solid alternative if you need broader framework compatibility. **SimpleExec** is suitable only for very basic command execution scenarios.

---

## Top Recommendations

### 1. CliWrap (Highly Recommended)

| Attribute | Details |
|-----------|---------|
| **Current Version** | 3.10.0 |
| **Release Date** | November 19, 2025 |
| **Total Downloads** | 17.7M |
| **License** | MIT |
| **NuGet** | https://www.nuget.org/packages/CliWrap |
| **GitHub** | https://github.com/Tyrrrz/CliWrap |

#### Framework Support
- .NET 6.0+ (no dependencies)
- .NET Standard 2.0+ (minimal dependencies)
- Compatible with .NET 7.0, 8.0, 9.0, 10.0

#### Maintenance Status
**Actively Maintained** - Regular releases every 5-6 months. Version 3.9.0 released June 2025, 3.10.0 released November 2025.

#### Key Features
- Fluent, immutable configuration API
- Flexible piping for stdin/stdout/stderr streams
- Fully asynchronous and cancellation-aware
- Graceful process termination via interrupt signals
- Event stream model for real-time output processing
- Cross-platform (Windows, Linux, macOS)
- Zero external dependencies on modern .NET

#### API Example (Git)
```csharp
var result = await Cli.Wrap("git")
    .WithArguments(["clone", "https://github.com/user/repo.git"])
    .WithWorkingDirectory("/projects")
    .ExecuteBufferedAsync();

Console.WriteLine(result.StandardOutput);
```

#### API Example (Docker)
```csharp
await foreach (var cmdEvent in Cli.Wrap("docker")
    .WithArguments(["build", "-t", "myapp", "."])
    .ListenAsync())
{
    switch (cmdEvent)
    {
        case StandardOutputCommandEvent stdOut:
            Console.WriteLine(stdOut.Text);
            break;
        case StandardErrorCommandEvent stdErr:
            Console.Error.WriteLine(stdErr.Text);
            break;
    }
}
```

#### Pros
- Best-in-class documentation with video tutorials
- Handles deadlock prevention automatically
- Event stream model perfect for long-running docker builds
- Immutable design prevents configuration bugs
- Most popular option (17.7M downloads, 5.5K/day)

#### Cons
- Memory buffering with `ExecuteBufferedAsync()` requires caution for large outputs
- Resource policies have varying platform support
- Credentials configuration primarily Windows-focused

#### Learning Curve
**Low** - Excellent documentation, video guides by Nick Chapsas, intuitive fluent API.

---

### 2. MedallionShell (Good Alternative)

| Attribute | Details |
|-----------|---------|
| **Current Version** | 1.6.2 |
| **Release Date** | November 15, 2020 |
| **Total Downloads** | 1.6M |
| **License** | MIT |
| **NuGet** | https://www.nuget.org/packages/MedallionShell |
| **GitHub** | https://github.com/madelson/MedallionShell |

#### Framework Support
- .NET Standard 1.3 and 2.0
- .NET Framework 4.5, 4.6, 4.7.1
- Compatible with .NET 5.0 through 10.0

#### Maintenance Status
**Not Actively Maintained** - Last release was November 2020. However, the library is stable and feature-complete for its intended use case.

#### Key Features
- Clean integration with async/await and Task
- Automatic handling of standard IO streams without deadlocks
- Secure argument escaping (prevents injection vulnerabilities)
- Timeout, CancellationToken, and signal support
- Shell object for reusable command configurations
- Cross-platform (Windows, Linux, Mono)

#### API Example (Git)
```csharp
var result = await Command.Run("git", "commit", "-m", "critical bugfix");
Console.WriteLine(result.StandardOutput);
```

#### API Example (Docker with Piping)
```csharp
await Command.Run("docker", "logs", "container-id")
    .RedirectTo(new FileInfo("container.log"));
```

#### Pros
- Broader framework compatibility (supports older .NET Framework)
- Excellent piping and stream redirection
- Secure by default (argument escaping)
- Well-documented with progressive complexity examples
- Stable, battle-tested codebase

#### Cons
- Not actively maintained (4+ years since last release)
- May lack support for newest .NET features
- Smaller community compared to CliWrap

#### Learning Curve
**Low to Medium** - Good documentation, but fewer tutorials and examples compared to CliWrap.

---

### 3. SimpleExec (For Basic Scenarios Only)

| Attribute | Details |
|-----------|---------|
| **Current Version** | 13.0.0 |
| **Release Date** | January 5, 2026 |
| **Total Downloads** | 3.3M |
| **License** | Apache 2.0 |
| **NuGet** | https://www.nuget.org/packages/SimpleExec |
| **GitHub** | https://github.com/adamralph/simple-exec |

#### Framework Support
- .NET 8.0+ only (no legacy support)

#### Maintenance Status
**Actively Maintained** - Very recent release (January 2026).

#### Key Features
- Minimal API surface (Run, RunAsync, ReadAsync)
- No shell invocation (direct process execution)
- Custom exit code handling
- Secrets masking for logs
- Zero dependencies

#### API Example
```csharp
await SimpleExec.Command.RunAsync("git", "status");

var (stdout, stderr) = await SimpleExec.Command.ReadAsync("docker", "ps");
```

#### Pros
- Extremely simple API
- Very actively maintained
- Zero dependencies
- Good for basic command execution

#### Cons
- **Not suitable for complex git/docker interactions**
- No piping or stream redirection
- No real-time output streaming
- Requires .NET 8.0+ (no legacy support)
- Requires substantial wrapper code for sophisticated workflows

#### Learning Curve
**Very Low** - Minimal API, but limited functionality.

---

## Recommendation Summary

| Use Case | Recommended Library |
|----------|---------------------|
| **Git/Docker CLI (modern .NET)** | CliWrap |
| **Git/Docker CLI (legacy .NET Framework)** | MedallionShell |
| **Simple one-off commands** | SimpleExec |
| **Real-time streaming output** | CliWrap |
| **Complex piping scenarios** | CliWrap or MedallionShell |

### Final Recommendation

**Use CliWrap** for your git and docker CLI interactions. It offers:

1. **Active maintenance** with regular releases
2. **Best documentation** with video tutorials
3. **Event streaming** perfect for docker build output
4. **Modern async patterns** with cancellation support
5. **Largest community** for support and examples

Install via:
```bash
dotnet add package CliWrap
```

---

## Sources

- [CliWrap on NuGet](https://www.nuget.org/packages/CliWrap)
- [CliWrap on GitHub](https://github.com/Tyrrrz/CliWrap)
- [MedallionShell on NuGet](https://www.nuget.org/packages/MedallionShell)
- [MedallionShell on GitHub](https://github.com/madelson/MedallionShell)
- [SimpleExec on NuGet](https://www.nuget.org/packages/SimpleExec)
- [SimpleExec on GitHub](https://github.com/adamralph/simple-exec)
- [Code Maze - Execute CLI Applications From C#](https://code-maze.com/csharp-execute-cli-applications/)
