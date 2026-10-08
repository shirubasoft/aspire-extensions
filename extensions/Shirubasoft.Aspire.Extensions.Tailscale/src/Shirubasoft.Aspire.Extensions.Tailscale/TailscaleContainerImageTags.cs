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
    // The pinned tailscaled writes a two-space-indented object of base64
    // strings, or "{}" for its initial empty store. Validate the entire store
    // before treating an absent or empty "_current-profile" as fresh registration. Empty files are corrupt. Prefs
    // use tab-indented JSON; validate their nesting, keys, values and tag grammar
    // before comparing the sets. Unrecognised serialization stops the sidecar.
    // Only the pinned image's BusyBox tools are used, and validation takes one
    // snapshot of the store without scratch files or writes to the state volume.
    public const string StartScript = $$"""
        set -eu
        export LC_ALL=C
        printf '%s' "${{ServeConfigVariable}}" > "$TS_SERVE_CONFIG"
        state_dir="${TS_STATE_DIR:-}"
        if [ -n "$state_dir" ]; then
          mkdir -p "$state_dir"
          rm -f "$state_dir/{{LegacyTagRecordFile}}"
          state="$state_dir/{{StateFile}}"
          unreadable() {
            echo "Tailscale sidecar: could not read the registered node profile from $state: $1. Stop this sidecar, remove the node in the Tailscale admin console, and delete the state volume to register the node again." >&2
            exit 1
          }
          entry() {
            printf '%s\n' "$store" | sed -n "s/^  \"$1\": \"\([^\"]*\)\",\{0,1\}$/\1/p"
          }
          decode() {
            decoded=$(printf '%s' "$1" | base64 -d) || unreadable "$2 does not decode"
            roundtrip=$(printf '%s' "$decoded" | base64 | tr -d '\n')
            [ "$roundtrip" = "$1" ] || unreadable "$2 is not canonical base64 data"
          }
          if [ -e "$state" ] || [ -L "$state" ]; then
            [ -f "$state" ] && [ -r "$state" ] || unreadable "the state is not a readable file"
            encoded_store=$(base64 "$state") || unreadable "the state file could not be read"
            encoded_store=$(printf '%s' "$encoded_store" | tr -d '\n')
            store=$(printf '%s' "$encoded_store" | base64 -d | awk '
              function fail() { bad = 1; exit 1 }
              NR == 1 {
                if ($0 == "{}") { closed = 1; next }
                if ($0 != "{") fail()
                next
              }
              {
                if (closed) fail()
                if ($0 == "}") {
                  if (!count || comma) fail()
                  closed = 1
                  next
                }
                if (count && !comma) fail()
                # Octal slash keeps BusyBox awk from admitting backslashes into the key alphabet.
                if ($0 !~ /^  "[A-Za-z0-9_\057-]+": "[A-Za-z0-9+\057=]*",?$/) fail()
                split($0, fields, "\"")
                if (seen[fields[2]]++) fail()
                if (fields[4] !~ /^([A-Za-z0-9+\057]{4})*([A-Za-z0-9+\057][AQgw]==|[A-Za-z0-9+\057]{2}[AEIMQUYcgkosw048]=)?$/) fail()
                comma = ($0 ~ /,$/)
                lines[++count] = $0
              }
              END {
                if (bad || !closed) exit 1
                for (i = 1; i <= count; i++) print lines[i]
              }
            ') || unreadable "unrecognised state format"
            # Byte comparison catches data that the shell or BusyBox would discard, including NUL.
            if [ -n "$store" ]; then
              canonical=$(printf '{\n%s\n}' "$store")
            else
              canonical='{}'
            fi
            exact=$(printf '%s' "$canonical" | base64 | tr -d '\n')
            terminated=$(printf '%s\n' "$canonical" | base64 | tr -d '\n')
            [ "$encoded_store" = "$exact" ] || [ "$encoded_store" = "$terminated" ] || unreadable "unrecognised state format"
            current=$(entry _current-profile)
            if [ -n "$current" ]; then
              decode "$current" "the _current-profile entry"
              profile=$decoded
              printf '%s' "$profile" | grep -Eq '^profile-[0-9a-f]+$' || unreadable "the current profile is not a profile key"
              encoded=$(entry "$profile")
              [ -n "$encoded" ] || unreadable "profile '$profile' is missing or empty"
              decode "$encoded" "profile '$profile'"
              registered=$(printf '%s' "$decoded" | awk '
                function fail() { bad = 1; exit 1 }
                function json_string(value) {
                  return value ~ /^"([^"\\[:cntrl:]]|\\(["\\\057bfnrt]|u[0-9a-fA-F]{4}))*"$/
                }
                function scalar(value) {
                  return json_string(value) || value ~ /^(null|true|false|\[\]|\{\}|-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?)$/
                }
                # Normalise CRLF outside strings; indentation and string contents remain intact.
                { sub(/\r$/, "") }
                NR == 1 {
                  if ($0 != "{") fail()
                  depth = 1; kind[depth] = "object"; node[depth] = ++serial
                  next
                }
                {
                  if (!depth) fail()
                  line = $0
                  indent = 0
                  while (substr(line, 1, 1) == "\t") { indent++; line = substr(line, 2) }
                  comma = (line ~ /,$/)
                  if (comma) line = substr(line, 1, length(line) - 1)
                  if (line == "}" || line == "]") {
                    if (indent != depth - 1 || !count[depth] || after[depth]) fail()
                    if (line != (kind[depth] == "object" ? "}" : "]")) fail()
                    depth--
                    if (!depth && comma) fail()
                    after[depth] = comma
                    next
                  }
                  if (indent != depth || (count[depth] && !after[depth])) fail()
                  key = ""
                  if (kind[depth] == "object") {
                    if (line !~ /^"[A-Za-z0-9_\057-]+": /) fail()
                    split(line, fields, "\"")
                    key = fields[2]
                    if (seen[node[depth], key]++) fail()
                    line = substr(line, length(key) + 5)
                  }
                  count[depth]++
                  after[depth] = comma
                  is_tags = (depth == 1 && key == "AdvertiseTags")
                  if (is_tags) {
                    if (line != "[" && line != "[]" && line != "null") fail()
                    have_tags = 1
                  }
                  if (depth == 1 && key == "ControlURL") {
                    if (!json_string(line)) fail()
                    have_control = 1
                  }
                  if (tags[depth]) {
                    if (line !~ /^"tag:[A-Za-z][A-Za-z0-9-]*"$/) fail()
                    values[++tag_count] = substr(line, 2, length(line) - 2)
                  }
                  if (line == "{" || line == "[") {
                    if (comma) fail()
                    depth++
                    kind[depth] = (line == "{" ? "object" : "array")
                    node[depth] = ++serial; count[depth] = 0; after[depth] = 0
                    tags[depth] = is_tags
                  } else if (!scalar(line)) fail()
                }
                END {
                  if (bad || depth || !have_control || !have_tags) exit 1
                  for (i = 1; i <= tag_count; i++) print values[i]
                }
              ') || unreadable "profile '$profile' has invalid prefs or AdvertiseTags"
              registered=$(printf '%s' "$registered" | sort -u)
              requested=$(printf '%s' "${{TagsVariable}}" | tr ',' '\n' | grep . | sort -u) || true
              if [ "$registered" != "$requested" ]; then
                echo "Tailscale sidecar: the node on this state volume registered with tags [$(printf '%s' "$registered" | tr '\n' ' ' | sed 's/ $//')] but the AppHost now requests [$(printf '%s' "$requested" | tr '\n' ' ' | sed 's/ $//')]. Tailscale assigns tags when it registers a node. To change them: stop this sidecar, remove the node in the Tailscale admin console, delete the state volume, and deploy again." >&2
                exit 1
              fi
            fi
          fi
        fi
        exec containerboot
        """;
}
