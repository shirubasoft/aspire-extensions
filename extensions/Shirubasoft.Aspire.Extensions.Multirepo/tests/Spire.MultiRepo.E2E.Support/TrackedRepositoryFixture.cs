namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    private static class TrackedRepositoryFixture
    {
        public static async Task CopyAsync(
            ProcessExecutor process,
            string source,
            string destination,
            IReadOnlyList<string> pathSpecs,
            CancellationToken cancellationToken)
        {
            var listedFiles = await process.RunAsync(
                new ProcessInvocation(
                    "git",
                    [
                        "-C", source,
                        "ls-files",
                        "-z",
                        "--",
                        .. pathSpecs
                    ],
                    source),
                cancellationToken).ConfigureAwait(false);
            if (!listedFiles.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"git ls-files for the E2E fixture failed with exit code {listedFiles.ExitCode}:" +
                    $"{Environment.NewLine}{listedFiles.CombinedOutput}");
            }

            foreach (var relativePath in listedFiles.StandardOutput.Split(
                '\0',
                StringSplitOptions.RemoveEmptyEntries))
            {
                var sourceFile = Path.GetFullPath(relativePath, source);
                var destinationFile = Path.GetFullPath(relativePath, destination);
                if (!IsPathContainedBy(source, sourceFile) ||
                    !IsPathContainedBy(destination, destinationFile))
                {
                    throw new InvalidOperationException(
                        $"Tracked fixture path '{relativePath}' escapes its repository root.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)
                    ?? throw new InvalidOperationException(
                        $"Tracked fixture path '{relativePath}' has no parent directory."));
                File.Copy(sourceFile, destinationFile);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(sourceFile));
                }
            }
        }

        private static bool IsPathContainedBy(string root, string path)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return !Path.IsPathRooted(relative) &&
                !string.Equals(relative, "..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
    }
}
