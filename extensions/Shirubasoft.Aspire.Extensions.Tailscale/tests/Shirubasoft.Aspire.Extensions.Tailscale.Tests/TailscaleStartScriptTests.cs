using System.Diagnostics;
using Xunit;

namespace Aspire.Hosting.Tests;

// The same scenarios exercise the host's POSIX shell and the pinned image's
// BusyBox. A stub containerboot exits without running a daemon or registering.
public abstract class TailscaleStartScriptScenarios
{
    protected const int ContainerbootExitCode = 42;
    private const string RegisteredState = "{\n  \"_current-profile\": \"ignored\"\n}\n";

    private protected abstract ScriptRunner CreateRunner();

    [Fact]
    public async Task FreshStartWritesServeConfigAndNormalizedMarkerBeforeContainerboot()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());

        var result = await workspace.RunAsync(" tag:web, tag:apps,tag:web ", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal("{\"serve\":true}", await File.ReadAllTextAsync(workspace.ServeConfigPath, TestContext.Current.CancellationToken));
        Assert.Equal("tag:apps,tag:web\n", await File.ReadAllTextAsync(workspace.MarkerPath, TestContext.Current.CancellationToken));
        Assert.Equal("containerboot\ntag:apps,tag:web\n", result.StandardOutput);
        Assert.Equal(["aspire-tags"], Directory.GetFileSystemEntries(workspace.StateDirectory).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"_machinekey\":\"ignored\"}")]
    public async Task StateWithoutCurrentProfileRewritesMarker(string state)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(state);
        await workspace.WriteMarkerAsync("tag:apps\n");

        var result = await workspace.RunAsync("tag:web", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal("tag:web\n", await File.ReadAllTextAsync(workspace.MarkerPath, TestContext.Current.CancellationToken));
        Assert.Equal(state, await File.ReadAllTextAsync(workspace.StatePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisteredNodeWithMatchingTagsStartsWithoutRewritingMarkerOrState()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(RegisteredState);
        await workspace.WriteMarkerAsync("tag:apps\n");

        var result = await workspace.RunAsync("tag:apps", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal("tag:apps\n", await File.ReadAllTextAsync(workspace.MarkerPath, TestContext.Current.CancellationToken));
        Assert.Equal(RegisteredState, await File.ReadAllTextAsync(workspace.StatePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisteredNodeWithChangedTagsStopsWithoutRewritingMarkerOrState()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(RegisteredState);
        await workspace.WriteMarkerAsync("tag:apps\n");

        var result = await workspace.RunAsync("tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[tag:web]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("delete the state volume", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("{project}_{resource}-ts-state", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("re-register", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("tag:apps\n", await File.ReadAllTextAsync(workspace.MarkerPath, TestContext.Current.CancellationToken));
        Assert.Equal(RegisteredState, await File.ReadAllTextAsync(workspace.StatePath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("tag:web,tag:apps", "tag:apps,tag:web\n")]
    [InlineData("tag:web,tag:apps,tag:web", "tag:web,tag:apps,tag:apps\n")]
    [InlineData(" \ttag:web, tag:apps\r\n", " tag:apps,\n\ttag:web \r\n")]
    public async Task TagOrderDuplicatesAndWhitespaceAreNormalized(string requested, string marker)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync("{\"_current-profile\" : \"ignored\"}");
        await workspace.WriteMarkerAsync(marker);

        var result = await workspace.RunAsync(requested, withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal(marker, await File.ReadAllTextAsync(workspace.MarkerPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisteredNodeWithoutMarkerStopsWithVolumeResetGuidance()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(RegisteredState);

        var result = await workspace.RunAsync("tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("[<missing marker>]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("delete the state volume", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("{project}_{resource}-ts-state", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(workspace.MarkerPath));
        Assert.Equal(RegisteredState, await File.ReadAllTextAsync(workspace.StatePath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public async Task FailedFirstRegistrationDoesNotBlockCorrectedTags(string? state)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var failed = await workspace.RunAsync("tag:apps", withStateDirectory: true);
        Assert.Equal(ContainerbootExitCode, failed.ExitCode);
        Assert.Equal("tag:apps\n", await File.ReadAllTextAsync(workspace.MarkerPath, TestContext.Current.CancellationToken));
        if (state is not null)
        {
            await workspace.WriteStateAsync(state);
        }

        var corrected = await workspace.RunAsync("tag:web", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, corrected.ExitCode);
        Assert.Equal("tag:web\n", await File.ReadAllTextAsync(workspace.MarkerPath, TestContext.Current.CancellationToken));
        Assert.Equal("containerboot\ntag:web\n", corrected.StandardOutput);
    }

    [Fact]
    public async Task StartWithoutStateDirectoryWritesOnlyServeConfig()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());

        var result = await workspace.RunAsync("tag:apps", withStateDirectory: false);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.False(Directory.Exists(workspace.StateDirectory));
        Assert.Equal("{\"serve\":true}", await File.ReadAllTextAsync(workspace.ServeConfigPath, TestContext.Current.CancellationToken));
        Assert.Equal("containerboot\n", result.StandardOutput);
    }
}

public sealed class TailscaleStartScriptTests : TailscaleStartScriptScenarios
{
    private protected override ScriptRunner CreateRunner() => ScriptRunner.HostShell();
}

public sealed class TailscaleStartScriptBusyBoxTests : TailscaleStartScriptScenarios
{
    private protected override ScriptRunner CreateRunner() => ScriptRunner.PinnedImage();
}

internal sealed class ScriptWorkspace : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tailscale-start-script-");
    private readonly ScriptRunner _runner;

    public ScriptWorkspace(ScriptRunner runner)
    {
        _runner = runner;
        var binDirectory = _root.CreateSubdirectory("bin");
        var containerboot = Path.Combine(binDirectory.FullName, "containerboot");
        File.WriteAllText(containerboot, "#!/bin/sh\nprintf 'containerboot\\n'\nif [ -n \"${TS_STATE_DIR:-}\" ]; then cat \"$TS_STATE_DIR/aspire-tags\"; fi\nexit 42\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(containerboot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public string RootDirectory => _root.FullName;

    public string StateDirectory => Path.Combine(_root.FullName, "state");

    public string StatePath => Path.Combine(StateDirectory, "tailscaled.state");

    public string ServeConfigPath => Path.Combine(_root.FullName, "serve.json");

    public string MarkerPath => Path.Combine(StateDirectory, "aspire-tags");

    public Task WriteStateAsync(string state) => WriteAsync(StatePath, state);

    public Task WriteMarkerAsync(string tags) => WriteAsync(MarkerPath, tags);

    private Task WriteAsync(string path, string contents)
    {
        Directory.CreateDirectory(StateDirectory);
        return File.WriteAllTextAsync(path, contents, TestContext.Current.CancellationToken);
    }

    public Task<ScriptResult> RunAsync(string tags, bool withStateDirectory) =>
        _runner.RunAsync(this, tags, withStateDirectory);

    public void Dispose() => _root.Delete(recursive: true);
}

internal sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError);

// Executes the start script either with the host shell or inside the pinned
// image, where /work is the workspace. Both place a fake containerboot first
// on PATH and point TS_SERVE_CONFIG and TS_STATE_DIR into the workspace.
internal sealed class ScriptRunner
{
    private static readonly Lazy<string?> ContainerRuntime = new(DetectContainerRuntime);
    private readonly Func<ScriptWorkspace, string, bool, ProcessStartInfo> _createStartInfo;

    private ScriptRunner(Func<ScriptWorkspace, string, bool, ProcessStartInfo> createStartInfo) =>
        _createStartInfo = createStartInfo;

    public static ScriptRunner HostShell()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "The start script runs under a POSIX shell.");
        return new((workspace, tags, withStateDirectory) =>
        {
            var startInfo = new ProcessStartInfo("sh");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(TailscaleSidecarDefaults.StartScript);
            startInfo.Environment["PATH"] = $"{workspace.RootDirectory}/bin:{Environment.GetEnvironmentVariable("PATH")}";
            startInfo.Environment["TS_SERVE_CONFIG"] = workspace.ServeConfigPath;
            startInfo.Environment["TAILSCALE_SERVE_CONFIG_JSON"] = "{\"serve\":true}";
            startInfo.Environment["TAILSCALE_TAGS"] = tags;
            startInfo.Environment.Remove("TS_STATE_DIR");
            if (withStateDirectory)
            {
                startInfo.Environment["TS_STATE_DIR"] = workspace.StateDirectory;
            }

            return startInfo;
        });
    }

    public static ScriptRunner PinnedImage()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The pinned image runs on a Linux container runtime.");
        var runtime = ContainerRuntime.Value;
        Assert.SkipWhen(runtime is null, "No running Docker or Podman runtime.");
        return new((workspace, tags, withStateDirectory) =>
        {
            var startInfo = new ProcessStartInfo(runtime!);
            foreach (var argument in new[] { "run", "--rm", "--network", "none", "--volume", $"{workspace.RootDirectory}:/work:z" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (runtime == "podman")
            {
                startInfo.ArgumentList.Add("--userns=keep-id");
            }
            else
            {
                startInfo.ArgumentList.Add("--user");
                startInfo.ArgumentList.Add($"{Run("id", "-u").Trim()}:{Run("id", "-g").Trim()}");
            }

            var variables = new Dictionary<string, string>
            {
                ["PATH"] = "/work/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                ["TS_SERVE_CONFIG"] = "/work/serve.json",
                ["TAILSCALE_SERVE_CONFIG_JSON"] = "{\"serve\":true}",
                ["TAILSCALE_TAGS"] = tags,
            };
            if (withStateDirectory)
            {
                variables["TS_STATE_DIR"] = "/work/state";
            }

            foreach (var (name, value) in variables)
            {
                startInfo.ArgumentList.Add("--env");
                startInfo.ArgumentList.Add($"{name}={value}");
            }

            startInfo.ArgumentList.Add("--entrypoint");
            startInfo.ArgumentList.Add("/bin/sh");
            startInfo.ArgumentList.Add(
                $"{TailscaleContainerImageTags.Registry}/{TailscaleContainerImageTags.Image}:{TailscaleContainerImageTags.Tag}");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(TailscaleSidecarDefaults.StartScript);
            return startInfo;
        });
    }

    public async Task<ScriptResult> RunAsync(ScriptWorkspace workspace, string tags, bool withStateDirectory)
    {
        var startInfo = _createStartInfo(workspace, tags, withStateDirectory);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ScriptResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string? DetectContainerRuntime()
    {
        foreach (var runtime in new[] { "docker", "podman" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(runtime, ["info"])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (process is not null && process.WaitForExit(TimeSpan.FromSeconds(30)) && process.ExitCode == 0)
                {
                    return runtime;
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        return null;
    }

    private static string Run(string fileName, string argument)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, [argument]) { RedirectStandardOutput = true })
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
