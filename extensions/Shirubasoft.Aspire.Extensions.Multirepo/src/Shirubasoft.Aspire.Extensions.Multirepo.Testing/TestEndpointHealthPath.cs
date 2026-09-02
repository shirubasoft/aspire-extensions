namespace Aspire.Hosting.Testing;

internal static class TestEndpointHealthPath
{
    public static bool IsRootRelative(string path)
    {
        if (!StartsWithSlash(path))
        {
            return false;
        }

        return !StartsWithAuthority(path) && Uri.IsWellFormedUriString(path, UriKind.Relative);
    }

    private static bool StartsWithSlash(string path) => path.Length > 0 && path[0] == '/';

    private static bool StartsWithAuthority(string path)
    {
        if (path.Length == 1)
        {
            return false;
        }

        return "/\\".Contains(path[1], StringComparison.Ordinal);
    }
}
