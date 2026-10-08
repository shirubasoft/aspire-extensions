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
    public const string ServeConfigPath = "/etc/tailscale-serve.json";
    public const string ServeConfigVariable = "TAILSCALE_SERVE_CONFIG_JSON";
    public const string TagsVariable = "TAILSCALE_TAGS";
    public const string TagRecordFile = "aspire-tags";
    public const string Entrypoint = "/bin/sh";

    // containerboot reads the serve configuration from a file and applies
    // --advertise-tags only while registering the node, so a node that keeps its
    // state keeps its registration tags. The start script writes the
    // configuration from an environment variable so no bind mount is needed,
    // refuses a state volume whose node registered with other tags, and then
    // hands over to the image's own entrypoint. The script uses only $NAME
    // expansions: those are the forms Compose escapes in shell arguments.
    public const string StartScript =
        "set -e\n"
        + $"printf '%s' \"${ServeConfigVariable}\" > \"$TS_SERVE_CONFIG\"\n"
        + "if [ -n \"$TS_STATE_DIR\" ]; then\n"
        + "mkdir -p \"$TS_STATE_DIR\"\n"
        + $"if [ -f \"$TS_STATE_DIR/{TagRecordFile}\" ]; then\n"
        + $"read -r recorded < \"$TS_STATE_DIR/{TagRecordFile}\" || true\n"
        + $"if [ \"$recorded\" != \"${TagsVariable}\" ]; then\n"
        + "echo \"Tailscale sidecar: the node on this state volume registered with tags"
        + " '$recorded', but the AppHost now requests '$TAILSCALE_TAGS'. Registration tags"
        + " do not change on an existing node. Retag the node in the Tailscale admin console,"
        + $" then delete $TS_STATE_DIR/{TagRecordFile} to accept the new tags.\" >&2\n"
        + "exit 1\n"
        + "fi\n"
        + "fi\n"
        + $"printf '%s\\n' \"${TagsVariable}\" > \"$TS_STATE_DIR/{TagRecordFile}\"\n"
        + "fi\n"
        + "exec containerboot";
}
