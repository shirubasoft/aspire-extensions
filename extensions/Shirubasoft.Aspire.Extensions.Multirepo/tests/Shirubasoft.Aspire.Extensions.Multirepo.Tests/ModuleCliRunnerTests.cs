using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using Xunit;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleCliRunnerTests
{
    [Theory]
    [InlineData("https://github.com/acme/orders.git")]
    [InlineData("https://github.example.test/acme/orders.git")]
    [InlineData("git@github.com:acme/orders.git")]
    [InlineData("acme/orders")]
    public void GitHub_repository_detection_accepts_supported_identities(string repository)
    {
        Assert.True(GitHubGitAuthentication.IsGitHubRepository(repository));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.test/acme/orders.git")]
    [InlineData("github.com:acme/orders.git")]
    [InlineData("git@example.test:acme/orders.git")]
    [InlineData("orders")]
    public void GitHub_repository_detection_rejects_other_identities(string? repository)
    {
        Assert.False(GitHubGitAuthentication.IsGitHubRepository(repository));
    }

    [Fact]
    public async Task Runner_streams_and_captures_standard_output_and_error()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var progress = new List<string>();

        var result = await ModuleCliRunner.RunAsync(
            "/bin/sh",
            ["-c", "echo cloning; echo warning >&2"],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(5),
            "test clone",
            TestContext.Current.CancellationToken,
            progress.Add);

        Assert.True(result.IsSuccess);
        Assert.Contains("cloning", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("warning", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("cloning", progress);
        Assert.Contains("warning", progress);
    }

    [Fact]
    public async Task Runner_streams_and_captures_output_without_transforming_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string output = "https://user:password@example.test/acme/orders.git?token=value#fragment";
        var progress = new List<string>();

        var result = await ModuleCliRunner.RunAsync(
            "/bin/sh",
            ["-c", $"printf '%s\\n' '{output}'"],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(5),
            "test raw output",
            TestContext.Current.CancellationToken,
            progress.Add);

        Assert.Equal(output, Assert.Single(progress));
        Assert.Contains(output, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_stops_a_command_at_the_configured_timeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ModuleCliRunner.RunAsync(
                "/bin/sh",
                ["-c", "sleep 10"],
                Path.GetTempPath(),
                TimeSpan.FromMilliseconds(100),
                "slow clone",
                TestContext.Current.CancellationToken));

        Assert.Contains("slow clone", exception.Message, StringComparison.Ordinal);
        Assert.Contains("00:00:00.1000000", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repository_commands_use_the_configured_git_executable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        var command = Assert.Single(await RepositorySynchronizer.CreateCommandsAsync(
            path,
            "https://example.test/acme/orders.git",
            updateRepository: true,
            gitExecutablePath: "custom-git",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("custom-git", command.Executable);
    }

    [Fact]
    public async Task Missing_GitHub_repository_is_cloned_with_Git_and_the_configured_credential_provider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        var command = Assert.Single(await RepositorySynchronizer.CreateCommandsAsync(
            path,
            "https://github.com/acme/orders.git",
            updateRepository: true,
            githubCliPath: "custom-gh",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("git", command.Executable);
        Assert.Contains(
            "credential.https://github.com.helper=!\'custom-gh\' auth git-credential",
            command.Arguments);
        Assert.Equal("clone", command.Arguments[^5]);
        Assert.Equal("--recurse-submodules", command.Arguments[^4]);
        Assert.Equal("--", command.Arguments[^3]);
        Assert.Equal("https://github.com/acme/orders.git", command.Arguments[^2]);
        Assert.Equal(path, command.Arguments[^1]);
    }

    [Fact]
    public async Task GitHub_CLI_credential_helper_is_process_scoped_without_exposing_its_token()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var githubCli = Path.Combine(directory.Path, "fake gh");
        var log = Path.Combine(directory.Path, "gh-arguments.txt");
        const string token = "test-token-that-must-not-be-an-argument";
        await File.WriteAllTextAsync(
            githubCli,
            $$"""
            #!/bin/sh
            printf '%s\n' "$*" > '{{log}}'
            cat >/dev/null
            printf 'username=x-access-token\npassword={{token}}\n'
            """,
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(
            githubCli,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var arguments = GitHubGitAuthentication.ConfigureCredentialHelper(
            ["credential", "fill"],
            "https://github.com/acme/orders.git",
            githubCli);
        var result = await CliCommand.Wrap("git")
            .WithArguments(arguments)
            .WithWorkingDirectory(directory.Path)
            .WithStandardInputPipe(PipeSource.FromString("protocol=https\nhost=github.com\n\n"))
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);
        var helperArguments = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);

        Assert.Contains($"password={token}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("auth git-credential get", helperArguments.Trim());
        Assert.DoesNotContain(token, arguments);
    }
}
