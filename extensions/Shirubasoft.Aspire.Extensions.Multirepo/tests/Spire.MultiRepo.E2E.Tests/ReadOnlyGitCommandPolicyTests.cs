using Spire.MultiRepo.E2E.Support;
using Xunit;
using SupportProgram = Spire.MultiRepo.E2E.Support.Program;

namespace Spire.MultiRepo.E2E.Tests;

[Collection(Spire.MultiRepo.E2E.Tests.MultiRepositoryE2ECollection.Name)]
public sealed class ReadOnlyGitCommandPolicyTests
{
    public static TheoryData<string[]> AllowedCommands
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add(["branch", "--show-current"]);
            data.Add(["-C", "/repository", "rev-parse", "--show-toplevel"]);
            data.Add(["status", "--porcelain", "--untracked-files=normal"]);
            data.Add(["-C", "/repository", "status", "--porcelain=v1", "--untracked-files=all"]);
            data.Add(["diff", "--cached", "--name-only", "-z", "--no-ext-diff", "HEAD", "--"]);
            data.Add(["ls-files", "--others", "--exclude-standard", "-z", "--"]);
            data.Add(["rev-parse", "0123456789abcdef^{commit}"]);
            return data;
        }
    }

    public static TheoryData<string[]> DeniedCommands
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add(["clean", "-fdx"]);
            data.Add(["restore", "."]);
            data.Add(["stash", "push"]);
            data.Add(["update-ref", "refs/heads/main", "HEAD"]);
            data.Add(["config", "user.name", "attacker"]);
            data.Add(["remote", "set-url", "origin", "https://example.test/repository.git"]);
            data.Add(["rev-parse", "--exec-path"]);
            data.Add(["unknown-command"]);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllowedCommands))]
    public void Allows_only_documented_inspection_shapes(string[] arguments)
    {
        Assert.True(ReadOnlyGitCommandPolicy.IsAllowed(arguments));
    }

    [Theory]
    [MemberData(nameof(DeniedCommands))]
    public void Denies_mutations_and_unknown_shapes(string[] arguments)
    {
        Assert.False(ReadOnlyGitCommandPolicy.IsAllowed(arguments));
    }

    [Fact]
    public async Task Proxy_rejects_and_records_every_unrecognized_read_only_invocation()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"git-proxy-{Guid.NewGuid():N}.jsonl");
        var originalLog = Environment.GetEnvironmentVariable(SupportProgram.GitProxy.LogEnvironmentVariable);
        var originalPolicy = Environment.GetEnvironmentVariable(SupportProgram.GitProxy.PolicyEnvironmentVariable);
        var originalError = Console.Error;
        using var error = new StringWriter();
        Environment.SetEnvironmentVariable(SupportProgram.GitProxy.LogEnvironmentVariable, logPath);
        Environment.SetEnvironmentVariable(
            SupportProgram.GitProxy.PolicyEnvironmentVariable,
            SupportProgram.GitProxyPolicy.ReadOnly.ToString());
        Console.SetError(error);
        try
        {
            var exitCode = await SupportProgram.GitProxy.RunAsync(
                ["unknown-command", "unexpected-argument"],
                TestContext.Current.CancellationToken);

            Assert.Equal(97, exitCode);
            Assert.Contains("unknown-command", await File.ReadAllTextAsync(
                logPath,
                TestContext.Current.CancellationToken));
            Assert.Contains("denied unrecognized invocation", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unexpected-argument", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable(SupportProgram.GitProxy.LogEnvironmentVariable, originalLog);
            Environment.SetEnvironmentVariable(SupportProgram.GitProxy.PolicyEnvironmentVariable, originalPolicy);
            File.Delete(logPath);
        }
    }

}
