using Aspire.Hosting.Testing;
using Xunit;

namespace Aspire.Hosting.Multirepo.Testing.Tests;

public sealed class DotEnvFileTests
{
    [Fact]
    public void Load_decodes_supported_escapes_and_preserves_unknown_escapes()
    {
        using var file = TemporaryEnvironmentFile.Create(
            """
            VALUE="line\ncarriage\rtab\tquote\"slash\\unknown\q" # comment
            """);

        var values = DotEnvFile.Load(file.Path);

        Assert.Equal(
            "line\ncarriage\rtab\tquote\"slash\\unknown\\q",
            Assert.Single(values).Value);
    }

    [Theory]
    [InlineData("VALUE=\"incomplete\\", "incomplete escape")]
    [InlineData("VALUE=\"value\" trailing", "unexpected trailing content")]
    [InlineData("VALUE=\"unterminated", "not terminated")]
    public void Load_rejects_invalid_double_quoted_values(string content, string expectedDetail)
    {
        using var file = TemporaryEnvironmentFile.Create(content);

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            DotEnvFile.Load(file.Path);
        });

        Assert.Contains(expectedDetail, exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryEnvironmentFile : IDisposable
    {
        private TemporaryEnvironmentFile(string path) => Path = path;

        public string Path { get; }

        public static TemporaryEnvironmentFile Create(string content)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aspire-dotenv-test-{Guid.NewGuid():N}.env");
            File.WriteAllText(path, content);
            return new TemporaryEnvironmentFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
