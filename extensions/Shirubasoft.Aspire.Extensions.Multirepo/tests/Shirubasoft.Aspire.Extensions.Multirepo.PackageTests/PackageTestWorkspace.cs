using Xunit;

[assembly: AssemblyFixture(typeof(Shirubasoft.Aspire.Extensions.Multirepo.PackageTests.PackageTestWorkspace))]

namespace Shirubasoft.Aspire.Extensions.Multirepo.PackageTests;

public sealed class PackageTestWorkspace : IDisposable
{
    private readonly string _rootPath;

    public PackageTestWorkspace()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "aspire-modular-package-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

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
