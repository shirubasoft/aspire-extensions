using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aspire.Hosting.Tests;

// Runs the sidecar start script with a fake containerboot on PATH, once under
// the host's POSIX shell and once under the pinned image's BusyBox. The script
// treats every absolute path it touches as configuration so a test can redirect
// it into a temporary directory. State fixtures follow the tailscaled file
// store layout: an indented JSON object of base64 values, where the key named
// by "_current-profile" holds the current profile's tab-indented prefs.
public abstract class TailscaleStartScriptScenarios
{
    protected const int ContainerbootExitCode = 42;

    private protected abstract ScriptRunner CreateRunner();

    [Fact]
    public async Task FirstStartWritesTheServeConfigAndStartsWithoutRecordingAnything()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal("{\"serve\":true}", await File.ReadAllTextAsync(workspace.ServeConfigPath, TestContext.Current.CancellationToken));
        Assert.Equal([], Directory.GetFileSystemEntries(workspace.StateDirectory));
    }

    [Fact]
    public async Task StateWithoutAProfileIsAFreshRegistration()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(TailscaleState.Create(currentProfile: null, prefs: null));

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
    }

    [Fact]
    public async Task EmptyCurrentProfileIsAFreshRegistration()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(TailscaleState.Create(currentProfile: "", prefs: null));

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
    }

    // A first boot that failed before registration leaves no identity, so a
    // corrected tag list must start normally. A record from an earlier package
    // version is removed because the profile is the only source of truth.
    [Fact]
    public async Task FailedFirstRegistrationDoesNotBlockCorrectedTags()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        Directory.CreateDirectory(workspace.StateDirectory);
        await File.WriteAllTextAsync(workspace.LegacyRecordPath, "tag:apps\n", TestContext.Current.CancellationToken);

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.False(File.Exists(workspace.LegacyRecordPath));
    }

    [Fact]
    public async Task RegisteredNodeWithTheSameTagSetStarts()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(TailscaleState.Create("profile-5f3a", TailscaleState.Prefs(["tag:web", "tag:apps"])));

        var result = await workspace.RunAsync(tags: "tag:apps,tag:web,tag:apps", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Equal(["tailscaled.state"], Directory.GetFileSystemEntries(workspace.StateDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RegisteredNodeWithOtherTagsRefusesToStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(TailscaleState.Create("profile-5f3a", TailscaleState.Prefs(["tag:apps"])));

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[tag:web]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("admin console", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("state volume", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredNodeWithoutTagsRefusesTaggedStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(TailscaleState.Create("profile-5f3a", TailscaleState.Prefs(null)));

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[]", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartWithoutAStateDirectorySkipsTheProfileCheck()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: false);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.False(Directory.Exists(workspace.StateDirectory));
        Assert.True(File.Exists(workspace.ServeConfigPath));
    }

    // JSON encoders differ in whitespace. A profile that is present must be
    // found in every layout, otherwise mismatched tags would start the node.
    [Theory]
    [InlineData("\"_current-profile\" : \"{0}\"")]
    [InlineData("\"_current-profile\":\t\"{0}\"")]
    [InlineData("\"_current-profile\":\"{0}\"")]
    public async Task WhitespaceVariantsStillDetectTheProfile(string entryFormat)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var entry = string.Format(null, entryFormat, TailscaleState.Base64("profile-5f3a"));
        await workspace.WriteStateTextAsync(
            "{\n" + entry + ",\n  \"profile-5f3a\": \"" + TailscaleState.Base64(TailscaleState.Prefs(["tag:apps"])) + "\"\n}\n");

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactStateFileIsParsed()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(
            "{\"_current-profile\":\"" + TailscaleState.Base64("profile-5f3a") + "\",\"profile-5f3a\":\""
            + TailscaleState.Base64(TailscaleState.Prefs(["tag:apps"])) + "\"}");

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"_current-profile\": null", "_current-profile")]
    [InlineData("\"_current-profile\": \"not*base64\"", "base64")]
    [InlineData("\"_current-profile\": \"cHJvZmlsZS0uLi94\"", "is not a profile key")]
    [InlineData("\"_current-profile\": \"ZXZpbCI7IHJtIC1yZiAv\"", "is not a profile key")]
    public async Task UnreadableCurrentProfileRefusesToStart(string entry, string reason)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(
            "{\n  \"_machinekey\": \"" + TailscaleState.Base64("privkey:00") + "\",\n  " + entry + "\n}\n");

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(reason, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProfileEntryRefusesToStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(
            "{\n  \"_current-profile\": \"" + TailscaleState.Base64("profile-5f3a") + "\"\n}\n");

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("profile-5f3a", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("missing", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not*base64", "base64")]
    [InlineData("bm90IGpzb24gYXQgYWxs", "prefs")]
    public async Task UnreadableProfileRefusesToStart(string encodedPrefs, string reason)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(
            "{\n  \"_current-profile\": \"" + TailscaleState.Base64("profile-5f3a") + "\",\n  \"profile-5f3a\": \"" + encodedPrefs + "\"\n}\n");

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(reason, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileWithoutAdvertiseTagsRefusesToStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(TailscaleState.Create(
            "profile-5f3a",
            "{\n\t\"ControlURL\": \"https://controlplane.tailscale.com\",\n\t\"Hostname\": \"quadra-web\"\n}"));

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("AdvertiseTags", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinnedImageStateWithTheRegisteredTagsStarts()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(TailscaleState.PinnedImageFixture());

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
    }

    [Fact]
    public async Task PinnedImageStateWithOtherTagsRefusesToStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(TailscaleState.PinnedImageFixture());

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[tag:apps]", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinnedImageStateWithASecondProfileUsesTheCurrentOne()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(TailscaleState.PinnedImageFixtureWithSecondProfile("profile-d4e5", ["tag:web"]));

        var starts = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);
        var refuses = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, starts.ExitCode);
        Assert.Equal(1, refuses.ExitCode);
        Assert.Contains("[tag:apps]", refuses.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinnedImageStateWithACorruptedProfileRefusesToStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(TailscaleState.PinnedImageFixtureWithCorruptedProfile());

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
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

public sealed class TailscaleStateFixtureTests
{
    // An image bump must refresh the captured state fixture.
    [Fact]
    public void PinnedImageStateFixtureMatchesTheImageTag()
    {
        Assert.Equal(TailscaleContainerImageTags.Tag, TailscaleState.FixtureImageTag);
        Assert.True(File.Exists(TailscaleState.FixturePath), TailscaleState.FixturePath);
    }

    [Fact]
    public void PinnedImageStateFixtureUsesUpstreamSerialization()
    {
        var state = JsonSerializer.Deserialize<Dictionary<string, string>>(TailscaleState.PinnedImageFixture())!;
        var currentProfile = Encoding.UTF8.GetString(Convert.FromBase64String(state["_current-profile"]));
        var prefs = Encoding.UTF8.GetString(Convert.FromBase64String(state[currentProfile]));
        var profiles = Encoding.UTF8.GetString(Convert.FromBase64String(state["_profiles"]));

        Assert.Matches("^profile-[0-9a-f]+$", currentProfile);
        Assert.StartsWith("{\n\t\"ControlURL\"", prefs, StringComparison.Ordinal);
        Assert.Contains("\n\t\"AdvertiseTags\": [\n\t\t\"tag:apps\"\n\t],", prefs, StringComparison.Ordinal);
        Assert.StartsWith("{\"" + currentProfile["profile-".Length..] + "\":{", profiles, StringComparison.Ordinal);
    }
}

internal static class TailscaleState
{
    public const string FixtureImageTag = "v1.102.5";

    public static string FixturePath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        $"tailscaled.state.{FixtureImageTag}.json");

    public static string PinnedImageFixture() => File.ReadAllText(FixturePath);

    public static string PinnedImageFixtureWithSecondProfile(string profileKey, string[] tags)
    {
        var state = JsonSerializer.Deserialize<Dictionary<string, string>>(PinnedImageFixture())!;
        var currentProfile = Encoding.UTF8.GetString(Convert.FromBase64String(state["_current-profile"]));
        var profiles = Encoding.UTF8.GetString(Convert.FromBase64String(state["_profiles"]));
        var id = profileKey["profile-".Length..];
        state["_profiles"] = Base64(profiles.TrimEnd('}')
            + ",\"" + id + "\":{\"ID\":\"" + id + "\",\"Key\":\"" + profileKey + "\",\"NodeID\":\"nOTHERNODE\"}}");
        state[profileKey] = Base64(Prefs(tags));
        state["_current-profile"] = Base64(currentProfile);
        return Serialize(state);
    }

    public static string PinnedImageFixtureWithCorruptedProfile()
    {
        var state = JsonSerializer.Deserialize<Dictionary<string, string>>(PinnedImageFixture())!;
        var currentProfile = Encoding.UTF8.GetString(Convert.FromBase64String(state["_current-profile"]));
        state[currentProfile] = state[currentProfile][..^8] + "!!!!";
        return Serialize(state);
    }

    public static string Create(string? currentProfile, string? prefs)
    {
        var entries = new Dictionary<string, string>
        {
            ["_machinekey"] = Base64("privkey:0000"),
        };
        if (currentProfile is not null)
        {
            entries["_current-profile"] = Base64(currentProfile);
        }

        if (!string.IsNullOrEmpty(currentProfile))
        {
            entries[currentProfile] = Base64(prefs ?? "{}");
            entries["_profiles"] = Base64("{\"5f3a\":{\"ID\":\"5f3a\",\"Key\":\"" + currentProfile + "\"}}");
        }

        return Serialize(entries);
    }

    // Prefs serialize with json.MarshalIndent(p, "", "\t") upstream.
    public static string Prefs(string[]? advertiseTags) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["ControlURL"] = "https://controlplane.tailscale.com",
                ["RouteAll"] = false,
                ["ExitNodeID"] = "",
                ["CorpDNS"] = true,
                ["AdvertiseTags"] = advertiseTags,
                ["Hostname"] = "quadra-web",
                ["AdvertiseRoutes"] = null,
                ["NetfilterMode"] = 2,
                ["Config"] = new Dictionary<string, object?>
                {
                    ["PrivateNodeKey"] = "privkey:0000",
                    ["UserProfile"] = new Dictionary<string, object?> { ["LoginName"] = "tagged-devices" },
                },
            },
            new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 });

    public static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    // The file store serializes with json.MarshalIndent(cache, "", "  ").
    private static string Serialize(Dictionary<string, string> entries) =>
        JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }) + "\n";
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
        File.WriteAllText(containerboot, "#!/bin/sh\nexit 42\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(containerboot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public string RootDirectory => _root.FullName;

    public string StateDirectory => Path.Combine(_root.FullName, "state");

    public string ServeConfigPath => Path.Combine(_root.FullName, "serve.json");

    public string LegacyRecordPath => Path.Combine(StateDirectory, "aspire-tags");

    public Task WriteStateAsync(string state) => WriteStateTextAsync(state);

    public async Task WriteStateTextAsync(string state)
    {
        Directory.CreateDirectory(StateDirectory);
        await File.WriteAllTextAsync(Path.Combine(StateDirectory, "tailscaled.state"), state, TestContext.Current.CancellationToken);
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
            foreach (var argument in new[] { "run", "--rm", "--volume", $"{workspace.RootDirectory}:/work:z" })
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
