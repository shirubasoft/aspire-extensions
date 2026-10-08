namespace Aspire.Hosting;

internal static class TailscaleContainerImageTags
{
    public const string Tag = "v1.102.5";
    public const string Registry = "docker.io";
    public const string Image = "tailscale/tailscale";
}

internal static class TailscaleSidecarDefaults
{
    public const string DefaultTag = "tag:apps";
    public const string DevelopmentHostnameSuffix = "-dev";
    public const string StateDirectory = "/var/lib/tailscale";
    public const string StateFile = "tailscaled.state";
    public const string LegacyTagRecordFile = "aspire-tags";
    public const string ServeConfigPath = "/etc/tailscale-serve.json";
    public const string ServeConfigVariable = "TAILSCALE_SERVE_CONFIG_JSON";
    public const string TagsVariable = "TAILSCALE_TAGS";
    public const string Entrypoint = "/bin/sh";

    // containerboot reads the serve configuration from a file and applies
    // --advertise-tags only while it registers the node, so a node that keeps its
    // state keeps its registration tags. The start script writes the
    // configuration from an environment variable so no bind mount is needed,
    // compares the requested tag set with the one in the persisted node profile,
    // and then hands over to the image's own entrypoint.
    //
    // tailscaled's file store is an indented JSON object of base64 values with
    // one key per line; "_current-profile" names the key that holds the current
    // profile's prefs, themselves indented JSON. The prefs are flattened before
    // AdvertiseTags is read. A profile that cannot be read stops the start, and
    // a state file without a profile means the node never registered. Only the
    // image's busybox tools are used, and only $NAME expansions appear because
    // Docker Compose escapes exactly those in shell arguments.
    public const string StartScript =
        "set -e\n"
        + $"printf '%s' \"${ServeConfigVariable}\" > \"$TS_SERVE_CONFIG\"\n"
        + "if [ -n \"$TS_STATE_DIR\" ]; then\n"
        + "mkdir -p \"$TS_STATE_DIR\"\n"
        + $"rm -f \"$TS_STATE_DIR/{LegacyTagRecordFile}\"\n"
        + $"state=\"$TS_STATE_DIR/{StateFile}\"\n"
        + "work=\"$TS_STATE_DIR/.aspire\"\n"
        + "rm -rf \"$work\"\n"
        + "mkdir -p \"$work\"\n"
        + "profile=\n"
        + "if [ -f \"$state\" ]; then\n"
        + "sed -n 's/.*\"_current-profile\": *\"\\([^\"]*\\)\".*/\\1/p' \"$state\" | head -n 1 | base64 -d > \"$work/profile\" 2>/dev/null || true\n"
        + "read -r profile < \"$work/profile\" || true\n"
        + "fi\n"
        + "if [ -n \"$profile\" ]; then\n"
        + "sed -n \"s/.*\\\"$profile\\\": *\\\"\\([^\\\"]*\\)\\\".*/\\1/p\" \"$state\" | head -n 1 | base64 -d 2>/dev/null | tr -d '\\n\\t ' > \"$work/prefs\" || true\n"
        + "if ! grep -q '\"ControlURL\"' \"$work/prefs\"; then\n"
        + "echo \"Tailscale sidecar: could not read the registered node profile from $state."
        + " Stop this sidecar, remove the node in the Tailscale admin console, and delete the state volume to register the node again.\" >&2\n"
        + "exit 1\n"
        + "fi\n"
        + "sed -n 's/.*\"AdvertiseTags\":\\[\\([^]]*\\)\\].*/\\1/p' \"$work/prefs\" | tr ',' '\\n' | tr -d '\"' | grep . | sort -u > \"$work/registered\" || true\n"
        + $"printf '%s' \"${TagsVariable}\" | tr ',' '\\n' | grep . | sort -u > \"$work/requested\" || true\n"
        + "if ! cmp -s \"$work/registered\" \"$work/requested\"; then\n"
        + "tr '\\n' ' ' < \"$work/registered\" > \"$work/registered.line\"\n"
        + "tr '\\n' ' ' < \"$work/requested\" > \"$work/requested.line\"\n"
        + "registered=\n"
        + "requested=\n"
        + "read -r registered < \"$work/registered.line\" || true\n"
        + "read -r requested < \"$work/requested.line\" || true\n"
        + "echo \"Tailscale sidecar: the node on this state volume registered with tags [$registered]"
        + " but the AppHost now requests [$requested]. Tailscale assigns tags when it registers a node."
        + " To change them: stop this sidecar, remove the node in the Tailscale admin console,"
        + " delete the state volume, and deploy again.\" >&2\n"
        + "exit 1\n"
        + "fi\n"
        + "fi\n"
        + "rm -rf \"$work\"\n"
        + "fi\n"
        + "exec containerboot";
}
