using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aspire.Hosting.Tests;

// Runs the sidecar start script under sh with a fake containerboot on PATH.
// The script must treat every absolute path it touches as configuration so
// the test can redirect it into a temporary directory. The tailscaled state
// fixtures follow the file store layout: an indented JSON object whose values
// are base64-encoded bytes, with the current profile's prefs stored under
// the key named by "_current-profile".
public sealed class TailscaleStartScriptTests
{
    private const int ContainerbootExitCode = 42;

    [Fact]
    public async Task FirstStartWritesTheServeConfigAndStartsWithoutRecordingAnything()
    {
        using var workspace = new ScriptWorkspace();

        var result = await workspace.RunAsync(tags: "tag:apps", stateDirectory: workspace.StateDirectory);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal("{\"serve\":true}", await File.ReadAllTextAsync(workspace.ServeConfigPath, TestContext.Current.CancellationToken));
        Assert.Equal([], Directory.GetFileSystemEntries(workspace.StateDirectory));
    }

    [Fact]
    public async Task StateWithoutAProfileIsAFreshRegistration()
    {
        using var workspace = new ScriptWorkspace();
        await workspace.WriteStateAsync(currentProfile: null, prefs: null);

        var result = await workspace.RunAsync(tags: "tag:web", stateDirectory: workspace.StateDirectory);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
    }

    // A first boot that failed before registration leaves no identity, so a
    // corrected tag list must start normally. A record from an earlier package
    // version is removed because the profile is now the only source of truth.
    [Fact]
    public async Task FailedFirstRegistrationDoesNotBlockCorrectedTags()
    {
        using var workspace = new ScriptWorkspace();
        Directory.CreateDirectory(workspace.StateDirectory);
        await File.WriteAllTextAsync(workspace.LegacyRecordPath, "tag:apps\n", TestContext.Current.CancellationToken);

        var result = await workspace.RunAsync(tags: "tag:web", stateDirectory: workspace.StateDirectory);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.False(File.Exists(workspace.LegacyRecordPath));
    }

    [Fact]
    public async Task RegisteredNodeWithTheSameTagSetStarts()
    {
        using var workspace = new ScriptWorkspace();
        await workspace.WriteStateAsync("profile-5f3a", ScriptWorkspace.Prefs(["tag:web", "tag:apps"]));

        var result = await workspace.RunAsync(tags: "tag:apps,tag:web,tag:apps", stateDirectory: workspace.StateDirectory);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal(["tailscaled.state"], Directory.GetFileSystemEntries(workspace.StateDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RegisteredNodeWithOtherTagsRefusesToStart()
    {
        using var workspace = new ScriptWorkspace();
        await workspace.WriteStateAsync("profile-5f3a", ScriptWorkspace.Prefs(["tag:apps"]));

        var result = await workspace.RunAsync(tags: "tag:web", stateDirectory: workspace.StateDirectory);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[tag:web]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("admin console", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("state volume", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredNodeWithoutTagsRefusesTaggedStart()
    {
        using var workspace = new ScriptWorkspace();
        await workspace.WriteStateAsync("profile-5f3a", ScriptWorkspace.Prefs(null));

        var result = await workspace.RunAsync(tags: "tag:apps", stateDirectory: workspace.StateDirectory);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableProfileRefusesToStart()
    {
        using var workspace = new ScriptWorkspace();
        await workspace.WriteStateAsync("profile-5f3a", prefs: "not json at all");

        var result = await workspace.RunAsync(tags: "tag:apps", stateDirectory: workspace.StateDirectory);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("tailscaled.state", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartWithoutAStateDirectorySkipsTheProfileCheck()
    {
        using var workspace = new ScriptWorkspace();

        var result = await workspace.RunAsync(tags: "tag:apps", stateDirectory: null);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.False(Directory.Exists(workspace.StateDirectory));
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

        public string LegacyRecordPath => Path.Combine(StateDirectory, "aspire-tags");

        public static string Prefs(string[]? advertiseTags) =>
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ControlURL"] = "https://controlplane.tailscale.com",
                ["RouteAll"] = false,
                ["ExitNodeID"] = "",
                ["CorpDNS"] = true,
                ["AdvertiseTags"] = advertiseTags,
                ["Hostname"] = "quadra-web",
                ["NetfilterMode"] = 2,
                ["Config"] = new Dictionary<string, object?> { ["UserProfile"] = new Dictionary<string, object?> { ["LoginName"] = "tagged-devices" } },
            });

        public async Task WriteStateAsync(string? currentProfile, string? prefs)
        {
            Directory.CreateDirectory(StateDirectory);
            var entries = new Dictionary<string, string>
            {
                ["_machinekey"] = Base64("privkey:0000"),
            };
            if (currentProfile is not null)
            {
                entries["_current-profile"] = Base64(currentProfile);
                entries[currentProfile] = Base64(prefs ?? "{}");
                entries["_profiles"] = Base64("[{\"ID\":\"5f3a\",\"Key\":\"" + currentProfile + "\"}]");
            }

            await File.WriteAllTextAsync(
                Path.Combine(StateDirectory, "tailscaled.state"),
                JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }),
                TestContext.Current.CancellationToken);
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

        private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError);
}
