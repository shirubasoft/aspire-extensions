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
    public const string ServeConfigPath = "/etc/tailscale/serve.json";
    public const string ServeConfigVariable = "TAILSCALE_SERVE_CONFIG_JSON";
    public const string Entrypoint = "/bin/sh";

    // containerboot reads the serve configuration from a file. The start script
    // writes it from an environment variable so no bind mount is needed, then
    // hands over to the image's own entrypoint.
    public const string StartScript =
        "mkdir -p /etc/tailscale"
        + $" && printf '%s' \"${ServeConfigVariable}\" > {ServeConfigPath}"
        + " && exec /usr/local/bin/containerboot";
}
