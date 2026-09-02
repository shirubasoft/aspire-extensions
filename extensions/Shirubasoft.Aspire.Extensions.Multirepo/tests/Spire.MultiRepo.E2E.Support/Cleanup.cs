namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    internal static async Task TryDeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => Directory.Delete(path, recursive: true), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                $"Cleanup warning for '{path}': {exception.Message}").ConfigureAwait(false);
        }
    }
}
