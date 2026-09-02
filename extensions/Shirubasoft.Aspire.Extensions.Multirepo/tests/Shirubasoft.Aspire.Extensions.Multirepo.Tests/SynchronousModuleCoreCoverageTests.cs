using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class SynchronousModuleCoreCoverageTests
{
    [Fact]
    public void Module_reference_delegates_contract_metadata_collections_and_resource_lookups()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var module = builder.DefineModule(
            "orders",
            "2",
            "Sample.Orders",
            definition => definition.AddContainer("cache", "redis", "8"));

        Assert.Equal("orders", module.Name);
        Assert.Equal("2", module.Version);
        Assert.Equal("Sample.Orders", module.PackageId);
        Assert.Single(module.Resources);
        Assert.Empty(module.Projects);
        Assert.Single(module.Containers);
        Assert.Throws<InvalidOperationException>(() => module.GetResource<ContainerResource>("cache"));

        builder.AddModule(module);
        var reference = new TestModuleReference(module);

        Assert.Equal(module.Name, reference.Name);
        Assert.Equal(module.Version, reference.Version);
        Assert.Equal(module.PackageId, reference.PackageId);
        Assert.Same(module.Resources, reference.Resources);
        Assert.Same(module.Projects, reference.Projects);
        Assert.Same(module.Containers, reference.Containers);
        Assert.Equal("cache", reference.GetResource<ContainerResource>("cache").Resource.Name);
        Assert.Throws<KeyNotFoundException>(() => reference.GetResource<ContainerResource>("missing"));
        Assert.Throws<InvalidOperationException>(() => reference.GetResource<ProjectResource>("cache"));
        Assert.Throws<ArgumentException>(() => reference.GetResource<ContainerResource>(" "));
        Assert.Throws<ArgumentNullException>(() => new TestModuleReference(null!));
    }

    [Fact]
    public void Module_attribute_exposes_default_and_configured_contract_identity()
    {
        var attribute = new GenerateDistributedApplicationModuleAttribute("orders");

        Assert.Equal("orders", attribute.Name);
        Assert.Equal("1", attribute.Version);
        Assert.Null(attribute.PackageId);

        attribute.Version = "3";
        attribute.PackageId = "Sample.Orders";

        Assert.Equal("3", attribute.Version);
        Assert.Equal("Sample.Orders", attribute.PackageId);
    }

    [Fact]
    public void Module_builder_binds_options_and_resolves_required_contracts_synchronously()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        builder.Configuration[
            $"{DistributedApplicationModuleExtensions.GetModuleConfigurationKey("orders")}:Region"] = "south";
        var catalog = builder.DefineModule(
            "catalog",
            "2",
            definition => definition.AddContainer("catalog-api", "catalog"));
        IDistributedApplicationModule? resolved = null;

        var orders = builder.DefineModule("orders", "1", definition =>
        {
            Assert.Same(builder.Configuration, definition.Configuration);
            Assert.Equal(
                DistributedApplicationModuleExtensions.GetModuleConfigurationKey("orders"),
                definition.ConfigurationSection.Path);
            Assert.Equal("south", definition.GetOptions<ModuleOptions>().Value.Region);
            resolved = definition.GetRequiredModule("catalog", "2");
            definition.WithRepository(appHost.Path, "  pinned-revision  ");
            definition.RequiresRepository();
            definition.AddResource<ParameterResource>("region", context =>
                context.ApplicationBuilder.AddParameter(
                    context.ResourceName,
                    "south",
                    publishValueAsDefault: true));
        });

        var typed = Assert.IsType<DistributedApplicationModule>(orders);
        Assert.Same(catalog, resolved);
        Assert.Equal(appHost.Path, typed.Repository);
        Assert.Equal("pinned-revision", typed.RepositoryRevision);
        Assert.True(typed.RequiresRepositoryContent);
        Assert.True(typed.ExplicitlyRequiresRepositoryContent);

        Assert.Throws<InvalidOperationException>(() => builder.DefineModule("missing-dependent", "1", definition =>
        {
            definition.GetRequiredModule("missing", "1");
            definition.AddContainer("api", "example/api");
        }));
        Assert.Throws<InvalidOperationException>(() => builder.DefineModule("wrong-version-dependent", "1", definition =>
        {
            definition.GetRequiredModule("catalog", "1");
            definition.AddContainer("api", "example/api");
        }));
    }

    [Fact]
    public void Module_builder_rejects_duplicate_names_and_invalid_project_declarations()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var projectPath = CreateProject(appHost.Path, "Orders.Api");

        var module = builder.ExportModule("validations", definition =>
        {
            definition.AddContainer("cache", "redis");
            Assert.Throws<InvalidOperationException>(() =>
                definition.AddResource<ParameterResource>("CACHE", context =>
                    context.ApplicationBuilder.AddParameter(context.ResourceName)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                definition.AddProject("invalid-enum", "relative.csproj", (ModuleProjectPathBase)99));
            Assert.Throws<ArgumentException>(() =>
                definition.AddProject("rooted", projectPath, ModuleProjectPathBase.Repository));
        });

        Assert.Single(module.Resources);

        var missingExport = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("missing-export", definition =>
                definition.AddProject("api", projectPath)));
        Assert.Contains("exported as a container", missingExport.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_definition_rejects_projects_from_multiple_repository_roots()
    {
        using var appHost = TemporaryDirectory.Create();
        using var firstRepository = TemporaryDirectory.Create();
        using var secondRepository = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(firstRepository.Path, ".git"));
        Directory.CreateDirectory(Path.Combine(secondRepository.Path, ".git"));
        var firstProject = CreateProject(firstRepository.Path, "First.Api");
        var secondProject = CreateProject(secondRepository.Path, "Second.Api");
        var builder = CreateBuilder(appHost.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ExportModule("split", definition =>
            {
                definition.AddProject("first", firstProject)
                    .ExportAsContainerWithCommand(new ModuleImageCommandOptions("first", "dotnet", "publish"));
                definition.AddProject("second", secondProject)
                    .ExportAsContainerWithCommand(new ModuleImageCommandOptions("second", "dotnet", "publish"));
            }));

        Assert.Contains("same Git repository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_and_container_builders_validate_callbacks_and_copy_image_options()
    {
        using var appHost = TemporaryDirectory.Create();
        var projectPath = CreateProject(appHost.Path, "Orders.Api");
        var builder = CreateBuilder(appHost.Path);
        var declaredOptions = new ModuleImageCommandOptions("orders-api", "dotnet", "publish")
        {
            ImageRegistry = "registry.example",
            ProducedImageReference = "orders-api:legacy",
            PullBeforeBuild = true,
            ImageTag = "candidate",
            WorkingDirectory = ".",
            BuildRepository = appHost.Path,
            BuildRepositoryRevision = "main"
        };

        var module = builder.ExportModule("builder-coverage", definition =>
        {
            Assert.Throws<ArgumentNullException>(() =>
                definition.AddResource<ParameterResource>("null-factory", null!));

            var project = definition.AddProject("api", projectPath);
            Assert.Equal("api", project.Project.Name);
            Assert.Throws<ArgumentNullException>(() => project.ConfigureProject(null!));
            Assert.Throws<ArgumentNullException>(() => project.ExportAsContainerWithCommand(null!));
            Assert.Throws<ArgumentException>(() => project.ExportAsContainer(" "));
            project.ExportAsContainerWithCommand(declaredOptions);

            var container = definition.AddContainer("worker", "worker");
            Assert.Equal("worker", container.Container.Name);
            Assert.Throws<ArgumentNullException>(() => container.Configure(null!));
            Assert.Throws<ArgumentNullException>(() => container.WithImagePublishCommand(null!));
            Assert.Throws<ArgumentException>(() => container.WithImagePublishCommand(
                new ModuleImageCommandOptions("different", "docker", "build")));

            var published = definition.AddContainer("published", "published", "candidate");
            published.Configure((_, _) => { });
            published.WithImagePublishCommand(new ModuleImageCommandOptions(
                "published",
                "docker",
                "build")
            {
                ImageTag = "candidate"
            });
            Assert.Throws<InvalidOperationException>(() => published.WithImagePublishCommand(
                new ModuleImageCommandOptions("published", "docker", "build")
                {
                    ImageTag = "candidate"
                }));

            var registryPublished = definition.AddContainer(
                "registry-published",
                "example/registry-published");
            registryPublished.WithImagePublishCommand(new ModuleImageCommandOptions(
                "example/registry-published",
                ModuleImageCommandOptions.ContainerRuntimePlaceholder,
                "build")
            {
                ImageRegistry = "registry.example"
            });
        });

        var typed = Assert.IsType<DistributedApplicationModule>(module);
        var copiedOptions = Assert.Single(typed.ProjectDefinitions).Export.CommandOptions!;
        Assert.NotSame(declaredOptions, copiedOptions);
        Assert.Equal(declaredOptions.ImageName, copiedOptions.ImageName);
        Assert.Equal(declaredOptions.ImageRegistry, copiedOptions.ImageRegistry);
        Assert.Equal(declaredOptions.ProducedImageReference, copiedOptions.ProducedImageReference);
        Assert.Equal(declaredOptions.PullBeforeBuild, copiedOptions.PullBeforeBuild);
        Assert.Equal(declaredOptions.ImageTag, copiedOptions.ImageTag);
        Assert.Equal(declaredOptions.WorkingDirectory, copiedOptions.WorkingDirectory);
        Assert.Equal(declaredOptions.BuildRepository, copiedOptions.BuildRepository);
        Assert.Equal(declaredOptions.BuildRepositoryRevision, copiedOptions.BuildRepositoryRevision);
        Assert.NotNull(typed.ContainerDefinitions.Single(container => container.Name == "published").ImagePublishOptions);
        Assert.NotNull(typed.ContainerDefinitions
            .Single(container => container.Name == "registry-published")
            .ImagePublishOptions);
    }

    [Fact]
    public void Import_applies_prefixes_and_case_insensitive_aliases()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.AddContainer("api", "example/api");
            definition.AddContainer("cache", "redis");
        });
        var options = new ModuleImportOptions { ResourcePrefix = "sales-" };
        options.ResourceAliases["CACHE"] = "shared-cache";

        builder.ImportModule(module.Name, options);

        Assert.Contains(builder.Resources, resource => resource.Name == "sales-api");
        Assert.Contains(builder.Resources, resource => resource.Name == "shared-cache");
        Assert.Equal("shared-cache", module.GetResource<ContainerResource>("cache").Resource.Name);
    }

    [Theory]
    [InlineData(AliasFailure.Unknown)]
    [InlineData(AliasFailure.Empty)]
    [InlineData(AliasFailure.Duplicate)]
    public void Import_rejects_invalid_resource_aliases(AliasFailure failure)
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var module = builder.ExportModule("orders", definition =>
        {
            definition.AddContainer("api", "example/api");
            definition.AddContainer("cache", "redis");
        });
        var options = new ModuleImportOptions();
        switch (failure)
        {
            case AliasFailure.Unknown:
                options.ResourceAliases["missing"] = "renamed";
                break;
            case AliasFailure.Empty:
                options.ResourceAliases["api"] = " ";
                break;
            case AliasFailure.Duplicate:
                options.ResourceAliases["api"] = "same";
                options.ResourceAliases["cache"] = "SAME";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }

        Assert.Throws<InvalidOperationException>(() => builder.ImportModule(module.Name, options));
    }

    [Fact]
    public void Resource_callbacks_report_declaration_order_unknown_name_and_type_mismatches()
    {
        AssertMaterializationFailure(
            "order",
            definition =>
            {
                definition.AddResource<ContainerResource>("consumer", context =>
                {
                    context.GetResource<ParameterResource>("later");
                    return context.ApplicationBuilder.AddContainer(context.ResourceName, "consumer");
                });
                definition.AddResource<ParameterResource>("later", context =>
                    context.ApplicationBuilder.AddParameter(context.ResourceName));
            },
            "declaration order");

        AssertMaterializationFailure(
            "unknown",
            definition => definition.AddResource<ContainerResource>("consumer", context =>
            {
                context.GetResource<ParameterResource>("missing");
                return context.ApplicationBuilder.AddContainer(context.ResourceName, "consumer");
            }),
            "does not declare");

        AssertMaterializationFailure(
            "type",
            definition =>
            {
                definition.AddContainer("cache", "redis");
                definition.AddResource<ContainerResource>("consumer", context =>
                {
                    context.GetResource<ParameterResource>("cache");
                    return context.ApplicationBuilder.AddContainer(context.ResourceName, "consumer");
                });
            },
            "not 'ParameterResource'");

        AssertMaterializationFailure(
            "null-factory",
            definition => definition.AddResource<ContainerResource>("expected", _ => null!),
            "returned null");

        AssertMaterializationFailure(
            "wrong-factory-name",
            definition => definition.AddResource<ContainerResource>("expected", context =>
                context.ApplicationBuilder.AddContainer("different", "consumer")),
            "returned a resource named 'different'");
    }

    [Fact]
    public void Build_repository_planning_reuses_matching_identities_and_plans_distinct_checkouts()
    {
        using var appHost = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(appHost.Path, ".git"));
        using var otherRepository = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var module = new DistributedApplicationModule(builder, "orders", "1", packageId: null);
        var registry = new ModuleApplicationRegistry(new ModularAppHostsOptions());
        var definitionRepository = new ModuleRepositoryContext(
            appHost.Path,
            appHost.Path,
            Revision: null,
            InitializerOwned: false,
            UsesModuleRepository: true);

        var inherited = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            "api",
            new ModuleImageCommandOptions("api", "docker", "build"),
            configured: null,
            definitionRepository,
            registry,
            moduleOptions: null);
        Assert.Same(definitionRepository, inherited);

        var sameIdentity = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            "same",
            new ModuleImageCommandOptions("same", "docker", "build")
            {
                BuildRepository = "."
            },
            configured: null,
            definitionRepository,
            registry,
            moduleOptions: null);
        Assert.Same(definitionRepository, sameIdentity);

        var local = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            "local",
            new ModuleImageCommandOptions("local", "docker", "build")
            {
                BuildRepository = otherRepository.Path
            },
            configured: null,
            definitionRepository,
            registry,
            moduleOptions: null);
        Assert.Equal(otherRepository.Path, local.RepositoryPath);
        Assert.False(local.InitializerOwned);
        Assert.False(local.UsesModuleRepository);

        var configuredLocal = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            "configured",
            new ModuleImageCommandOptions("configured", "docker", "build")
            {
                BuildRepository = "https://github.com/example/ignored.git"
            },
            new DistributedApplicationModuleContainerOptions
            {
                BuildRepository = otherRepository.Path,
                BuildRepositoryRevision = " "
            },
            definitionRepository,
            registry,
            moduleOptions: null);
        Assert.Equal(otherRepository.Path, configuredLocal.RepositoryPath);
        Assert.False(configuredLocal.InitializerOwned);

        var moduleOptions = new DistributedApplicationModuleOptions
        {
            UpdateRepositoryOnInitialize = false
        };
        var remote = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            "remote",
            new ModuleImageCommandOptions("remote", "docker", "build")
            {
                BuildRepository = "https://github.com/example/build-inputs.git"
            },
            configured: null,
            definitionRepository,
            registry,
            moduleOptions);
        Assert.True(remote.InitializerOwned);
        Assert.False(remote.UsesModuleRepository);
        Assert.Equal("https://github.com/example/build-inputs.git", remote.Repository);
        Assert.False(Assert.Single(registry.RepositoryPlans!.Requirements).UpdateOnInitialize);

        var pinnedLocal = ModuleMaterializationPlanning.ResolveBuildRepository(
            builder,
            module,
            "pinned-local",
            new ModuleImageCommandOptions("pinned-local", "docker", "build")
            {
                BuildRepository = otherRepository.Path,
                BuildRepositoryRevision = "v1"
            },
            configured: null,
            definitionRepository,
            registry,
            moduleOptions: null);
        Assert.True(pinnedLocal.InitializerOwned);
        Assert.Equal("v1", pinnedLocal.Revision);
        Assert.Equal(2, registry.RepositoryPlans.Requirements.Count);
    }

    [Fact]
    public void Repository_identity_matching_handles_local_remote_and_mixed_repositories()
    {
        using var baseDirectory = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(baseDirectory.Path, "first"));
        Directory.CreateDirectory(Path.Combine(baseDirectory.Path, "second"));

        Assert.True(RepositoryIdentity.AreEquivalent(
            "first",
            Path.Combine(baseDirectory.Path, "first"),
            baseDirectory.Path));
        Assert.False(RepositoryIdentity.AreEquivalent(
            "first",
            "second",
            baseDirectory.Path));
        Assert.True(RepositoryIdentity.AreEquivalent(
            "https://github.com/example/orders.git",
            "git@github.com:example/orders.git",
            baseDirectory.Path));
        Assert.False(RepositoryIdentity.AreEquivalent(
            "https://github.com/example/orders.git",
            "https://github.com/example/catalog.git",
            baseDirectory.Path));
        Assert.False(RepositoryIdentity.AreEquivalent(
            "first",
            "https://github.com/example/orders.git",
            baseDirectory.Path));
    }

    [Theory]
    [InlineData(PublishOverrideField.PublishCommand)]
    [InlineData(PublishOverrideField.PublishArguments)]
    [InlineData(PublishOverrideField.PublishWorkingDirectory)]
    [InlineData(PublishOverrideField.ProducedImageReference)]
    [InlineData(PublishOverrideField.PullBeforeBuild)]
    [InlineData(PublishOverrideField.BuildRepository)]
    [InlineData(PublishOverrideField.BuildRepositoryRevision)]
    [InlineData(PublishOverrideField.RefreshBuildRepositoryOnRun)]
    [InlineData(PublishOverrideField.PublishImage)]
    public void Container_publish_overrides_require_a_declared_publisher(PublishOverrideField field)
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
        {
            var module = new DistributedApplicationModuleOptions();
            var container = new DistributedApplicationModuleContainerOptions();
            module.Containers["cache"] = container;
            options.Modules["orders"] = module;
            switch (field)
            {
                case PublishOverrideField.PublishCommand:
                    container.PublishCommand = "docker";
                    break;
                case PublishOverrideField.PublishArguments:
                    container.PublishArguments = ["build"];
                    break;
                case PublishOverrideField.PublishWorkingDirectory:
                    container.PublishWorkingDirectory = ".";
                    break;
                case PublishOverrideField.ProducedImageReference:
                    container.ProducedImageReference = "cache:legacy";
                    break;
                case PublishOverrideField.PullBeforeBuild:
                    container.PullBeforeBuild = true;
                    break;
                case PublishOverrideField.BuildRepository:
                    container.BuildRepository = appHost.Path;
                    break;
                case PublishOverrideField.BuildRepositoryRevision:
                    container.BuildRepositoryRevision = "main";
                    break;
                case PublishOverrideField.RefreshBuildRepositoryOnRun:
                    container.RefreshBuildRepositoryOnRun = true;
                    break;
                case PublishOverrideField.PublishImage:
                    container.PublishImage = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field));
            }
        });
        var module = builder.ExportModule(
            "orders",
            definition => definition.AddContainer("cache", "redis"));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddModule(module));

        Assert.Contains("does not call WithImagePublishCommand", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_configuration_rejects_unknown_project_and_container_resources()
    {
        using var projectAppHost = TemporaryDirectory.Create();
        var projectBuilder = CreateBuilder(projectAppHost.Path);
        projectBuilder.ConfigureModularAppHosts(options =>
        {
            var module = new DistributedApplicationModuleOptions();
            module.Projects["missing-project"] = new DistributedApplicationModuleProjectOptions();
            options.Modules["orders"] = module;
        });
        var missingProject = Assert.Throws<InvalidOperationException>(() =>
            projectBuilder.ExportModule("orders", definition =>
                definition.AddContainer("cache", "redis")));
        Assert.Contains("Available projects: (none)", missingProject.Message, StringComparison.Ordinal);

        using var containerAppHost = TemporaryDirectory.Create();
        var containerBuilder = CreateBuilder(containerAppHost.Path);
        containerBuilder.ConfigureModularAppHosts(options =>
        {
            var module = new DistributedApplicationModuleOptions();
            module.Containers["missing-container"] = new DistributedApplicationModuleContainerOptions();
            options.Modules["orders"] = module;
        });
        var missingContainer = Assert.Throws<InvalidOperationException>(() =>
            containerBuilder.ExportModule("orders", definition =>
                definition.AddContainer("cache", "redis")));
        Assert.Contains("Available containers: 'cache'", missingContainer.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InvalidConfiguration.GlobalProjectMode)]
    [InlineData(InvalidConfiguration.ModuleProjectMode)]
    [InlineData(InvalidConfiguration.ProjectProjectMode)]
    [InlineData(InvalidConfiguration.ProjectPullPolicy)]
    [InlineData(InvalidConfiguration.ContainerPullPolicy)]
    [InlineData(InvalidConfiguration.ProjectDigest)]
    [InlineData(InvalidConfiguration.ContainerDigest)]
    public void Invalid_module_configuration_is_rejected_synchronously(InvalidConfiguration invalid)
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.ConfigureModularAppHosts(options => ConfigureInvalidOptions(options, invalid)));

        Assert.Contains("Aspire:ModularAppHosts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_container_registry_digest_and_pull_policy_are_applied_during_materialization()
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        builder.ConfigureModularAppHosts(options =>
        {
            var module = new DistributedApplicationModuleOptions();
            module.Containers["cache"] = new DistributedApplicationModuleContainerOptions
            {
                ImageRegistry = "registry.example.test",
                ImageSHA256 = $"sha256:{new string('a', 64)}",
                ImagePullPolicy = ImagePullPolicy.Always
            };
            options.Modules["orders"] = module;
        });
        var module = builder.ExportModule(
            "orders",
            definition => definition.AddContainer("cache", "redis"));

        builder.AddModule(module);

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal(new string('a', 64), image.SHA256);
    }

    private static void AssertMaterializationFailure(
        string moduleName,
        Action<IDistributedApplicationModuleBuilder> define,
        string expectedMessage)
    {
        using var appHost = TemporaryDirectory.Create();
        var builder = CreateBuilder(appHost.Path);
        var module = builder.ExportModule(moduleName, define);

        var exception = Assert.ThrowsAny<Exception>(() => builder.AddModule(module));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    private static IDistributedApplicationBuilder CreateBuilder(string projectDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(SynchronousModuleCoreCoverageTests).Assembly.FullName,
            ProjectDirectory = projectDirectory,
            DisableDashboard = true
        });

    private static string CreateProject(string directory, string name)
    {
        var projectPath = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return projectPath;
    }

    private static void ConfigureInvalidOptions(
        ModularAppHostsOptions options,
        InvalidConfiguration invalid)
    {
        if (invalid == InvalidConfiguration.GlobalProjectMode)
        {
            options.ProjectMode = (ModuleProjectMode)99;
            return;
        }

        var module = new DistributedApplicationModuleOptions();
        options.Modules["orders"] = module;
        if (invalid == InvalidConfiguration.ModuleProjectMode)
        {
            module.ProjectMode = (ModuleProjectMode)99;
            return;
        }

        if (invalid is InvalidConfiguration.ProjectProjectMode or
            InvalidConfiguration.ProjectPullPolicy or
            InvalidConfiguration.ProjectDigest)
        {
            var project = new DistributedApplicationModuleProjectOptions();
            module.Projects["api"] = project;
            if (invalid == InvalidConfiguration.ProjectProjectMode)
            {
                project.ProjectMode = (ModuleProjectMode)99;
            }
            else if (invalid == InvalidConfiguration.ProjectPullPolicy)
            {
                project.ImagePullPolicy = (ImagePullPolicy)99;
            }
            else
            {
                project.ImageSHA256 = "sha256:ABC";
            }

            return;
        }

        var container = new DistributedApplicationModuleContainerOptions();
        module.Containers["cache"] = container;
        if (invalid == InvalidConfiguration.ContainerPullPolicy)
        {
            container.ImagePullPolicy = (ImagePullPolicy)99;
        }
        else
        {
            container.ImageSHA256 = "not-a-digest";
        }
    }

    public enum AliasFailure
    {
        Unknown,
        Empty,
        Duplicate
    }

    public enum PublishOverrideField
    {
        PublishCommand,
        PublishArguments,
        PublishWorkingDirectory,
        ProducedImageReference,
        PullBeforeBuild,
        BuildRepository,
        BuildRepositoryRevision,
        RefreshBuildRepositoryOnRun,
        PublishImage
    }

    public enum InvalidConfiguration
    {
        GlobalProjectMode,
        ModuleProjectMode,
        ProjectProjectMode,
        ProjectPullPolicy,
        ContainerPullPolicy,
        ProjectDigest,
        ContainerDigest
    }

    private sealed class ModuleOptions
    {
        public string Region { get; set; } = "default";
    }

    private sealed class TestModuleReference(IDistributedApplicationModule module)
        : DistributedApplicationModuleReference(module);
}
