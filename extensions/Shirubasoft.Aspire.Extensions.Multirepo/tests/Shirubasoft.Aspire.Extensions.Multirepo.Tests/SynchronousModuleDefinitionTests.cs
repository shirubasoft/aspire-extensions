using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class SynchronousModuleDefinitionTests
{
    [Fact]
    public void DefineModule_does_not_invoke_the_configured_git_executable()
    {
        using var appHost = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(appHost.Path, ".git"));
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
            options.GitExecutablePath = "git-must-not-run-during-definition");

        var module = builder.DefineModule(
            "orders",
            "1",
            definition => definition.AddContainer("orders-cache", "redis"));

        Assert.Equal("orders", module.Name);
        Assert.Single(module.Containers);
        Assert.Empty(builder.Resources);
    }

    [Fact]
    public void DefineModule_is_idempotent_and_validates_contract_identity()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var callbackCount = 0;

        var first = builder.DefineModule("orders", "2", "Sample.Orders", definition =>
        {
            callbackCount++;
            definition.AddContainer("orders-cache", "redis");
        });
        var duplicate = builder.DefineModule("orders", "2", "Sample.Orders", _ => callbackCount++);

        Assert.Same(first, duplicate);
        Assert.Equal(1, callbackCount);
        Assert.Throws<InvalidOperationException>(() =>
            builder.DefineModule("orders", "3", "Sample.Orders", _ => { }));
        Assert.Throws<InvalidOperationException>(() =>
            builder.DefineModule("orders", "2", "Sample.Other", _ => { }));
        Assert.Throws<ArgumentException>(() =>
            builder.DefineModule("invalid", "1", "invalid/package", _ => { }));
    }

    [Fact]
    public void Filesystem_repository_discovery_accepts_git_worktree_metadata_files()
    {
        using var repository = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(repository.Path, ".git"), "gitdir: /tmp/example");
        var projectDirectory = Path.Combine(repository.Path, "src", "Orders");
        Directory.CreateDirectory(projectDirectory);

        var root = RepositoryIdentity.TryFindRepositoryRoot(projectDirectory);

        Assert.Equal(repository.Path, root);
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory)
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(SynchronousModuleDefinitionTests).Assembly.FullName,
            ProjectDirectory = projectDirectory,
            DisableDashboard = true
        });
    }
}
