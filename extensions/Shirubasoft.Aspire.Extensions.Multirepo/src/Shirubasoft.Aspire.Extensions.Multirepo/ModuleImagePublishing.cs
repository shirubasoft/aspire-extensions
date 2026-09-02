using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal static class ModuleImageReference
{
    public static (string? Registry, string Name) ParseRepository(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var separator = repository.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return (null, repository);
        }

        var firstSegment = repository[..separator];
        if (HasExplicitRegistry(firstSegment))
        {
            return (firstSegment, repository[(separator + 1)..]);
        }

        return (null, repository);
    }

    private static bool HasExplicitRegistry(string firstSegment)
    {
        if (firstSegment.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        return firstSegment.Contains(':', StringComparison.Ordinal) ||
            string.Equals(firstSegment, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetRepository(ModuleImageCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.ImageRegistry)
            ? options.ImageName
            : $"{options.ImageRegistry}/{options.ImageName}";
    }

    public static string GetTag(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        var withoutDigest = imageReference.Split('@', 2)[0];
        var lastSlash = withoutDigest.LastIndexOf('/');
        var lastColon = withoutDigest.LastIndexOf(':');
        var candidates = new[] { "latest", withoutDigest[(lastColon + 1)..] };
        return candidates[Convert.ToInt32(lastColon > lastSlash)];
    }
}

internal static class ModuleImageTag
{
    private const int MaximumLength = 128;
    private const string FallbackTag = "latest";
    private const string DirtySuffix = "-dirty";

    public static string FromRepository(string? branchName, string? commit)
    {
        var branchTag = FromBranch(branchName);
        var commitTag = NormalizeCommit(commit);
        if (commitTag is null)
        {
            return branchTag;
        }

        var prefix = ResolveCommitPrefix(branchName, branchTag);
        var suffix = $"-{commitTag}";
        var availableLength = MaximumLength - suffix.Length;
        prefix = prefix[..Math.Min(prefix.Length, availableLength)].TrimEnd('.', '-');
        return $"{prefix}{suffix}";
    }

    private static string ResolveCommitPrefix(string? branchName, string branchTag)
    {
        if (branchTag == FallbackTag && string.IsNullOrWhiteSpace(branchName))
        {
            return "sha";
        }

        return branchTag;
    }

    public static string FromBranch(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return FallbackTag;
        }

        var characters = branchName.Trim()
            .Select(NormalizeTagCharacter)
            .ToArray();
        var tag = CollapseTagSeparators(new string(characters));
        tag = EnsureNonEmptyTag(tag.Trim('.', '-'));
        tag = EnsureValidTagStart(tag);
        tag = EnsureNonEmptyTag(tag);
        return tag[..Math.Min(tag.Length, MaximumLength)].TrimEnd('.', '-');
    }

    private static char NormalizeTagCharacter(char character)
    {
        if (IsTagCharacter(character))
        {
            return char.ToLowerInvariant(character);
        }

        return '-';
    }

    private static bool IsTagCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || "_.-".Contains(character, StringComparison.Ordinal);

    private static string CollapseTagSeparators(string tag)
    {
        while (tag.Contains("--", StringComparison.Ordinal))
        {
            tag = tag.Replace("--", "-", StringComparison.Ordinal);
        }

        return tag;
    }

    private static string EnsureNonEmptyTag(string tag)
    {
        if (tag.Length == 0)
        {
            return FallbackTag;
        }

        return tag;
    }

    private static string EnsureValidTagStart(string tag)
    {
        if (!IsTagFirstCharacter(tag[0]))
        {
            return $"branch-{tag.TrimStart('_', '.', '-')}".TrimEnd('-');
        }

        return tag;
    }

    private static bool IsTagFirstCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '_';

    public static string AppendDirtySuffix(string imageTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageTag);
        if (imageTag.EndsWith(DirtySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return imageTag;
        }

        var availableLength = MaximumLength - DirtySuffix.Length;
        var cleanTag = imageTag[..Math.Min(imageTag.Length, availableLength)].TrimEnd('.', '-');
        return $"{cleanTag}{DirtySuffix}";
    }

    private static string? NormalizeCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            return null;
        }

        var value = new string(commit.Trim()
            .Where(char.IsAsciiHexDigit)
            .Select(char.ToLowerInvariant)
            .Take(12)
            .ToArray());
        return value.Length >= 7 ? value : null;
    }
}

internal static class ContainerImageInspector
{
    public static async Task<bool> ExistsAsync(
        string containerRuntime,
        string imageReference,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        return await RunAsync(
            containerRuntime,
            ["image", "inspect", imageReference],
            timeout,
            $"Container image inspection '{imageReference}'",
            cancellationToken).ConfigureAwait(false) == 0;
    }

    public static async Task<bool> PullAsync(
        string containerRuntime,
        string imageReference,
        TimeSpan timeout,
        Action<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            var progressLock = new object();
            void ReportProgress(string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                lock (progressLock)
                {
                    progress(line);
                }
            }

            var result = await ModuleOperationTimeout.RunAsync(
                async token => await CliCommand.Wrap(containerRuntime)
                        .WithArguments(["pull", imageReference])
                        .WithValidation(CommandResultValidation.None)
                        .WithStandardOutputPipe(PipeTarget.ToDelegate(ReportProgress))
                        .WithStandardErrorPipe(PipeTarget.ToDelegate(ReportProgress))
                        .ExecuteAsync(token)
                        .ConfigureAwait(false),
                timeout,
                $"Container image pull '{imageReference}'",
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException)
        {
            return false;
        }
    }

    private static async Task<int?> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ModuleOperationTimeout.RunAsync(
                async token => await CliCommand.Wrap(executable)
                        .WithArguments(arguments)
                        .WithValidation(CommandResultValidation.None)
                        .WithStandardOutputPipe(PipeTarget.ToStream(Stream.Null))
                        .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
                        .ExecuteAsync(token)
                        .ConfigureAwait(false),
                timeout,
                operation,
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or TimeoutException)
        {
            return null;
        }
    }
}
