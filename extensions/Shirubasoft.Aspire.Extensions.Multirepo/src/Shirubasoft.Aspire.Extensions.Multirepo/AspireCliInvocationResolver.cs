using System.Text.Json;

namespace Aspire.Hosting;

internal sealed record AspireCliInvocation(
    string Executable,
    IReadOnlyList<string> PrefixArguments)
{
    internal bool UsesLocalToolManifest
        => string.Equals(Executable, "dotnet", StringComparison.Ordinal)
            && PrefixArguments.SequenceEqual(["tool", "run", "aspire", "--"]);
}

internal static class AspireCliInvocationResolver
{
    internal static AspireCliInvocation Resolve(
        string aspireCliPath,
        string appHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspireCliPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        if (!string.Equals(aspireCliPath, "aspire", StringComparison.Ordinal))
        {
            return new AspireCliInvocation(aspireCliPath, []);
        }

        var manifestPath = FindAspireToolManifest(GetAppHostDirectory(appHostPath));
        return manifestPath is null
            ? new AspireCliInvocation(aspireCliPath, [])
            : new AspireCliInvocation("dotnet", ["tool", "run", "aspire", "--"]);
    }

    private static string GetAppHostDirectory(string appHostPath)
    {
        var absoluteAppHostPath = Path.GetFullPath(appHostPath);
        return Directory.Exists(absoluteAppHostPath)
            ? absoluteAppHostPath
            : Path.GetDirectoryName(absoluteAppHostPath)!;
    }

    private static string? FindAspireToolManifest(string appHostDirectory)
    {
        var current = new DirectoryInfo(appHostDirectory);
        while (current is not null)
        {
            var manifestPath = Path.Combine(current.FullName, ".config", "dotnet-tools.json");
            if (IsAspireToolManifest(manifestPath))
            {
                return manifestPath;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsAspireToolManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        return ManifestProvidesAspireCli(manifestPath);
    }

    internal static bool ShouldFallBackToAspireOnPath(
        AspireCliInvocation invocation,
        string output)
        => invocation.UsesLocalToolManifest
            && output.Contains("dotnet tool restore", StringComparison.OrdinalIgnoreCase);

    private static bool ManifestProvidesAspireCli(string manifestPath)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("tools", out var tools))
            {
                return false;
            }

            return ContainsAspireTool(tools);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Unable to read local .NET tool manifest '{manifestPath}'.",
                exception);
        }
    }

    private static bool ContainsAspireTool(JsonElement tools)
    {
        foreach (var tool in tools.EnumerateObject())
        {
            if (ProvidesAspireCommand(tool.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ProvidesAspireCommand(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return HasAspireCommandProperty(tool);
    }

    private static bool HasAspireCommandProperty(JsonElement tool)
    {
        if (!tool.TryGetProperty("commands", out var commands))
        {
            return false;
        }

        return IsAspireCommandArray(commands);
    }

    private static bool IsAspireCommandArray(JsonElement commands)
    {
        if (commands.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return ContainsAspireCommand(commands);
    }

    private static bool ContainsAspireCommand(JsonElement commands)
    {
        foreach (var command in commands.EnumerateArray())
        {
            if (string.Equals(command.GetString(), "aspire", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
