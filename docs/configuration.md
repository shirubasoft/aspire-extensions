# Configuration Reference

This document describes all configuration options available for the SharedResources library.

## SharedResourceConfiguration

The `SharedResourceConfiguration` class contains all configuration settings for the library. Settings are read from the `SharedResources` section in your configuration.

### Configuration Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RepositoriesBasePath` | `string?` | `null` | Base directory where repositories are located. When set, repository paths are resolved as `{RepositoriesBasePath}/{repo-name}`. |
| `PromptForMissingPaths` | `bool` | `true` | Whether to prompt the user for missing repository paths. Set to `false` in CI/CD environments. |
| `RepositoryPaths` | `Dictionary<string, string>` | `{}` | Dictionary of explicit repository paths keyed by service name. Takes precedence over `RepositoriesBasePath`. |

### RepositoriesBasePath

When `RepositoriesBasePath` is set, the library automatically resolves repository paths by extracting the repository name from the `GitHubRepository` property and appending it to the base path.

**Example:**
```json
{
  "SharedResources": {
    "RepositoriesBasePath": "/home/user/repos"
  }
}
```

With `gitHubRepository: "myorg/api-service"`, the path resolves to `/home/user/repos/api-service`.

### PromptForMissingPaths

Controls interactive prompting behavior when a repository path cannot be resolved.

| Value | Behavior |
|-------|----------|
| `true` (default) | Uses `IInteractionService` to prompt the user for the path. Suitable for local development. |
| `false` | Throws `RepositoryNotFoundException` immediately. Required for CI/CD environments. |

### RepositoryPaths

A dictionary that maps service names to their absolute repository paths. These explicit paths take precedence over `RepositoriesBasePath` resolution.

**Example:**
```json
{
  "SharedResources": {
    "RepositoryPaths": {
      "api-service": "/custom/path/to/api-repo",
      "worker-service": "/another/path/to/worker"
    }
  }
}
```

## Configuration Sources

The SharedResources library supports multiple configuration sources, processed in the following priority order:

### 1. Explicit RepositoryPaths (Highest Priority)

Paths defined in `RepositoryPaths` dictionary take precedence over all other resolution methods.

### 2. Environment Variables

Environment variables can override or supplement configuration values.

### 3. User Secrets

Ideal for development environments, user secrets keep sensitive paths out of source control.

### 4. appsettings.json Files

Configuration files provide defaults that can be committed to source control.

### 5. RepositoriesBasePath Resolution

If no explicit path is found, the library attempts to resolve using `RepositoriesBasePath + repo-name`.

### 6. User Prompt (Lowest Priority)

If all other methods fail and `PromptForMissingPaths` is `true`, the user is prompted interactively.

## Configuration Methods

### appsettings.json

Add configuration to your `appsettings.json` or `appsettings.Development.json`:

```json
{
  "SharedResources": {
    "RepositoriesBasePath": "/home/user/repos",
    "PromptForMissingPaths": true,
    "RepositoryPaths": {
      "api-service": "/custom/path/to/api-service",
      "worker-service": "/custom/path/to/worker-service"
    }
  }
}
```

### User Secrets

User secrets are recommended for development to avoid committing local paths:

```bash
# Initialize user secrets (if not already done)
dotnet user-secrets init

# Set the base path for all repositories
dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/user/repos"

# Set individual repository paths
dotnet user-secrets set "SharedResources:RepositoryPaths:api-service" "/path/to/api-service"
dotnet user-secrets set "SharedResources:RepositoryPaths:worker-service" "/path/to/worker-service"

# Disable prompts
dotnet user-secrets set "SharedResources:PromptForMissingPaths" "false"
```

To list current secrets:
```bash
dotnet user-secrets list
```

To remove a secret:
```bash
dotnet user-secrets remove "SharedResources:RepositoriesBasePath"
```

### Environment Variables

Environment variables use the `__` (double underscore) separator for nested configuration keys.

**Naming Convention:**

| Configuration Key | Environment Variable |
|-------------------|---------------------|
| `SharedResources:RepositoriesBasePath` | `SHAREDRESOURCES__REPOSITORIESBASEPATH` |
| `SharedResources:PromptForMissingPaths` | `SHAREDRESOURCES__PROMPTFORMISSINGPATHS` |
| `SharedResources:RepositoryPaths:api-service` | `SHAREDRESOURCES__REPOSITORYPATHS__API-SERVICE` |

**Examples:**

