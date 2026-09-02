using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;
using Xunit;
using CliCommand = global::CliWrap.Cli;

namespace Shirubasoft.Aspire.Extensions.Multirepo.PackageTests;

public sealed class PackedPackageContractTests
{
    private const string CorePackageId = "Shirubasoft.Aspire.Extensions.Multirepo";
    private const string TestingPackageId = "Shirubasoft.Aspire.Extensions.Multirepo.Testing";
    private const string ToolPackageId = "Shirubasoft.Aspire.Extensions.Multirepo.Tool";
    private const string TemplatePackageId = "Shirubasoft.Aspire.Extensions.Multirepo.Templates";
    private const string MinimumSupportedSdkVersion = "10.0.100";
    private static readonly SemaphoreSlim PackageBuildLock = new(1, 1);
    private static PackageArtifacts? _packageArtifacts;
    private readonly PackageTestWorkspace _workspace;

    public PackedPackageContractTests(PackageTestWorkspace workspace)
    {
        _workspace = workspace;
    }

    [Fact]
    public async Task Core_package_excludes_testing_and_Docker_dependencies()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);

        var dependencies = ReadDependencies(packages.CorePackagePath);

        Assert.Contains(dependencies, dependency => dependency.Id == "Aspire.Hosting");
        Assert.Contains(dependencies, dependency => dependency.Id == "CliWrap");
        Assert.Contains(dependencies, dependency => dependency.Id == "Microsoft.Extensions.Configuration.Binder");
        Assert.Contains(dependencies, dependency => dependency.Id == "Microsoft.Extensions.Options");
        Assert.DoesNotContain(dependencies, dependency => dependency.Id == "Aspire.Hosting.Testing");
        Assert.DoesNotContain(dependencies, dependency => dependency.Id == "Aspire.Hosting.Docker");
        Assert.DoesNotContain(dependencies, dependency => dependency.Id == TestingPackageId);
    }

    [Fact]
    public async Task Testing_package_declares_required_dependencies()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);

        var dependencies = ReadDependencies(packages.TestingPackagePath);

        var core = Assert.Single(dependencies, dependency => dependency.Id == CorePackageId);
        Assert.Contains(packages.Version, core.Version, StringComparison.Ordinal);
        Assert.Contains(dependencies, dependency => dependency.Id == "Aspire.Hosting.Docker");
        Assert.Contains(dependencies, dependency => dependency.Id == "Aspire.Hosting.Testing");
        Assert.Contains(dependencies, dependency => dependency.Id == "CliWrap");
    }

    [Fact]
    public async Task Packages_publish_license_repository_and_debugging_metadata()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);

        foreach (var packagePath in new[]
                 {
                     packages.CorePackagePath,
                     packages.TestingPackagePath,
                     packages.ToolPackagePath,
                     packages.TemplatePackagePath
                 })
        {
            var metadata = ReadMetadata(packagePath);
            Assert.Equal("MIT", metadata.LicenseExpression);
            Assert.Equal("https://github.com/Shirubasoft/aspire-extensions.git", metadata.RepositoryUrl);
            Assert.False(string.IsNullOrWhiteSpace(metadata.RepositoryCommit));
        }

        Assert.True(File.Exists(packages.CoreSymbolPackagePath));
        Assert.True(File.Exists(packages.TestingSymbolPackagePath));
        Assert.True(File.Exists(packages.ToolSymbolPackagePath));
    }

    [Fact]
    public async Task Package_version_is_embedded_in_every_shipped_assembly()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        var expectedVersion = new Version(8, 7, 6, 0);

        Assert.Equal(expectedVersion, ReadAssemblyVersion(
            packages.CorePackagePath,
            "lib/net10.0/Shirubasoft.Aspire.Extensions.Multirepo.dll",
            packages.OutputPath));
        Assert.Equal(expectedVersion, ReadAssemblyVersion(
            packages.CorePackagePath,
            "analyzers/dotnet/cs/Shirubasoft.Aspire.Extensions.Multirepo.Generators.dll",
            packages.OutputPath));
        Assert.Equal(expectedVersion, ReadAssemblyVersion(
            packages.TestingPackagePath,
            "lib/net10.0/Shirubasoft.Aspire.Extensions.Multirepo.Testing.dll",
            packages.OutputPath));
        Assert.Equal(expectedVersion, ReadAssemblyVersion(
            packages.ToolPackagePath,
            "tools/net10.0/any/Shirubasoft.Aspire.Extensions.Multirepo.Tool.dll",
            packages.OutputPath));
        Assert.StartsWith(packages.Version, ReadInformationalVersion(
            packages.CorePackagePath,
            "lib/net10.0/Shirubasoft.Aspire.Extensions.Multirepo.dll"),
            StringComparison.Ordinal);
        Assert.StartsWith(packages.Version, ReadInformationalVersion(
            packages.CorePackagePath,
            "analyzers/dotnet/cs/Shirubasoft.Aspire.Extensions.Multirepo.Generators.dll"),
            StringComparison.Ordinal);
        Assert.StartsWith(packages.Version, ReadInformationalVersion(
            packages.TestingPackagePath,
            "lib/net10.0/Shirubasoft.Aspire.Extensions.Multirepo.Testing.dll"),
            StringComparison.Ordinal);
        Assert.StartsWith(packages.Version, ReadInformationalVersion(
            packages.ToolPackagePath,
            "tools/net10.0/any/Shirubasoft.Aspire.Extensions.Multirepo.Tool.dll"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Packed_tool_installs_launches_and_applies_a_manifest()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        var toolPath = _workspace.CreateDirectory("installed-tool");
        var nugetConfig = Path.Combine(toolPath, "NuGet.Config");
        new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add",
                        new XAttribute("key", "package-tests"),
                        new XAttribute("value", packages.OutputPath)),
                    new XElement("add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json")))))
            .Save(nugetConfig);
        await RunDotNetAsync(
            packages.RepositoryRoot,
            TestContext.Current.CancellationToken,
            "tool",
            "install",
            ToolPackageId,
            "--version",
            packages.Version,
            "--tool-path",
            toolPath,
            "--configfile",
            nugetConfig);
        var executable = Path.Combine(
            toolPath,
            OperatingSystem.IsWindows() ? "modular-apphosts.exe" : "modular-apphosts");

        var help = await RunCommandAsync(
            executable,
            ["images", "--help"],
            toolPath,
            TestContext.Current.CancellationToken);

        Assert.True(help.IsSuccess, help.StandardError);
        Assert.Contains("publish", help.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("apply", help.StandardOutput, StringComparison.Ordinal);
        var workflowHelp = await RunCommandAsync(
            executable,
            ["workflow", "dispatch", "--help"],
            toolPath,
            TestContext.Current.CancellationToken);
        Assert.True(workflowHelp.IsSuccess, workflowHelp.StandardError);
        Assert.Contains("--repository", workflowHelp.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--workflow-document", workflowHelp.StandardOutput, StringComparison.Ordinal);

        var probeProject = Path.Combine(toolPath, "EnvironmentProbe.proj");
        await File.WriteAllTextAsync(
            probeProject,
            """
            <Project>
              <Target Name="PrintManifestConfiguration">
                <Message Importance="high" Text="registry=$(Aspire__ModularAppHosts__Modules__orders__Projects__api__ImageRegistry)" />
                <Message Importance="high" Text="tag=$(Aspire__ModularAppHosts__Modules__orders__Projects__api__ImageTag)" />
                <Message Importance="high" Text="publish=$(Aspire__ModularAppHosts__Modules__orders__Projects__api__PublishImage)" />
              </Target>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        const string workflowDocument =
            "{\"schemaVersion\":1,\"images\":[{\"module\":\"orders\",\"resource\":\"api\"," +
            "\"resourceKind\":\"project\",\"registry\":\"registry.example.test\"," +
            "\"repository\":\"acme/orders\",\"tag\":\"candidate\",\"digest\":null}]}";
        var apply = await RunCommandAsync(
            executable,
            [
                "images", "apply", "--json", workflowDocument,
                "--", "dotnet", "msbuild", probeProject,
                "-target:PrintManifestConfiguration", "-verbosity:minimal", "-nologo"
            ],
            toolPath,
            TestContext.Current.CancellationToken);

        Assert.True(apply.IsSuccess, apply.StandardError);
        Assert.Contains("registry=registry.example.test", apply.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("tag=candidate", apply.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("publish=False", apply.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_package_ships_its_command_and_workflow_reference()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);

        var readme = ReadTextEntry(packages.ToolPackagePath, "README.md");

        Assert.Contains("## Producer: publish images", readme, StringComparison.Ordinal);
        Assert.Contains("## Consumer: apply images", readme, StringComparison.Ordinal);
        Assert.Contains("workflow dispatch", readme, StringComparison.Ordinal);
        Assert.Contains("## Module image workflow document contract", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Define an Aspire resource graph once", readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Core_package_ships_generator_symbols_with_the_analyzer()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        const string generatorPdb =
            "analyzers/dotnet/cs/Shirubasoft.Aspire.Extensions.Multirepo.Generators.pdb";

        Assert.True(ContainsEntry(packages.CorePackagePath, generatorPdb));
        Assert.True(ContainsEntry(packages.CoreSymbolPackagePath, generatorPdb));
    }

    [Fact]
    public async Task Core_package_generator_targets_the_minimum_supported_compiler()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        const string generatorAssembly =
            "analyzers/dotnet/cs/Shirubasoft.Aspire.Extensions.Multirepo.Generators.dll";

        Assert.Equal(
            new Version(5, 0, 0, 0),
            ReadAssemblyReferenceVersion(packages.CorePackagePath, generatorAssembly, "Microsoft.CodeAnalysis"));
        Assert.Equal(
            new Version(5, 0, 0, 0),
            ReadAssemblyReferenceVersion(packages.CorePackagePath, generatorAssembly, "Microsoft.CodeAnalysis.CSharp"));
    }

    [Fact]
    public async Task Packed_core_package_compiles_a_source_generator_consumer()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        const string source = """
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            namespace PackedCoreConsumer;

            [GenerateDistributedApplicationModule("orders")]
            public static partial class OrdersModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddProject("orders-api", "Orders.Api.csproj", ModuleProjectPathBase.Repository)
                        .ExportAsContainerWithCommand(
                            new ModuleImageCommandOptions("orders-api", "dotnet", "publish"));
                    module.AddResource<ContainerResource>(
                        "orders-database",
                        context => context.ApplicationBuilder.AddContainer(context.ResourceName, "database"),
                        new ModuleImageCommandOptions("orders-database", "dotnet", "publish")
                        {
                            ProducedImageReference = "orders-database:legacy",
                            PullBeforeBuild = true
                        });
                }
            }

            public static class Contract
            {
                public static System.Type GeneratedModuleType => typeof(OrdersModule.Module);

                public static OrdersModule.Module Import(
                    Aspire.Hosting.IDistributedApplicationBuilder builder) =>
                    builder.ImportOrdersModule();

                public static ModularAppHostsOptions InitializationOptions => new()
                {
                    UpdateRepositoriesOnInitialize = true,
                    GitHubCliPath = "gh"
                };

                public static DistributedApplicationModuleOptions ModuleOptions => new()
                {
                    UpdateRepositoryOnInitialize = true
                };

                public static string ContainerRuntimePlaceholder =>
                    ModuleImageCommandOptions.ContainerRuntimePlaceholder;

                public static IResourceBuilder<IResourceWithEndpoints> GetApi(OrdersModule.Module module) =>
                    module.OrdersApi;
            }
            """;

        await BuildConsumerAsync(
            packages,
            "CoreConsumer",
            CorePackageId,
            source,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Packed_core_package_compiles_a_generated_consumer_with_the_minimum_sdk()
    {
        Assert.SkipUnless(
            await IsMinimumSdkFeatureBandInstalledAsync(TestContext.Current.CancellationToken),
            $"The .NET SDK {MinimumSupportedSdkVersion} feature band is not installed.");
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        const string source = """
            using Aspire.Hosting;

            namespace MinimumSdkConsumer;

            [GenerateDistributedApplicationModule("compatible")]
            public static partial class CompatibleModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("cache", "redis");
                }

                public static System.Type GeneratedModuleType => typeof(Module);
            }
            """;

        await BuildConsumerAsync(
            packages,
            "MinimumSdkCoreConsumer",
            CorePackageId,
            source,
            TestContext.Current.CancellationToken,
            MinimumSupportedSdkVersion);
    }

    [Fact]
    public async Task Packed_testing_package_compiles_a_Compose_testing_consumer()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        const string source = """
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting.Docker;
            using Aspire.Hosting.Testing;

            namespace PackedTestingConsumer;

            public static class Contract
            {
                public static IDistributedApplicationTestingBuilder Create(string configurationFile) =>
                    DockerComposeDeploymentTestingBuilder.Create<EntryPoint>(configurationFile);

                public static IResourceBuilder<DockerComposeEnvironmentResource> ExportEndpoint(
                    IResourceBuilder<DockerComposeEnvironmentResource> environment,
                    EndpointReference endpoint) =>
                    environment.WithTestEndpoint("api", endpoint, healthCheckPath: "/health");
            }

            public sealed class EntryPoint;
            """;

        await BuildConsumerAsync(
            packages,
            "TestingConsumer",
            TestingPackageId,
            source,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Module_item_template_scaffolds_a_named_versioned_contract()
    {
        var packages = await GetPackagesAsync(TestContext.Current.CancellationToken);
        var workingDirectory = _workspace.CreateDirectory("template");
        var outputPath = Path.Combine(workingDirectory, "output");
        var defaultNamespaceOutputPath = Path.Combine(workingDirectory, "default-output");
        var hivePath = Path.Combine(workingDirectory, "hive");

        await RunDotNetAsync(
            packages.RepositoryRoot,
            TestContext.Current.CancellationToken,
            "new",
            "install",
            packages.TemplatePackagePath,
            "--debug:custom-hive",
            hivePath);
        await RunDotNetAsync(
            packages.RepositoryRoot,
            TestContext.Current.CancellationToken,
            "new",
            "aspire-module",
            "--name",
            "InventoryModule",
            "--moduleName",
            "inventory",
            "--namespace",
            "Inventory.Modules",
            "--output",
            outputPath,
            "--debug:custom-hive",
            hivePath);
        await RunDotNetAsync(
            packages.RepositoryRoot,
            TestContext.Current.CancellationToken,
            "new",
            "aspire-module",
            "--name",
            "DefaultModule",
            "--moduleName",
            "defaults",
            "--output",
            defaultNamespaceOutputPath,
            "--debug:custom-hive",
            hivePath);

        var sourcePath = Path.Combine(outputPath, "InventoryModule.cs");
        Assert.True(File.Exists(sourcePath));
        var source = await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken);
        Assert.Contains("namespace Inventory.Modules;", source, StringComparison.Ordinal);
        Assert.Contains("partial class InventoryModule", source, StringComparison.Ordinal);
        Assert.Contains("public const string Name = \"inventory\";", source, StringComparison.Ordinal);
        Assert.Contains("Version = \"1\"", source, StringComparison.Ordinal);
        Assert.Contains("module.AddContainer(ApiResourceName, \"nginx\", \"alpine\")", source, StringComparison.Ordinal);
        Assert.Contains("targetPort: 80", source, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-module-name", source, StringComparison.Ordinal);

        var defaultNamespaceSource = await File.ReadAllTextAsync(
            Path.Combine(defaultNamespaceOutputPath, "DefaultModule.cs"),
            TestContext.Current.CancellationToken);
        Assert.Contains("namespace Aspire.Modules;", defaultNamespaceSource, StringComparison.Ordinal);
    }

    private async Task<PackageArtifacts> GetPackagesAsync(CancellationToken cancellationToken)
    {
        if (_packageArtifacts is not null)
        {
            return _packageArtifacts;
        }

        await PackageBuildLock.WaitAsync(cancellationToken);
        try
        {
            if (_packageArtifacts is not null)
            {
                return _packageArtifacts;
            }

            var repositoryRoot = FindRepositoryRoot();
            var outputPath = _workspace.CreateDirectory("packages");
            var version = $"8.7.6-package-tests-{Guid.NewGuid():N}";

            await RunDotNetAsync(
                repositoryRoot,
                cancellationToken,
                "pack",
                "src/Shirubasoft.Aspire.Extensions.Multirepo/Shirubasoft.Aspire.Extensions.Multirepo.csproj",
                "--configuration",
                "Release",
                "--no-restore",
                "--output",
                outputPath,
                $"-p:Version={version}");
            await RunDotNetAsync(
                repositoryRoot,
                cancellationToken,
                "pack",
                "src/Shirubasoft.Aspire.Extensions.Multirepo.Tool/Shirubasoft.Aspire.Extensions.Multirepo.Tool.csproj",
                "--configuration",
                "Release",
                "--no-restore",
                "--output",
                outputPath,
                $"-p:Version={version}");
            await RunDotNetAsync(
                repositoryRoot,
                cancellationToken,
                "pack",
                "src/Shirubasoft.Aspire.Extensions.Multirepo.Testing/Shirubasoft.Aspire.Extensions.Multirepo.Testing.csproj",
                "--configuration",
                "Release",
                "--no-restore",
                "--output",
                outputPath,
                $"-p:Version={version}");
            await RunDotNetAsync(
                repositoryRoot,
                cancellationToken,
                "pack",
                "templates/Shirubasoft.Aspire.Extensions.Multirepo.Templates.csproj",
                "--configuration",
                "Release",
                "--no-restore",
                "--output",
                outputPath,
                $"-p:Version={version}");

            return _packageArtifacts = new PackageArtifacts(
                repositoryRoot,
                outputPath,
                version,
                Path.Combine(outputPath, $"{CorePackageId}.{version}.nupkg"),
                Path.Combine(outputPath, $"{TestingPackageId}.{version}.nupkg"),
                Path.Combine(outputPath, $"{ToolPackageId}.{version}.nupkg"),
                Path.Combine(outputPath, $"{TemplatePackageId}.{version}.nupkg"),
                Path.Combine(outputPath, $"{CorePackageId}.{version}.snupkg"),
                Path.Combine(outputPath, $"{TestingPackageId}.{version}.snupkg"),
                Path.Combine(outputPath, $"{ToolPackageId}.{version}.snupkg"));
        }
        finally
        {
            PackageBuildLock.Release();
        }
    }

    private static IReadOnlyList<PackageDependency> ReadDependencies(string packagePath)
    {
        Assert.True(File.Exists(packagePath), $"Package '{packagePath}' was not created.");
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspec = Assert.Single(
            archive.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = nuspec.Open();
        var document = XDocument.Load(stream);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(element => new PackageDependency(
                (string?)element.Attribute("id") ?? string.Empty,
                (string?)element.Attribute("version") ?? string.Empty))
            .ToArray();
    }

    private static PackageMetadata ReadMetadata(string packagePath)
    {
        Assert.True(File.Exists(packagePath), $"Package '{packagePath}' was not created.");
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspec = Assert.Single(
            archive.Entries,
            entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = nuspec.Open();
        var document = XDocument.Load(stream);
        var metadata = Assert.Single(document.Descendants(), element => element.Name.LocalName == "metadata");
        var license = Assert.Single(metadata.Elements(), element => element.Name.LocalName == "license");
        var repository = Assert.Single(metadata.Elements(), element => element.Name.LocalName == "repository");
        return new PackageMetadata(
            license.Value,
            (string?)repository.Attribute("url") ?? string.Empty,
            (string?)repository.Attribute("commit") ?? string.Empty);
    }

    private static bool ContainsEntry(string packagePath, string entryPath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return archive.Entries.Any(entry =>
            string.Equals(entry.FullName, entryPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadTextEntry(string packagePath, string entryPath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = Assert.Single(archive.Entries, entry =>
            string.Equals(entry.FullName, entryPath, StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Version ReadAssemblyVersion(
        string packagePath,
        string entryPath,
        string extractionDirectory)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = Assert.Single(archive.Entries, entry =>
            string.Equals(entry.FullName, entryPath, StringComparison.OrdinalIgnoreCase));
        var extractedPath = Path.Combine(
            extractionDirectory,
            $"{Guid.NewGuid():N}-{Path.GetFileName(entryPath)}");
        entry.ExtractToFile(extractedPath);
        return Assert.IsType<Version>(AssemblyName.GetAssemblyName(extractedPath).Version);
    }

    private static string ReadInformationalVersion(string packagePath, string entryPath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = Assert.Single(archive.Entries, entry =>
            string.Equals(entry.FullName, entryPath, StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        using var assemblyStream = new MemoryStream();
        stream.CopyTo(assemblyStream);
        assemblyStream.Position = 0;
        using var peReader = new PEReader(assemblyStream);
        var metadata = peReader.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        foreach (var attributeHandle in assembly.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(attributeHandle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var attributeType = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (!string.Equals(
                    metadata.GetString(attributeType.Name),
                    nameof(AssemblyInformationalVersionAttribute),
                    StringComparison.Ordinal))
            {
                continue;
            }

            var value = metadata.GetBlobReader(attribute.Value);
            Assert.Equal(1, value.ReadUInt16());
            return Assert.IsType<string>(value.ReadSerializedString());
        }

        throw new InvalidOperationException(
            $"Assembly '{entryPath}' does not declare {nameof(AssemblyInformationalVersionAttribute)}.");
    }

    private static Version ReadAssemblyReferenceVersion(
        string packagePath,
        string entryPath,
        string assemblyName)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = Assert.Single(archive.Entries, entry =>
            string.Equals(entry.FullName, entryPath, StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        using var assemblyStream = new MemoryStream();
        stream.CopyTo(assemblyStream);
        assemblyStream.Position = 0;
        using var peReader = new PEReader(assemblyStream);
        var metadata = peReader.GetMetadataReader();
        foreach (var referenceHandle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(referenceHandle);
            if (string.Equals(metadata.GetString(reference.Name), assemblyName, StringComparison.Ordinal))
            {
                return reference.Version;
            }
        }

        throw new InvalidOperationException(
            $"Assembly '{entryPath}' does not reference '{assemblyName}'.");
    }

    private static async Task BuildConsumerAsync(
        PackageArtifacts packages,
        string name,
        string packageId,
        string source,
        CancellationToken cancellationToken,
        string? sdkVersion = null)
    {
        var projectDirectory = Path.Combine(packages.OutputPath, name);
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, $"{name}.csproj");
        var nugetConfigPath = Path.Combine(projectDirectory, "NuGet.Config");
        File.WriteAllText(projectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{{packageId}}" Version="{{packages.Version}}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Contract.cs"), source);
        if (sdkVersion is not null)
        {
            File.WriteAllText(Path.Combine(projectDirectory, "global.json"), $$"""
                {
                  "sdk": {
                    "version": "{{sdkVersion}}",
                    "rollForward": "latestPatch",
                    "allowPrerelease": false
                  }
                }
                """);
        }

        new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add",
                        new XAttribute("key", "package-tests"),
                        new XAttribute("value", packages.OutputPath)),
                    new XElement("add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json")))))
            .Save(nugetConfigPath);

        await RunDotNetAsync(
            projectDirectory,
            cancellationToken,
            "restore",
            projectPath,
            "--configfile",
            nugetConfigPath);
        await RunDotNetAsync(
            projectDirectory,
            cancellationToken,
            "build",
            projectPath,
            "--configuration",
            "Release",
            "--no-restore");
    }

    private static async Task<bool> IsMinimumSdkFeatureBandInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await CliCommand.Wrap("dotnet")
            .WithArguments("--list-sdks")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        return result.IsSuccess && result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.StartsWith("10.0.1", StringComparison.Ordinal));
    }

    private static async Task RunDotNetAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var result = await CliCommand.Wrap("dotnet")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        Assert.True(
            result.IsSuccess,
            $"dotnet {string.Join(' ', arguments)} failed with exit code {result.ExitCode}.{Environment.NewLine}" +
            result.StandardOutput + Environment.NewLine + result.StandardError);
    }

    private static Task<BufferedCommandResult> RunCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var command = CliCommand.Wrap(executable)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);
        if (environmentVariables is not null)
        {
            command = command.WithEnvironmentVariables(environmentVariables);
        }

        return command.ExecuteBufferedAsync(cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Shirubasoft.Aspire.Extensions.Multirepo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }

    private sealed record PackageArtifacts(
        string RepositoryRoot,
        string OutputPath,
        string Version,
        string CorePackagePath,
        string TestingPackagePath,
        string ToolPackagePath,
        string TemplatePackagePath,
        string CoreSymbolPackagePath,
        string TestingSymbolPackagePath,
        string ToolSymbolPackagePath);

    private sealed record PackageDependency(string Id, string Version);

    private sealed record PackageMetadata(
        string LicenseExpression,
        string RepositoryUrl,
        string RepositoryCommit);
}
