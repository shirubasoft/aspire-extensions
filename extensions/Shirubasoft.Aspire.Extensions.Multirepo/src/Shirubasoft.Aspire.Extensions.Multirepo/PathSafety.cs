namespace Aspire.Hosting;

internal static class PathSafety
{
    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool AreEqual(string? first, string? second)
    {
        return first is not null && second is not null &&
            string.Equals(Normalize(first), Normalize(second), PathComparison);
    }

    public static bool IsContainedBy(string root, string path)
    {
        var fullRoot = Normalize(root);
        var candidate = Normalize(path);
        var relativePath = Path.GetRelativePath(fullRoot, candidate);
        if (IsLexicallyOutsideRoot(relativePath))
        {
            return false;
        }

        return IsResolvedPathContainedBy(fullRoot, candidate);
    }

    private static bool IsLexicallyOutsideRoot(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return true;
        }

        return IsParentTraversal(relativePath);
    }

    private static bool IsParentTraversal(string relativePath)
    {
        if (relativePath.Equals("..", PathComparison))
        {
            return true;
        }

        return StartsWithParentTraversal(relativePath);
    }

    private static bool StartsWithParentTraversal(string relativePath) =>
        relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) ||
        relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);

    public static string GetContainedPath(string root, string path, string parameterName)
    {
        var candidate = Path.GetFullPath(path, root);
        if (!IsContainedBy(root, candidate))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                path,
                "The path must remain inside the selected repository and cannot traverse a symbolic link outside it.");
        }

        return candidate;
    }

    private static bool IsResolvedPathContainedBy(string root, string candidate)
    {
        var resolvedRoot = ResolvePath(root);
        var resolvedCandidate = ResolvePath(candidate);
        return IsLexicallyContainedBy(resolvedRoot, resolvedCandidate);
    }

    private static string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Unable to determine the root of '{path}'.");
        var current = pathRoot;
        var relativePath = Path.GetRelativePath(pathRoot, fullPath);

        foreach (var segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var entry = GetExistingEntry(current);
            current = ResolveEntryPath(current, entry);
        }

        return Normalize(current);
    }

    private static FileSystemInfo? GetExistingEntry(string path)
    {
        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path);
        }

        return GetExistingFile(path);
    }

    private static FileInfo? GetExistingFile(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path);
        }

        return null;
    }

    private static string ResolveEntryPath(string path, FileSystemInfo? entry)
    {
        if (entry?.LinkTarget is not null)
        {
            return ResolveLink(entry);
        }

        return path;
    }

    private static bool IsLexicallyContainedBy(string root, string candidate)
    {
        var relativePath = Path.GetRelativePath(root, candidate);
        return !IsLexicallyOutsideRoot(relativePath);
    }

    private static string ResolveLink(FileSystemInfo entry)
    {
        return Path.GetFullPath(entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? entry.FullName);
    }

    private static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && string.Equals(fullPath, root, PathComparison)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