```bash
# Set base path
export SHAREDRESOURCES__REPOSITORIESBASEPATH=/home/user/repos

# Set individual paths
export SHAREDRESOURCES__REPOSITORYPATHS__API-SERVICE=/path/to/api-service
export SHAREDRESOURCES__REPOSITORYPATHS__WORKER-SERVICE=/path/to/worker-service

# Disable prompts for CI/CD
export SHAREDRESOURCES__PROMPTFORMISSINGPATHS=false
```

**Note:** Service names in environment variables are case-insensitive but typically uppercased by convention.

## Priority Order

When resolving a repository path for a service, the library checks sources in this order:

1. **Explicit path in `RepositoryPaths[ServiceName]`**
   - From appsettings.json, user-secrets, or bound configuration

2. **Environment variable**
   - `SHAREDRESOURCES__REPOSITORYPATHS__{SERVICENAME}`

3. **`RepositoriesBasePath` + repository name**
   - Repository name is extracted from `GitHubRepository` (part after the slash)
   - Path must exist on the filesystem

4. **User prompt via `IInteractionService`**
   - Only if `PromptForMissingPaths` is `true`
   - User can optionally save the path to user secrets

If none of these sources provide a valid path, a `RepositoryNotFoundException` is thrown.

## Examples

### Development Configuration

For local development with multiple repositories in the same directory:

**appsettings.Development.json:**
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Aspire.Hosting.SharedResources": "Debug"
    }
  },
  "SharedResources": {
    "PromptForMissingPaths": true
  }
}
```

**User secrets:**
```bash
dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/user/code"
```

### CI/CD Configuration

For automated environments, disable prompts and use environment variables:

```yaml
# GitHub Actions example
env:
  SHAREDRESOURCES__PROMPTFORMISSINGPATHS: "false"
  SHAREDRESOURCES__REPOSITORYPATHS__API-SERVICE: ${{ github.workspace }}/api-service
  SHAREDRESOURCES__REPOSITORYPATHS__WORKER-SERVICE: ${{ github.workspace }}/worker-service
```

```yaml
# Azure DevOps example
variables:
  SHAREDRESOURCES__PROMPTFORMISSINGPATHS: "false"
  SHAREDRESOURCES__REPOSITORIESBASEPATH: $(Build.SourcesDirectory)/repos
```

### Mixed Configuration

Combine methods for flexibility:

**appsettings.json (defaults, committed):**
```json
{
  "SharedResources": {
    "PromptForMissingPaths": true
  }
}
```

**User secrets (developer-specific, not committed):**
```bash
dotnet user-secrets set "SharedResources:RepositoriesBasePath" "/home/alice/projects"
```

**Environment variables (override for specific runs):**
```bash
SHAREDRESOURCES__REPOSITORYPATHS__API-SERVICE=/tmp/test-repo dotnet run
```

## Build Command Placeholders

The `ImageBuildCommand` property in `SharedResourceAnnotation` supports these placeholders:

| Placeholder | Description | Example Value |
|-------------|-------------|---------------|
| `{RepoPath}` | Absolute path to the repository root directory | `/home/user/repos/api-service` |
| `{ProjectPath}` | Full path to the project file (`RepoPath` + `ProjectPath` property) | `/home/user/repos/api-service/src/Api/Api.csproj` |
| `{ImageName}` | Container image name (`ImageName` property or `ServiceName`) | `api-service` |
| `{ImageTag}` | Current commit SHA (7 characters) from the repository HEAD | `abc1234` |

### Example Build Commands

**dotnet publish (SDK container publishing):**
```csharp
imageBuildCommand: "dotnet publish {ProjectPath} --os linux /t:PublishContainer -p:ContainerRepository={ImageName} -p:ContainerImageTag={ImageTag}"
```

Substitutes to:
```
dotnet publish /home/user/repos/api-service/src/Api/Api.csproj --os linux /t:PublishContainer -p:ContainerRepository=api-service -p:ContainerImageTag=abc1234
```

**docker build:**
```csharp
imageBuildCommand: "docker build -t {ImageName}:{ImageTag} {RepoPath}"
```

Substitutes to:
```
docker build -t api-service:abc1234 /home/user/repos/api-service
```

**docker build with custom Dockerfile:**
```csharp
imageBuildCommand: "docker build -t {ImageName}:{ImageTag} -f {RepoPath}/docker/Dockerfile.prod {RepoPath}"
```

Substitutes to:
```
docker build -t api-service:abc1234 -f /home/user/repos/api-service/docker/Dockerfile.prod /home/user/repos/api-service
```

## Logging Configuration

Enable debug logging to troubleshoot path resolution:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Aspire.Hosting.SharedResources": "Debug"
    }
  }
}
```

This will output detailed information about:
- Path resolution attempts
- Configuration sources checked
- Git operations (commit SHA, branch)
- Image existence checks
- Build command execution
