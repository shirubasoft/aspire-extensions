using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task StartupDisablesUnvalidatedNetmapCache()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        workspace.EnvironmentOverrides["TS_USE_CACHED_NETMAP"] = "true";
        await workspace.WriteStateTextAsync(TailscaleState.PinnedImageFixture());

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
        Assert.Contains("CACHE=false", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tag:apps", ContainerbootExitCode)]
    [InlineData("tag:web", 1)]
    public async Task GeneratedSingleProfileUsesTheCheckedTags(string tags, int exitCode)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(TailscaleState.PinnedImageSingleProfileFixture());

        var result = await workspace.RunAsync(tags, withStateDirectory: true);

        Assert.Equal(exitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("TS_AUTH_ONCE", "false")]
    [InlineData("TS_USERSPACE", "false")]
    [InlineData("TS_EXTRA_ARGS", "--advertise-tags=tag:web")]
    [InlineData("TS_EXTRA_ARGS", "--advertise-tags=tag:apps --advertise-tags=tag:web")]
    [InlineData("TS_EXTRA_ARGS", "--advertise-tags=tag:apps --force-reauth")]
    [InlineData("TS_TAILSCALED_EXTRA_ARGS", "--state=/other/tailscaled.state")]
    [InlineData("TS_TAILSCALED_EXTRA_ARGS", "--config=/other/config.json")]
    [InlineData("TS_EXPERIMENTAL_VERSIONED_CONFIG_DIR", "/other")]
    [InlineData("TS_KUBE_SECRET", "other")]
    [InlineData("KUBERNETES_SERVICE_HOST", "192.0.2.1")]
    [InlineData("TS_TEST_ONLY_ROOT", "/other")]
    [InlineData("TS_DEBUG_FAKE_GOOS", "windows")]
    public async Task AlternateStartupEnvironmentRefusesToStart(string name, string value)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        workspace.EnvironmentOverrides[name] = value;
        await workspace.WriteStateTextAsync(TailscaleState.PinnedImageFixture());

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("startup environment", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tag:apps --accept-dns=true")]
    [InlineData("tag:apps,")]
    [InlineData("tag:ap_ps")]
    public async Task InvalidRequestedTagsRefuseToStart(string tags)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var result = await workspace.RunAsync(tags, withStateDirectory: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("startup environment", result.StandardError, StringComparison.Ordinal);
    }

    // These cases alter selection inputs while the directly named prefs still have tag:apps.
    [Theory]
    [InlineData("metadata-id-redirect")]
    [InlineData("multi-profile")]
    [InlineData("no-metadata")]
    [InlineData("empty-metadata")]
    [InlineData("null-metadata")]
    [InlineData("empty-metadata-value")]
    [InlineData("malformed-metadata")]
    [InlineData("metadata-array")]
    [InlineData("null-profile")]
    [InlineData("map-id-mismatch")]
    [InlineData("embedded-id-mismatch")]
    [InlineData("missing-id")]
    [InlineData("empty-id")]
    [InlineData("wrong-id-type")]
    [InlineData("key-mismatch")]
    [InlineData("missing-key")]
    [InlineData("empty-key")]
    [InlineData("wrong-key-type")]
    [InlineData("duplicate-key")]
    [InlineData("duplicate-map-id")]
    [InlineData("escaped-key")]
    [InlineData("escaped-id")]
    [InlineData("case-key")]
    [InlineData("case-duplicate-key")]
    [InlineData("unknown-key")]
    [InlineData("unknown-network-key")]
    [InlineData("unknown-user-key")]
    [InlineData("wrong-network-type")]
    [InlineData("wrong-user-id-type")]
    [InlineData("local-user")]
    [InlineData("missing-name")]
    [InlineData("missing-control")]
    [InlineData("missing-network")]
    [InlineData("missing-user")]
    [InlineData("missing-node-id")]
    [InlineData("missing-local-user")]
    [InlineData("bad-created")]
    [InlineData("duplicate-network-key")]
    [InlineData("nested-case-key")]
    [InlineData("orphan-profile")]
    [InlineData("no-current")]
    [InlineData("empty-current")]
    [InlineData("wrong-current")]
    [InlineData("metadata-only")]
    [InlineData("orphan-without-current")]
    [InlineData("serve-without-current")]
    [InlineData("route-info-without-current")]
    [InlineData("orphan-serve")]
    [InlineData("orphan-route-info")]
    [InlineData("no-machine-key")]
    [InlineData("empty-machine-key")]
    [InlineData("legacy-selector")]
    [InlineData("windows-selector")]
    [InlineData("legacy-ios")]
    [InlineData("legacy-android")]
    public async Task UnsupportedProfileStateRefusesToStart(string corruption)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var state = JsonNode.Parse(corruption is "metadata-id-redirect" or "multi-profile"
            ? TailscaleState.PinnedImageMultiProfileFixture() : TailscaleState.PinnedImageFixture())!.AsObject();
        var metadata = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(state["_profiles"]!.GetValue<string>())))!.AsObject();
        var profile = metadata["c298"]!.AsObject();
        switch (corruption)
        {
            case "metadata-id-redirect": profile["ID"] = "d4e5"; break;
            case "map-id-mismatch": metadata["d4e5"] = profile.DeepClone(); metadata.Remove("c298"); break;
            case "embedded-id-mismatch": profile["ID"] = "d4e5"; break;
            case "missing-id": profile.Remove("ID"); break;
            case "empty-id": profile["ID"] = ""; break;
            case "wrong-id-type": profile["ID"] = 123; break;
            case "key-mismatch": profile["Key"] = "profile-d4e5"; break;
            case "missing-key": profile.Remove("Key"); break;
            case "empty-key": profile["Key"] = ""; break;
            case "wrong-key-type": profile["Key"] = false; break;
            case "case-key": profile["key"] = profile["Key"]!.DeepClone(); profile.Remove("Key"); break;
            case "case-duplicate-key": profile["key"] = "profile-d4e5"; break;
            case "unknown-key": profile["Unknown"] = ""; break;
            case "unknown-network-key": profile["NetworkProfile"]!["Unknown"] = ""; break;
            case "unknown-user-key": profile["UserProfile"]!["Unknown"] = ""; break;
            case "wrong-network-type": profile["NetworkProfile"] = false; break;
            case "wrong-user-id-type": profile["UserProfile"]!["ID"] = "123"; break;
            case "local-user": profile["LocalUserID"] = "S-1-5-123"; break;
            case "missing-name": profile.Remove("Name"); break;
            case "missing-control": profile.Remove("ControlURL"); break;
            case "missing-network": profile.Remove("NetworkProfile"); break;
            case "missing-user": profile.Remove("UserProfile"); break;
            case "missing-node-id": profile.Remove("NodeID"); break;
            case "missing-local-user": profile.Remove("LocalUserID"); break;
            case "bad-created": profile["Created"] = "yesterday"; break;
            case "nested-case-key": profile["NetworkProfile"]!["magicdnsname"] = "example.ts.net"; break;
            case "null-profile": metadata["c298"] = null; break;
        }

        var encodedMetadata = metadata.ToJsonString();
        encodedMetadata = corruption switch
        {
            "duplicate-network-key" => encodedMetadata.Replace("\"MagicDNSName\":", "\"MagicDNSName\":\"other\",\"MagicDNSName\":", StringComparison.Ordinal),
            "empty-metadata" => "{}",
            "null-metadata" => "null",
            "empty-metadata-value" => "",
            "malformed-metadata" => encodedMetadata[..^1],
            "metadata-array" => "[]",
            "duplicate-key" => encodedMetadata.Replace("\"ID\":", "\"Key\":\"profile-d4e5\",\"ID\":", StringComparison.Ordinal),
            "duplicate-map-id" => "{\"c298\":" + profile.ToJsonString() + ",\"c298\":" + profile.ToJsonString() + "}",
            "escaped-key" => encodedMetadata.Replace("\"Key\"", "\"\\u004bey\"", StringComparison.Ordinal),
            "escaped-id" => encodedMetadata.Replace("\"ID\":\"c298\"", "\"ID\":\"\\u0063298\"", StringComparison.Ordinal),
            _ => encodedMetadata,
        };
        state["_profiles"] = TailscaleState.Base64(encodedMetadata);
        switch (corruption)
        {
            case "no-metadata": state.Remove("_profiles"); break;
            case "orphan-profile": state["profile-d4e5"] = state["profile-c298"]!.DeepClone(); break;
            case "no-current": state.Remove("_current-profile"); break;
            case "empty-current": state["_current-profile"] = ""; break;
            case "wrong-current": state["_current-profile"] = TailscaleState.Base64("profile-d4e5"); break;
            case "metadata-only": state.Remove("_current-profile"); state.Remove("profile-c298"); break;
            case "orphan-without-current": state.Remove("_current-profile"); state.Remove("_profiles"); break;
            case "serve-without-current":
            case "route-info-without-current":
                state.Remove("_current-profile"); state.Remove("_profiles"); state.Remove("profile-c298");
                state[corruption == "serve-without-current" ? "_serve/c298" : "profile-c298||_routeInfo"] = TailscaleState.Base64("{}");
                break;
            case "orphan-serve": state["_serve/d4e5"] = TailscaleState.Base64("{}"); break;
            case "orphan-route-info": state["profile-d4e5||_routeInfo"] = TailscaleState.Base64("{}"); break;
            case "no-machine-key": state.Remove("_machinekey"); break;
            case "empty-machine-key": state["_machinekey"] = ""; break;
            case "legacy-selector": state["server-mode-start-key"] = TailscaleState.Base64("profile-c298"); break;
            case "windows-selector": state["_current/S-1-5-123"] = TailscaleState.Base64("profile-c298"); break;
            case "legacy-ios": state["ipn-go-bridge"] = state["profile-c298"]!.DeepClone(); break;
            case "legacy-android": state["ipn-android"] = state["profile-c298"]!.DeepClone(); break;
        }

        await workspace.WriteStateTextAsync(state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("state volume", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("registered")]
    public async Task LegacyDaemonPrefsRefuseToStart(string current)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var state = JsonNode.Parse(TailscaleState.PinnedImageFixture())!.AsObject();
        var prefs = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(state["profile-c298"]!.GetValue<string>())))!;
        prefs["AdvertiseTags"] = new JsonArray("tag:web");
        state["_daemon"] = TailscaleState.Base64(TailscaleState.SerializePrefs(prefs));
        if (current != "registered")
        {
            state.Remove("_current-profile");
            state.Remove("_profiles");
            state.Remove("profile-c298");
            if (current == "empty")
            {
                state["_current-profile"] = "";
            }
        }

        await workspace.WriteStateTextAsync(state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("state volume", result.StandardError, StringComparison.Ordinal);
    }

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

        // NewFileStore writes this exact initial store before it has any keys.
        await workspace.WriteStateTextAsync("{}");
        var emptyStore = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);
        Assert.Equal(ContainerbootExitCode, emptyStore.ExitCode);
    }

    [Fact]
    public async Task EmptyCurrentProfileRefusesToStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateAsync(TailscaleState.Create(currentProfile: "", prefs: null));

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("state volume", result.StandardError, StringComparison.Ordinal);
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

    // Only the pinned image's MarshalIndent layout is accepted.
    [Theory]
    [InlineData("\"_current-profile\" : \"{0}\"")]
    [InlineData("\"_current-profile\":\t\"{0}\"")]
    [InlineData("\"_current-profile\":\"{0}\"")]
    public async Task NoncanonicalWhitespaceRefusesToStart(string entryFormat)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var entry = string.Format(null, entryFormat, TailscaleState.Base64("profile-5f3a"));
        await workspace.WriteStateTextAsync(
            "{\n" + entry + ",\n  \"profile-5f3a\": \"" + TailscaleState.Base64(TailscaleState.Prefs(["tag:apps"])) + "\"\n}\n");

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactStateFileRefusesToStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(
            "{\"_current-profile\":\"" + TailscaleState.Base64("profile-5f3a") + "\",\"profile-5f3a\":\""
            + TailscaleState.Base64(TailscaleState.Prefs(["tag:apps"])) + "\"}");

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"_current-profile\": null", "state format")]
    [InlineData("\"_current-profile\": \"not*base64\"", "state format")]
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
        var state = JsonNode.Parse(TailscaleState.Create("profile-5f3a", TailscaleState.Prefs(["tag:apps"])))!.AsObject();
        state.Remove("profile-5f3a");
        await workspace.WriteStateTextAsync(state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("profile-5f3a", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("missing", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not*base64", "state format")]
    [InlineData("bm90IGpzb24gYXQgYWxs", "prefs")]
    public async Task UnreadableProfileRefusesToStart(string encodedPrefs, string reason)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var state = JsonNode.Parse(TailscaleState.Create("profile-5f3a", TailscaleState.Prefs(["tag:apps"])))!;
        state["profile-5f3a"] = encodedPrefs;
        await workspace.WriteStateTextAsync(state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

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

    // Every case runs under the host shell and the pinned image's BusyBox.
    [Theory]
    [InlineData("escaped-key")]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("whitespace-only")]
    [InlineData("duplicate-key")]
    [InlineData("unknown-structure")]
    [InlineData("crlf")]
    [InlineData("missing-comma")]
    [InlineData("trailing-comma")]
    [InlineData("trailing-content")]
    [InlineData("unsafe-key")]
    public async Task NoncanonicalStateRefusesToStart(string corruption)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var state = TailscaleState.Create("profile-5f3a", TailscaleState.Prefs(["tag:apps"]));
        state = corruption switch
        {
            "escaped-key" => state.Replace("_current-profile", "\\u005fcurrent-profile", StringComparison.Ordinal),
            "truncated" => "{\"_current-profile\": \"cHJv",
            "empty" => "",
            "whitespace-only" => " \t\n \n",
            "duplicate-key" => state.Replace("{\n", "{\n  \"_current-profile\": \"\",\n", StringComparison.Ordinal),
            "unknown-structure" => state.Replace("{\n", "{\n  \"extra\": {},\n", StringComparison.Ordinal),
            "crlf" => state.Replace("\n", "\r\n", StringComparison.Ordinal),
            "missing-comma" => state.Replace("\",\n", "\"\n", StringComparison.Ordinal),
            "trailing-comma" => state.Replace("\"\n}", "\",\n}", StringComparison.Ordinal),
            "trailing-content" => state + "[]\n",
            "unsafe-key" => state.Replace("_machinekey", "unsafe.key", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        await workspace.WriteStateTextAsync(state);

        var result = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("state format", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("escaped-key")]
    [InlineData("truncated")]
    [InlineData("duplicate-key")]
    [InlineData("unknown-structure")]
    [InlineData("compact")]
    [InlineData("wrong-indent")]
    [InlineData("invalid-tag")]
    [InlineData("missing-comma")]
    [InlineData("trailing-comma")]
    [InlineData("trailing-content")]
    public async Task NoncanonicalPrefsRefusesToStart(string corruption)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var prefs = TailscaleState.Prefs(["tag:apps"]);
        prefs = corruption switch
        {
            "escaped-key" => prefs[..^2] + ",\n\t\"\\u0041dvertiseTags\": null\n}",
            "truncated" => prefs[..^1],
            "duplicate-key" => prefs.Replace("{\n", "{\n\t\"AdvertiseTags\": null,\n", StringComparison.Ordinal),
            "unknown-structure" => prefs.Replace("\t\"RouteAll\": false,", "\t\"RouteAll\": false garbage,", StringComparison.Ordinal),
            "compact" => prefs.Replace("\n", "", StringComparison.Ordinal).Replace("\t", "", StringComparison.Ordinal),
            "wrong-indent" => prefs.Replace("\t", "  ", StringComparison.Ordinal),
            "invalid-tag" => prefs.Replace("tag:apps", "tag:ap ps", StringComparison.Ordinal),
            "missing-comma" => prefs.Replace("\",\n", "\"\n", StringComparison.Ordinal),
            "trailing-comma" => prefs.Replace("\"tag:apps\"\n", "\"tag:apps\",\n", StringComparison.Ordinal),
            "trailing-content" => prefs + "\n[]",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        await workspace.WriteStateAsync(TailscaleState.Create("profile-5f3a", prefs));

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("advertisetags")]
    [InlineData("ADVERTISETAGS")]
    [InlineData("advertiseTags")]
    public async Task CaseVariantAdvertiseTagsRefusesToStart(string key)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var prefs = TailscaleState.Prefs(["tag:apps"]);
        prefs = prefs[..^2] + ",\n\t\"" + key + "\": [\n\t\t\"tag:web\"\n\t]\n}";
        await workspace.WriteStateAsync(TailscaleState.Create("profile-5f3a", prefs));

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Prefs", "Unknown", false)]
    [InlineData("Prefs", "routeall", false)]
    [InlineData("Prefs", "routeall", true)]
    [InlineData("AutoUpdate", "Unknown", false)]
    [InlineData("AutoUpdate", "check", false)]
    [InlineData("AutoUpdate", "check", true)]
    [InlineData("AppConnector", "Unknown", false)]
    [InlineData("AppConnector", "advertise", false)]
    [InlineData("AppConnector", "advertise", true)]
    [InlineData("Config", "Unknown", false)]
    [InlineData("Config", "nodeid", false)]
    [InlineData("Config", "nodeid", true)]
    [InlineData("UserProfile", "Unknown", false)]
    [InlineData("UserProfile", "loginname", false)]
    [InlineData("UserProfile", "loginname", true)]
    [InlineData("AttestationKey", "Unknown", false)]
    [InlineData("AttestationKey", "TpmPrivate", false)]
    [InlineData("AttestationKey", "TpmPrivate", true)]
    [InlineData("DriveShare", "Unknown", false)]
    [InlineData("DriveShare", "Name", false)]
    [InlineData("DriveShare", "Name", true)]
    public async Task NoncanonicalPrefsKeyRefusesToStart(string scope, string key, bool duplicate)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var prefs = JsonNode.Parse(TailscaleState.Prefs(["tag:apps"]))!.AsObject();
        prefs["AutoUpdate"] = new JsonObject { ["Check"] = true, ["Apply"] = null };
        prefs["AppConnector"] = new JsonObject { ["Advertise"] = false };
        prefs["Config"]!["NodeID"] = "nTESTNODE";
        prefs["Config"]!["AttestationKey"] = new JsonObject { ["tpmPrivate"] = "", ["tpmPublic"] = "" };
        prefs["DriveShares"] = new JsonArray(new JsonObject { ["name"] = "fixture-share" });
        var target = scope switch
        {
            "Prefs" => prefs,
            "AutoUpdate" => prefs["AutoUpdate"]!.AsObject(),
            "AppConnector" => prefs["AppConnector"]!.AsObject(),
            "Config" => prefs["Config"]!.AsObject(),
            "UserProfile" => prefs["Config"]!["UserProfile"]!.AsObject(),
            "AttestationKey" => prefs["Config"]!["AttestationKey"]!.AsObject(),
            "DriveShare" => prefs["DriveShares"]![0]!.AsObject(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        var canonical = target.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
        if (canonical.Key is not null && !duplicate)
        {
            target.Remove(canonical.Key);
        }

        target[key] = canonical.Value?.DeepClone() ?? JsonValue.Create(true);
        await workspace.WriteStateAsync(TailscaleState.Create("profile-5f3a", prefs.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 })));

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not read", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown-key")]
    [InlineData("case-current-profile")]
    [InlineData("duplicate-current-profile")]
    [InlineData("duplicate-machine-key")]
    [InlineData("case-profile-key")]
    public async Task NoncanonicalStoreKeyRefusesToStart(string corruption)
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var state = TailscaleState.Create("profile-5f3a", TailscaleState.Prefs(["tag:apps"]));
        state = corruption switch
        {
            "unknown-key" => state.Replace("{\n", "{\n  \"unknown\": \"\",\n", StringComparison.Ordinal),
            "case-current-profile" => state.Replace("_current-profile", "_CURRENT-PROFILE", StringComparison.Ordinal),
            "duplicate-current-profile" => state.Replace("{\n", "{\n  \"_CURRENT-PROFILE\": \"\",\n", StringComparison.Ordinal),
            "duplicate-machine-key" => state.Replace("{\n", "{\n  \"_MACHINEKEY\": \"\",\n", StringComparison.Ordinal),
            "case-profile-key" => state.Replace("{\n", "{\n  \"PROFILE-5F3A\": \"\",\n", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        await workspace.WriteStateTextAsync(state);

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("state format", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisteredNodeWithCrLfPrefsAndTheSameTagsStarts()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        var prefs = TailscaleState.Prefs(["tag:apps"]).Replace("\n", "\r\n", StringComparison.Ordinal);
        await workspace.WriteStateAsync(TailscaleState.Create("profile-5f3a", prefs));

        var result = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);

        Assert.Equal(ContainerbootExitCode, result.ExitCode);
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
    public async Task PinnedImageStateWithASecondProfileRefusesToStart()
    {
        using var workspace = new ScriptWorkspace(CreateRunner());
        await workspace.WriteStateTextAsync(TailscaleState.PinnedImageMultiProfileFixture());

        var starts = await workspace.RunAsync(tags: "tag:apps", withStateDirectory: true);
        var refuses = await workspace.RunAsync(tags: "tag:web", withStateDirectory: true);

        Assert.Equal(1, starts.ExitCode);
        Assert.Equal(1, refuses.ExitCode);
        Assert.Contains("state volume", starts.StandardError, StringComparison.Ordinal);
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
    [Fact]
    public void StateKeyAllowlistMatchesTheImageTag()
    {
        Assert.Contains("# State schema: " + TailscaleContainerImageTags.Tag + "\n",
            TailscaleSidecarDefaults.StartScript, StringComparison.Ordinal);
    }

    // An image bump must refresh the captured state fixture.
    [Fact]
    public void PinnedImageStateFixtureMatchesTheImageTag()
    {
        Assert.Equal(TailscaleContainerImageTags.Tag, TailscaleState.FixtureImageTag);
        Assert.True(File.Exists(TailscaleState.FixturePath), TailscaleState.FixturePath);
        Assert.True(File.Exists(TailscaleState.MultiProfileFixturePath), TailscaleState.MultiProfileFixturePath);
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

    [Fact]
    public void SecondProfileFixtureHasValidProfileMetadata()
    {
        var state = JsonSerializer.Deserialize<Dictionary<string, string>>(
            TailscaleState.PinnedImageFixtureWithSecondProfile("profile-d4e5", ["tag:web"]))!;
        var profiles = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Convert.FromBase64String(state["_profiles"]))!;
        var currentProfile = Encoding.UTF8.GetString(Convert.FromBase64String(state["_current-profile"]));

        Assert.Equal(2, profiles.Count);
        Assert.Equal(currentProfile, profiles[currentProfile["profile-".Length..]].GetProperty("Key").GetString());
        Assert.Equal("profile-d4e5", profiles["d4e5"].GetProperty("Key").GetString());
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

    public static string MultiProfileFixturePath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        $"tailscaled.multi-profile.state.{FixtureImageTag}.json");

    public static string PinnedImageSingleProfileFixture() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", $"tailscaled.single-profile.state.{FixtureImageTag}.json"));

    public static string PinnedImageMultiProfileFixture() => File.ReadAllText(MultiProfileFixturePath);

    public static string PinnedImageFixtureWithSecondProfile(string profileKey, string[] tags)
    {
        var state = JsonSerializer.Deserialize<Dictionary<string, string>>(PinnedImageFixture())!;
        var currentProfile = Encoding.UTF8.GetString(Convert.FromBase64String(state["_current-profile"]));
        var profiles = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Convert.FromBase64String(state["_profiles"]))!;
        var id = profileKey["profile-".Length..];
        profiles[id] = JsonSerializer.SerializeToElement(new { ID = id, Key = profileKey, NodeID = "nOTHERNODE" });
        state["_profiles"] = Base64(JsonSerializer.Serialize(profiles));
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
            var profile = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(
                JsonNode.Parse(PinnedImageFixture())!["_profiles"]!.GetValue<string>())))!["c298"]!.DeepClone();
            var id = currentProfile["profile-".Length..];
            profile["ID"] = id;
            profile["Key"] = currentProfile;
            entries["_profiles"] = Base64(new JsonObject { [id] = profile }.ToJsonString());
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

    public static string SerializePrefs(JsonNode prefs) => prefs.ToJsonString(
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
        File.WriteAllText(containerboot, "#!/bin/sh\nprintf 'CACHE=%s\\n' \"${TS_USE_CACHED_NETMAP:-}\"\nexit 42\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(containerboot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public Dictionary<string, string> EnvironmentOverrides { get; } = [];

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
            startInfo.Environment["TS_EXTRA_ARGS"] = "--advertise-tags=" + tags;
            startInfo.Environment["TS_AUTH_ONCE"] = "true";
            startInfo.Environment["TS_USERSPACE"] = "true";
            foreach (var (name, value) in workspace.EnvironmentOverrides)
            {
                startInfo.Environment[name] = value;
            }
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
                ["TS_EXTRA_ARGS"] = "--advertise-tags=" + tags,
                ["TS_AUTH_ONCE"] = "true",
                ["TS_USERSPACE"] = "true",
            };
            if (withStateDirectory)
            {
                variables["TS_STATE_DIR"] = "/work/state";
            }

            foreach (var (name, value) in workspace.EnvironmentOverrides)
            {
                variables[name] = value;
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
