using ActionsToolkit.Core.Extensions;
using ActionsToolkit.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shirubasoft.Aspire.Extensions.Multirepo.Tool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        using var services = new ServiceCollection()
            .AddGitHubActionsCore()
            .BuildServiceProvider();
        using var standardInput = Console.OpenStandardInput();

        return await ToolApplication.RunAsync(
            args,
            new CliWrapProcessRunner(standardInput, Console.Out, Console.Error),
            configuration,
            services.GetRequiredService<ICoreService>(),
            Directory.GetCurrentDirectory(),
            Console.Out,
            Console.Error,
            CancellationToken.None).ConfigureAwait(false);
    }
}
