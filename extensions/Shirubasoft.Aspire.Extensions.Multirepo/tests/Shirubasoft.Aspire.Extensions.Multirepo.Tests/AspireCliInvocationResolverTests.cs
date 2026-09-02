using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class AspireCliInvocationResolverTests
{
    [Fact]
    public void Resolve_prefers_the_nearest_repository_tool_manifest()
    {
        using var directory = TemporaryDirectory.Create();
        var manifestDirectory = Path.Combine(directory.Path, ".config");
        var appHostDirectory = Path.Combine(directory.Path, "src", "AppHost");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(appHostDirectory);
        File.WriteAllText(
            Path.Combine(manifestDirectory, "dotnet-tools.json"),
            """
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "aspire.cli": {
                  "version": "13.4.6",
                  "commands": [ "aspire" ]
                }
              }
            }
            """);

        var invocation = AspireCliInvocationResolver.Resolve("aspire", appHostDirectory);

        Assert.Equal("dotnet", invocation.Executable);
        Assert.Equal(["tool", "run", "aspire", "--"], invocation.PrefixArguments);
    }

    [Fact]
    public void Resolve_preserves_explicit_paths_and_falls_back_when_no_manifest_provides_aspire()
    {
        using var directory = TemporaryDirectory.Create();
        var manifestDirectory = Path.Combine(directory.Path, ".config");
        var appHostPath = Path.Combine(directory.Path, "src", "AppHost", "AppHost.csproj");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(appHostPath)!);
        File.WriteAllText(
            Path.Combine(manifestDirectory, "dotnet-tools.json"),
            """{ "version": 1, "isRoot": true, "tools": {} }""");

        var explicitInvocation = AspireCliInvocationResolver.Resolve("custom-aspire", appHostPath);
        var fallbackInvocation = AspireCliInvocationResolver.Resolve("aspire", appHostPath);

        Assert.Equal("custom-aspire", explicitInvocation.Executable);
        Assert.Empty(explicitInvocation.PrefixArguments);
        Assert.Equal("aspire", fallbackInvocation.Executable);
        Assert.Empty(fallbackInvocation.PrefixArguments);

        File.WriteAllText(
            Path.Combine(manifestDirectory, "dotnet-tools.json"),
            """{ "version": 1, "isRoot": true }""");
        var missingToolsInvocation = AspireCliInvocationResolver.Resolve("aspire", appHostPath);
        Assert.Equal("aspire", missingToolsInvocation.Executable);
    }

    [Fact]
    public void Local_manifest_failure_only_falls_back_for_a_missing_restore_diagnostic()
    {
        var localInvocation = new AspireCliInvocation("dotnet", ["tool", "run", "aspire", "--"]);
        var explicitInvocation = new AspireCliInvocation("custom-aspire", []);

        Assert.True(AspireCliInvocationResolver.ShouldFallBackToAspireOnPath(
            localInvocation,
            "Run 'dotnet tool restore' to make the command available."));
        Assert.False(AspireCliInvocationResolver.ShouldFallBackToAspireOnPath(
            localInvocation,
            "Aspire failed for another reason."));
        Assert.False(AspireCliInvocationResolver.ShouldFallBackToAspireOnPath(
            explicitInvocation,
            "Run 'dotnet tool restore' to make the command available."));
    }

    [Fact]
    public void Resolve_reports_an_invalid_tool_manifest_with_its_path()
    {
        using var directory = TemporaryDirectory.Create();
        var manifestDirectory = Path.Combine(directory.Path, ".config");
        Directory.CreateDirectory(manifestDirectory);
        var manifestPath = Path.Combine(manifestDirectory, "dotnet-tools.json");
        File.WriteAllText(manifestPath, "{");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AspireCliInvocationResolver.Resolve("aspire", directory.Path));

        Assert.Contains(manifestPath, exception.Message, StringComparison.Ordinal);
    }
}
