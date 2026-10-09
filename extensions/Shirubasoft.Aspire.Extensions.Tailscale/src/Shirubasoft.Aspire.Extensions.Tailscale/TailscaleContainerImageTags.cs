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
    public const string TagMarkerFile = "aspire-tags";
    public const string ServeConfigPath = "/etc/tailscale-serve.json";
    public const string ServeConfigVariable = "TAILSCALE_SERVE_CONFIG_JSON";
    public const string TagsVariable = "TAILSCALE_TAGS";
    public const string StartScriptVariable = "TAILSCALE_START_SCRIPT";
    public const string Entrypoint = "/bin/sh";

    // The script travels in an environment variable so that it can use every
    // shell expansion: Docker Compose escapes only $NAME forms in shell
    // arguments, but a doubled "$" in an environment value reaches the shell as
    // one "$".
    public const string EntrypointCommand = $"eval \"${StartScriptVariable}\"";

    // containerboot applies --advertise-tags during registration. Keep a marker
    // of those tags so persisted nodes stop when the AppHost requests a new set.
    // Raw literals inherit checkout line endings; the container's shell requires LF.
    public static readonly string StartScript = $$"""
        set -eu
        export LC_ALL=C
        normalize_tags() {
          printf '%s' "$1" | tr -d '[:space:]' | tr ',' '\n' | sort -u | paste -sd ',' -
        }
        printf '%s' "${{ServeConfigVariable}}" > "$TS_SERVE_CONFIG"
        state_dir="${TS_STATE_DIR:-}"
        if [ -n "$state_dir" ]; then
          mkdir -p "$state_dir"
          marker="$state_dir/{{TagMarkerFile}}"
          state="$state_dir/{{StateFile}}"
          requested=$(normalize_tags "${{TagsVariable}}")
          if [ -f "$state" ] && grep -q '"_current-profile"[[:space:]]*:' "$state"; then
            registered='<missing marker>'
            if [ -f "$marker" ]; then
              registered=$(normalize_tags "$(cat "$marker")")
            fi
            if [ "$registered" != "$requested" ]; then
              echo "Tailscale sidecar: this state volume records registration tags [$registered], but the AppHost requests [$requested]. Stop this sidecar, remove the node in the Tailscale admin console, and delete the state volume ({project}_{resource}-ts-state in Compose) to re-register with the requested tags." >&2
              exit 1
            fi
          else
            printf '%s\n' "$requested" > "$marker"
          fi
        fi
        exec containerboot
        """.ReplaceLineEndings("\n");
}
