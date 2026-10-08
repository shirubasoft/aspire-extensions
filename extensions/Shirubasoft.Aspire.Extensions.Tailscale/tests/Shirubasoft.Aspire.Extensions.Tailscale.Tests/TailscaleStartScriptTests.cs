using System.Diagnostics;
using Xunit;

namespace Aspire.Hosting.Tests;

// Runs the sidecar start script under sh with a fake containerboot on PATH.
// The script must treat every absolute path it touches as configuration so
// the test can redirect it into a temporary directory.
public sealed class TailscaleStartScriptTests
{
    private const int ContainerbootExitCode = 42;

    [Fact]
    public async Task FirstStartWritesTheServeConfigAndRecordsTheTags()
    {
        using var workspace = new ScriptWorkspace();

        var result = await workspace.RunAsync(tags: "tag:apps", stateDirectory: workspace.StateDirectory);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal("{\"serve\":true}", await File.ReadAllTextAsync(workspace.ServeConfigPath, TestContext.Current.CancellationToken));
        Assert.Equal("tag:apps\n", await File.ReadAllTextAsync(workspace.TagRecordPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RestartWithTheSameTagsStartsContainerboot()
    {
        using var workspace = new ScriptWorkspace();
        await workspace.RecordTagsAsync("tag:apps,tag:web");

        var result = await workspace.RunAsync(tags: "tag:apps,tag:web", stateDirectory: workspace.StateDirectory);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
    }

    [Fact]
    public async Task RestartWithChangedTagsRefusesToStart()
    {
        using var workspace = new ScriptWorkspace();
        await workspace.RecordTagsAsync("tag:apps");

        var result = await workspace.RunAsync(tags: "tag:web", stateDirectory: workspace.StateDirectory);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("tag:apps", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("tag:web", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("admin console", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(workspace.TagRecordPath, result.StandardError, StringComparison.Ordinal);
        Assert.Equal("tag:apps\n", await File.ReadAllTextAsync(workspace.TagRecordPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartWithoutAStateDirectorySkipsTheTagRecord()
    {
        using var workspace = new ScriptWorkspace();

        var result = await workspace.RunAsync(tags: "tag:apps", stateDirectory: null);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.False(File.Exists(workspace.TagRecordPath));
        Assert.True(File.Exists(workspace.ServeConfigPath));
    }

    private sealed class ScriptWorkspace : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tailscale-start-script-");

        public ScriptWorkspace()
        {
            Assert.SkipUnless(
                OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
                "The start script runs under a POSIX shell.");

            var binDirectory = _root.CreateSubdirectory("bin");
            var containerboot = Path.Combine(binDirectory.FullName, "containerboot");
            File.WriteAllText(containerboot, $"#!/bin/sh\nexit {ContainerbootExitCode}\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(containerboot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            BinDirectory = binDirectory.FullName;
            StateDirectory = Path.Combine(_root.FullName, "state");
            ServeConfigPath = Path.Combine(_root.FullName, "serve.json");
        }

        public string BinDirectory { get; }

        public string StateDirectory { get; }

        public string ServeConfigPath { get; }

        public string TagRecordPath => Path.Combine(StateDirectory, "aspire-tags");

        public async Task RecordTagsAsync(string tags)
        {
            Directory.CreateDirectory(StateDirectory);
            await File.WriteAllTextAsync(TagRecordPath, tags + "\n", TestContext.Current.CancellationToken);
        }

        public async Task<ScriptResult> RunAsync(string tags, string? stateDirectory)
        {
            var startInfo = new ProcessStartInfo("sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(TailscaleSidecarDefaults.StartScript);
            startInfo.Environment["PATH"] = $"{BinDirectory}:{Environment.GetEnvironmentVariable("PATH")}";
            startInfo.Environment["TS_SERVE_CONFIG"] = ServeConfigPath;
            startInfo.Environment["TAILSCALE_SERVE_CONFIG_JSON"] = "{\"serve\":true}";
            startInfo.Environment["TAILSCALE_TAGS"] = tags;
            startInfo.Environment.Remove("TS_STATE_DIR");
            if (stateDirectory is not null)
            {
                startInfo.Environment["TS_STATE_DIR"] = stateDirectory;
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start sh.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            return new ScriptResult(process.ExitCode, await standardOutput, await standardError);
        }

        public void Dispose() => _root.Delete(recursive: true);
    }

    private sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError);
}
