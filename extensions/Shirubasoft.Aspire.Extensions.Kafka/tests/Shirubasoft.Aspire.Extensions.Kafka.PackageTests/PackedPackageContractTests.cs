using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace Shirubasoft.Aspire.Extensions.Kafka.PackageTests;

public sealed class PackedPackageContractTests(PackageTestWorkspace workspace)
{
    private const string PackageId = "Shirubasoft.Aspire.Extensions.Kafka";
    private const string PackageVersion = "8.7.6";
    private static readonly SemaphoreSlim PackageBuildLock = new(1, 1);
    private static PackageArtifacts? _artifacts;

    [Fact]
    public async Task PackageContainsRuntimeDocumentationAndSymbolArtifacts()
    {
        var artifacts = await GetArtifactsAsync(
            workspace.PackageArtifactsPath,
            TestContext.Current.CancellationToken);

        using var package = ZipFile.OpenRead(artifacts.PackagePath);
        using var symbols = ZipFile.OpenRead(artifacts.SymbolPackagePath);

        Assert.Contains(package.Entries, entry => entry.FullName == $"lib/net10.0/{PackageId}.dll");
        Assert.Contains(package.Entries, entry => entry.FullName == $"lib/net10.0/{PackageId}.xml");
        Assert.Contains(package.Entries, entry => entry.FullName == "README.md");
        Assert.Contains(symbols.Entries, entry => entry.FullName == $"lib/net10.0/{PackageId}.pdb");
    }

    [Fact]
    public async Task PackagePublishesIdentityDependenciesAndRepositoryMetadata()
    {
        var artifacts = await GetArtifactsAsync(
            workspace.PackageArtifactsPath,
            TestContext.Current.CancellationToken);
        using var package = ZipFile.OpenRead(artifacts.PackagePath);
        var nuspecEntry = Assert.Single(package.Entries, entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        await using var nuspecStream = nuspecEntry.Open();
        var document = await XDocument.LoadAsync(
            nuspecStream,
            LoadOptions.None,
            TestContext.Current.CancellationToken);
        var metadata = document.Descendants().Single(element => element.Name.LocalName == "metadata");

        Assert.Equal(PackageId, ChildValue(metadata, "id"));
        Assert.Equal(PackageVersion, ChildValue(metadata, "version"));
        Assert.Equal("MIT", ChildValue(metadata, "license"));
        Assert.Equal("README.md", ChildValue(metadata, "readme"));
        var repository = metadata.Elements().Single(element => element.Name.LocalName == "repository");
        Assert.Equal("git", repository.Attribute("type")?.Value);
        Assert.Equal("https://github.com/Shirubasoft/aspire-extensions.git", repository.Attribute("url")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(repository.Attribute("commit")?.Value));
        var dependencies = metadata.Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(element => element.Attribute("id")?.Value)
            .ToArray();
        Assert.Contains("Aspire.Hosting.Kafka", dependencies);
        Assert.Contains("Confluent.Kafka", dependencies);
    }

    [Fact]
    public async Task PackedPackageBuildsAnAppHostConsumer()
    {
        var artifacts = await GetArtifactsAsync(
            workspace.PackageArtifactsPath,
            TestContext.Current.CancellationToken);
        var consumerPath = workspace.CreateDirectory("consumer");
        await File.WriteAllTextAsync(
            Path.Combine(consumerPath, "Consumer.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{{PackageId}}" Version="{{PackageVersion}}" />
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(consumerPath, "Program.cs"),
            """
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);
            var kafka = builder.AddKafka("kafka");
            var registry = kafka.AddSchemaRegistry("schema-registry");
            var topic = kafka.AddTopic("orders");
            builder.AddContainer("consumer", "alpine")
                .WithReference(registry)
                .WaitFor(registry)
                .WaitFor(topic);
            """,
            TestContext.Current.CancellationToken);
        var nugetConfig = Path.Combine(consumerPath, "NuGet.Config");
        new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add",
                        new XAttribute("key", "package-tests"),
                        new XAttribute("value", artifacts.OutputPath)),
                    new XElement("add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json")))))
            .Save(nugetConfig);

        await RunDotNetAsync(
            consumerPath,
            TestContext.Current.CancellationToken,
            "build",
            "Consumer.csproj",
            "--configuration",
            "Release",
            "--configfile",
            nugetConfig);
    }

    private static async Task<PackageArtifacts> GetArtifactsAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        await PackageBuildLock.WaitAsync(cancellationToken);
        try
        {
            if (_artifacts is not null)
            {
                return _artifacts;
            }

            var repositoryRoot = FindRepositoryRoot();
            var projectPath = Path.Combine(
                repositoryRoot,
                "extensions",
                PackageId,
                "src",
                PackageId,
                $"{PackageId}.csproj");

            await RunDotNetAsync(
                repositoryRoot,
                cancellationToken,
                "pack",
                projectPath,
                "--configuration",
                "Release",
                "--output",
                outputPath,
                $"-p:Version={PackageVersion}");

            _artifacts = new PackageArtifacts(
                outputPath,
                Path.Combine(outputPath, $"{PackageId}.{PackageVersion}.nupkg"),
                Path.Combine(outputPath, $"{PackageId}.{PackageVersion}.snupkg"));
            Assert.True(File.Exists(_artifacts.PackagePath));
            Assert.True(File.Exists(_artifacts.SymbolPackagePath));
            return _artifacts;
        }
        finally
        {
            PackageBuildLock.Release();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static async Task RunDotNetAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed.\n{output}\n{error}");
    }

    private static string? ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value;

    private sealed record PackageArtifacts(string OutputPath, string PackagePath, string SymbolPackagePath);
}
