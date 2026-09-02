using Aspire.Hosting.Testing;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Spire.ModuleContract;
using Xunit;

namespace Spire.Consumer.Tests;

public sealed class ExternalImageTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Consumer_runs_the_producer_image_without_its_build_repository()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();
        var imageConfigurationKey = ModuleImageWorkflowConfiguration.GetResourceKey(
            SpireModule.Name,
            SpireModule.ApiResourceName,
            ModuleResourceKind.Container);
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(configuration[$"{imageConfigurationKey}:ImageName"]),
            "Run through modular-apphosts images apply with a module image workflow document.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TestTimeout);
        var unavailableDefinitionRepository = Path.Combine(
            Path.GetTempPath(),
            $"missing-workflow-image-definition-{Guid.NewGuid():N}");
        var unavailableBuildRepository = Path.Combine(
            Path.GetTempPath(),
            $"missing-workflow-image-build-{Guid.NewGuid():N}");
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Spire_Consumer_AppHost>(
                [
                    $"Aspire:ModularAppHosts:Modules:{SpireModule.Name}:Repository=" +
                    unavailableDefinitionRepository,
                    $"Aspire:ModularAppHosts:Modules:{SpireModule.Name}:Containers:" +
                    $"{SpireModule.ApiResourceName}:BuildRepository=" +
                    unavailableBuildRepository
                ],
                timeout.Token);
        await using var application = await builder.BuildAsync(timeout.Token)
            .WaitAsync(TestTimeout, timeout.Token);

        await application.StartAsync(timeout.Token).WaitAsync(TestTimeout, timeout.Token);
        await application.ResourceNotifications.WaitForResourceHealthyAsync(
            SpireModule.ApiResourceName,
            timeout.Token);
        using var client = application.CreateHttpClient(SpireModule.ApiResourceName, "http");

        var marker = await client.GetStringAsync("/marker.txt", timeout.Token);

        Assert.Equal("multi-repo-resource-pinned-revision", marker.Trim());
        Assert.False(Directory.Exists(unavailableDefinitionRepository));
        Assert.False(Directory.Exists(unavailableBuildRepository));
    }
}
