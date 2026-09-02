namespace Spire.MultiRepo.E2E.Support;

internal static class ReadOnlyGitCommandPolicy
{
    private static readonly string[][] AllowedCommands =
    [
        ["branch", "--show-current"],
        ["config", "--get", "remote.origin.url"],
        ["diff", "--name-only", "-z", "--no-ext-diff", "HEAD", "--"],
        ["diff", "--cached", "--name-only", "-z", "--no-ext-diff", "HEAD", "--"],
        ["ls-files", "--others", "--exclude-standard", "-z", "--"],
        ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"],
        ["rev-parse", "--is-inside-work-tree"],
        ["rev-parse", "--short=12", "HEAD"],
        ["rev-parse", "--show-toplevel"],
        ["status", "--porcelain", "--untracked-files=normal"],
        ["status", "--porcelain=v1", "--untracked-files=all"]
    ];

    public static bool IsAllowed(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var command = GetCommandArguments(arguments);
        if (AllowedCommands.Any(allowed => command.SequenceEqual(allowed, StringComparer.Ordinal)))
        {
            return true;
        }

        return command.Count == 2 &&
            string.Equals(command[0], "rev-parse", StringComparison.Ordinal) &&
            !command[1].StartsWith("-", StringComparison.Ordinal) &&
            command[1].EndsWith("^{commit}", StringComparison.Ordinal);
    }

    public static string FindOperation(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var command = GetCommandArguments(arguments);
        if (command.Count >= 2 &&
            string.Equals(command[0], "submodule", StringComparison.Ordinal) &&
            string.Equals(command[1], "update", StringComparison.Ordinal))
        {
            return "submodule-update";
        }

        return command.FirstOrDefault() ?? "unknown";
    }

    private static IReadOnlyList<string> GetCommandArguments(IReadOnlyList<string> arguments)
    {
        var index = 0;
        while (index < arguments.Count)
        {
            if (string.Equals(arguments[index], "-C", StringComparison.Ordinal) && index + 1 < arguments.Count)
            {
                index += 2;
                continue;
            }

            break;
        }

        return arguments.Skip(index).ToArray();
    }
}
