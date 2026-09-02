namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (ProcessTestChild.IsInvocation(args))
        {
            return await ProcessTestChild.RunAsync(args).ConfigureAwait(false);
        }

        if (RuntimeProxy.IsInvocation())
        {
            return await RuntimeProxy.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GitProxy.LogEnvironmentVariable)))
        {
            return await GitProxy.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
        }

        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        E2EOptions options;
        try
        {
            options = E2EOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }

        var repositoryRoot = options.RepositoryRoot ?? FindRepositoryRoot(Directory.GetCurrentDirectory());
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"modular-apphosts-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var scenario = new MultiRepositoryScenario(repositoryRoot, temporaryRoot, options);
            await scenario.RunAsync(cancellationSource.Token).ConfigureAwait(false);
            await Console.Out.WriteLineAsync("Multi-repository initialization E2E passed.").ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Multi-repository initialization E2E was cancelled.")
                .ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception)
        {
            await FailureBundle.WriteAsync(repositoryRoot, temporaryRoot, exception, CancellationToken.None)
                .ConfigureAwait(false);
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (!options.KeepTemporary)
            {
                await TryDeleteDirectoryAsync(temporaryRoot, cleanup.Token).ConfigureAwait(false);
            }
            else
            {
                await Console.Error.WriteLineAsync($"E2E workspace: {temporaryRoot}").ConfigureAwait(false);
            }
        }
    }
}
