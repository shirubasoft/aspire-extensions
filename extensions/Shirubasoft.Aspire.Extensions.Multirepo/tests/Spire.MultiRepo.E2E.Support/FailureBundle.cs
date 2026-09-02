namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    private static class FailureBundle
    {
        public static async Task WriteAsync(
            string repositoryRoot,
            string temporaryRoot,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var outputDirectory = Path.Combine(
                repositoryRoot,
                "artifacts",
                "e2e",
                "multi-repo-failure");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "exception.txt"),
                exception.ToString(),
                cancellationToken).ConfigureAwait(false);

            if (!Directory.Exists(temporaryRoot))
            {
                return;
            }

            foreach (var source in Directory.EnumerateFiles(
                temporaryRoot,
                "*",
                SearchOption.AllDirectories).Where(IsDiagnosticFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(temporaryRoot, source);
                var safeName = string.Concat(relativePath.Select(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '.' or '-'
                        ? character
                        : '_'));
                try
                {
                    var contents = await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        Path.Combine(outputDirectory, safeName),
                        contents,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception diagnosticException) when (
                    diagnosticException is IOException or UnauthorizedAccessException)
                {
                    await Console.Error.WriteLineAsync(
                        $"Unable to add '{relativePath}' to the failure bundle: " +
                            diagnosticException.Message).ConfigureAwait(false);
                }
            }
        }

        private static bool IsDiagnosticFile(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
        }
    }
}
