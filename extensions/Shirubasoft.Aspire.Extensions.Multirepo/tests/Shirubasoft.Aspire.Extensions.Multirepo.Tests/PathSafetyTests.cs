using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class PathSafetyTests
{
    [Fact]
    public void Containment_uses_the_operating_system_path_comparison()
    {
        using var directory = TemporaryDirectory.Create();
        var root = Path.Combine(directory.Path, "Module");
        Directory.CreateDirectory(root);
        var differentlyCasedPath = Path.Combine(directory.Path, "module", "service.csproj");

        Assert.Equal(OperatingSystem.IsWindows(), PathSafety.IsContainedBy(root, differentlyCasedPath));
    }

    [Fact]
    public void Containment_rejects_parent_traversal()
    {
        using var directory = TemporaryDirectory.Create();
        var root = Path.Combine(directory.Path, "module");
        Directory.CreateDirectory(root);

        Assert.False(PathSafety.IsContainedBy(root, Path.Combine(root, "..", "outside.csproj")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PathSafety.GetContainedPath(root, "../outside.csproj", "path"));
    }

    [Fact]
    public void Containment_preserves_the_filesystem_root()
    {
        using var directory = TemporaryDirectory.Create();
        var root = Assert.IsType<string>(Path.GetPathRoot(directory.Path));

        Assert.True(PathSafety.AreEqual(root, root));
        Assert.True(PathSafety.IsContainedBy(root, directory.Path));
        Assert.Equal(directory.Path, PathSafety.GetContainedPath(root, directory.Path, "path"));
    }

    [Fact]
    public void Containment_rejects_a_symbolic_link_that_escapes_the_repository()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var root = Path.Combine(directory.Path, "module");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

        Assert.False(PathSafety.IsContainedBy(root, Path.Combine(root, "linked", "service.csproj")));
    }

}
