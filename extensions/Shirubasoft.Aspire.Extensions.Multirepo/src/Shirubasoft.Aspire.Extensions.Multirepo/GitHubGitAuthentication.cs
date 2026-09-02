namespace Aspire.Hosting;

internal static class GitHubGitAuthentication
{
    public static bool UsesCredentialProvider(string? repository) =>
        TryGetHttpsCredentialScope(repository) is not null;

    public static bool IsGitHubRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        var value = repository.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return IsGitHubHost(uri.Host);
        }

        return IsScpLikeGitHubRepository(value);
    }

    private static bool IsScpLikeGitHubRepository(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return IsOwnerRepository(value);
        }

        var hostStart = value.LastIndexOf('@', colon);
        return IsGitHubHost(value[(hostStart >= 0 ? hostStart + 1 : 0)..colon]);
    }

    private static bool IsOwnerRepository(string value) =>
        value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;

    public static IReadOnlyList<string> ConfigureCredentialHelper(
        IReadOnlyList<string> arguments,
        string? repository,
        string githubCliPath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubCliPath);

        var credentialScope = TryGetHttpsCredentialScope(repository);
        if (credentialScope is null)
        {
            return arguments;
        }

        var configured = new List<string>(arguments.Count + 4)
        {
            "-c",
            $"credential.{credentialScope}.helper=",
            "-c",
            $"credential.{credentialScope}.helper=!{QuoteShellArgument(githubCliPath)} auth git-credential"
        };
        configured.AddRange(arguments);
        return configured;
    }

    private static string? TryGetHttpsCredentialScope(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        return TryGetAbsoluteHttpsCredentialScope(repository.Trim());
    }

    private static string? TryGetAbsoluteHttpsCredentialScope(string repository)
    {
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return TryGetHttpsCredentialScope(uri);
    }

    private static string? TryGetHttpsCredentialScope(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryGetGitHubCredentialScope(uri);
    }

    private static string? TryGetGitHubCredentialScope(Uri uri)
    {
        if (!IsGitHubHost(uri.Host))
        {
            return null;
        }

        return $"https://{uri.IdnHost}";
    }

    private static bool IsGitHubHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase) ||
        host.StartsWith("github.", StringComparison.OrdinalIgnoreCase);

    private static string QuoteShellArgument(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
