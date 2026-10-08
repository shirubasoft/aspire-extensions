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
    public const string StartScriptVariable = "TAILSCALE_START_SCRIPT";
    public const string Entrypoint = "/bin/sh";

    // The script travels in an environment variable so that it can use every
    // shell expansion: Docker Compose escapes only $NAME forms in shell
    // arguments, but a doubled "$" in an environment value reaches the shell as
    // one "$".
    public const string EntrypointCommand = $"eval \"${StartScriptVariable}\"";

    // containerboot reads the serve configuration from a file and applies
    // --advertise-tags only while it registers the node, so a node that keeps its
    // state keeps its registration tags. The start script writes the
    // configuration from an environment variable so no bind mount is needed,
    // compares the requested tag set with the one in the persisted node profile,
    // and then hands over to the image's own entrypoint.
    //
    // tailscaled's file store is a JSON object of base64 values; the value of
    // "_current-profile" names the key that holds the current profile's prefs,
    // themselves JSON with an AdvertiseTags array. No state file, no
    // "_current-profile" entry, or an empty one means the node never registered.
    // Anything else that cannot be decoded or validated stops the start, because
    // starting would risk the wrong identity. Only tools from the image's busybox
    // are used, every step is bounded by the state file, and no scratch files
    // are written, so concurrent starts on one volume only read.
    public const string StartScript =
        "set -eu\n"
        + $"printf '%s' \"${ServeConfigVariable}\" > \"$TS_SERVE_CONFIG\"\n"
        + "state_dir=\"${TS_STATE_DIR:-}\"\n"
        + "if [ -n \"$state_dir\" ]; then\n"
        + "  mkdir -p \"$state_dir\"\n"
        + $"  rm -f \"$state_dir/{LegacyTagRecordFile}\"\n"
        + $"  state=\"$state_dir/{StateFile}\"\n"
        + "  unreadable() {\n"
        + "    echo \"Tailscale sidecar: could not read the registered node profile from $state: $1."
        + " Stop this sidecar, remove the node in the Tailscale admin console, and delete the state volume to register the node again.\" >&2\n"
        + "    exit 1\n"
        + "  }\n"
        + "  entry() {\n"
        + "    sed -n \"s/.*\\\"$1\\\"[[:space:]]*:[[:space:]]*\\\"\\([^\\\"]*\\)\\\".*/\\1/p\" \"$state\" | head -n 1\n"
        + "  }\n"
        + "  is_base64() {\n"
        + "    printf '%s' \"$1\" | grep -Eq '^[A-Za-z0-9+/]+={0,2}$'\n"
        + "  }\n"
        + "  if [ -f \"$state\" ] && grep -q '\"_current-profile\"' \"$state\"; then\n"
        + "    grep -Eq '\"_current-profile\"[[:space:]]*:[[:space:]]*\"' \"$state\" || unreadable \"the _current-profile entry is not a string\"\n"
        + "    current=$(entry _current-profile)\n"
        + "    if [ -n \"$current\" ]; then\n"
        + "      is_base64 \"$current\" || unreadable \"the _current-profile entry is not base64\"\n"
        + "      profile=$(printf '%s' \"$current\" | base64 -d) || unreadable \"the _current-profile entry does not decode\"\n"
        + "      printf '%s' \"$profile\" | grep -Eq '^profile-[0-9a-f]+$' || unreadable \"'$profile' is not a profile key\"\n"
        + "      grep -Eq \"\\\"$profile\\\"[[:space:]]*:[[:space:]]*\\\"\" \"$state\" || unreadable \"profile '$profile' is missing\"\n"
        + "      encoded=$(entry \"$profile\")\n"
        + "      is_base64 \"$encoded\" || unreadable \"profile '$profile' is not base64\"\n"
        + "      decoded=$(printf '%s' \"$encoded\" | base64 -d) || unreadable \"profile '$profile' does not decode\"\n"
        + "      prefs=$(printf '%s' \"$decoded\" | tr -d '\\n\\t ')\n"
        + "      case \"$prefs\" in *'\"ControlURL\"'*) ;; *) unreadable \"profile '$profile' does not contain prefs\" ;; esac\n"
        + "      case \"$prefs\" in *'\"AdvertiseTags\":'*) ;; *) unreadable \"profile '$profile' has no AdvertiseTags\" ;; esac\n"
        + "      registered=$(printf '%s' \"$prefs\" | sed -n 's/.*\"AdvertiseTags\":\\[\\([^]]*\\)\\].*/\\1/p' | tr ',' '\\n' | tr -d '\"' | grep . | sort -u) || true\n"
        + $"      requested=$(printf '%s' \"${TagsVariable}\" | tr ',' '\\n' | grep . | sort -u) || true\n"
        + "      if [ \"$registered\" != \"$requested\" ]; then\n"
        + "        echo \"Tailscale sidecar: the node on this state volume registered with tags"
        + " [$(printf '%s' \"$registered\" | tr '\\n' ' ' | sed 's/ $//')]"
        + " but the AppHost now requests [$(printf '%s' \"$requested\" | tr '\\n' ' ' | sed 's/ $//')]."
        + " Tailscale assigns tags when it registers a node. To change them: stop this sidecar,"
        + " remove the node in the Tailscale admin console, delete the state volume, and deploy again.\" >&2\n"
        + "        exit 1\n"
        + "      fi\n"
        + "    fi\n"
        + "  fi\n"
        + "fi\n"
        + "exec containerboot";
}
