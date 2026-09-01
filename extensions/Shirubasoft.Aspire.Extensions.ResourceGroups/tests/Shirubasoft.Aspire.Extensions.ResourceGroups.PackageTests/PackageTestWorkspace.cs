using Xunit;

[assembly: AssemblyFixture(typeof(Shirubasoft.Aspire.Extensions.ResourceGroups.PackageTests.PackageTestWorkspace))]

namespace Shirubasoft.Aspire.Extensions.ResourceGroups.PackageTests;

public sealed class PackageTestWorkspace : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "shirubasoft-aspire-resource-groups-package-tests",
        Guid.NewGuid().ToString("N"));

    public PackageTestWorkspace()
    {
        Directory.CreateDirectory(_rootPath);
        PackageArtifactsPath = CreateDirectory("package-artifacts");
    }

    public string PackageArtifactsPath { get; }

    public string CreateDirectory(string name)
    {
        var path = Path.Combine(_rootPath, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
