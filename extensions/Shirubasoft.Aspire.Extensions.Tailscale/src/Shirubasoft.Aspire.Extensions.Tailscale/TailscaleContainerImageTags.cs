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
    // before accepting either a pre-registration store or one Linux profile.
    // Profile metadata must select exactly the prefs checked below. Empty files
    // and incomplete registrations require resetting the volume. Prefs
    // use tab-indented JSON; validate their nesting, keys, values and tag grammar
    // before comparing the sets. Unrecognised serialization stops the sidecar.
    // State and prefs key allowlists follow the pinned upstream types. The
    // literal schema version below forces image updates to refresh this contract.
    // Only the pinned image's BusyBox tools are used, and validation takes one
    // snapshot of the store without scratch files or writes to the state volume.
    // Raw literals inherit checkout line endings; the container's shell requires LF.
    public static readonly string StartScript = $$"""
        # State schema: v1.102.5
        set -eu
        export LC_ALL=C
        invalid_environment() {
          echo "Tailscale sidecar: unsupported startup environment: $1. Restore the AppHost-generated Tailscale environment before starting this sidecar." >&2
          exit 1
        }
        [ "${TS_AUTH_ONCE:-}" = true ] && [ "${TS_USERSPACE:-}" = true ] || invalid_environment "TS_AUTH_ONCE and TS_USERSPACE must be true"
        printf '%s' "${TAILSCALE_TAGS:-}" | awk '
          $0 !~ /^tag:[A-Za-z][A-Za-z0-9-]*(,tag:[A-Za-z][A-Za-z0-9-]*)*$/ { exit 1 }
          END { if (NR != 1) exit 1 }
        ' || invalid_environment "TAILSCALE_TAGS must be a comma-separated list of valid tags"
        [ "${TS_EXTRA_ARGS:-}" = "--advertise-tags=$TAILSCALE_TAGS" ] || invalid_environment "TS_EXTRA_ARGS must advertise exactly TAILSCALE_TAGS"
        [ -z "${TS_TAILSCALED_EXTRA_ARGS:-}${TS_EXPERIMENTAL_VERSIONED_CONFIG_DIR:-}${TS_KUBE_SECRET:-}${KUBERNETES_SERVICE_HOST:-}${TS_TEST_ONLY_ROOT:-}${TS_DEBUG_FAKE_GOOS:-}" ] || invalid_environment "alternate daemon arguments, config files, state stores and roots are unsupported"
        # Disk-cached netmaps are another startup input outside the validated store.
        # Obtain the node and its assigned tags from control instead of replaying a cache.
        export TS_USE_CACHED_NETMAP=false
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
              # ipn/store.go:24-86, ipn/serve.go:28-30, ipn/ipnlocal/profiles.go:494-503,
              # and ipn/ipnlocal/local.go:839-884,8584-8588.
              # Linux can migrate _daemon at startup; this sidecar only accepts native profile state.
              function state_key(key) {
                return key ~ /^(_machinekey|_profiles|_current-profile|_taildrop-received|_debug_(magicsock|sockstats|syspolicy)_until)$/ ||
                  key ~ /^(profile-[0-9a-f]+|_serve\057[0-9a-f]+|profile-[0-9a-f]+[|][|]_routeInfo)$/
              }
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
                if ($0 !~ /^  "[A-Za-z0-9_\057\174-]+": "[A-Za-z0-9+\057=]*",?$/) fail()
                split($0, fields, "\"")
                if (!state_key(fields[2]) || seen[tolower(fields[2])]++) fail()
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
              metadata=$(entry _profiles)
              [ -n "$metadata" ] || unreadable "the _profiles metadata is missing or empty"
              decode "$metadata" "the _profiles metadata"
              # writeKnownProfiles uses compact JSON. Parse the entire typed Linux
              # LoginProfile map, including nested objects, before following any link.
              id=$(printf '%s' "$decoded" | awk '
                function fail() { exit 1 }
                function take(value) {
                  if (substr(doc, pos, length(value)) != value) fail()
                  pos += length(value)
                }
                function string_value(remaining, value) {
                  remaining = substr(doc, pos)
                  if (!match(remaining, /^"([^"\\[:cntrl:]]|\\(["\\\057bfnrt]|u[0-9a-fA-F]{4}))*"/)) fail()
                  value = substr(remaining, 1, RLENGTH)
                  pos += RLENGTH
                  return value
                }
                function fields(scope, names, list, n, i) {
                  n = split(names, list, " ")
                  for (i = 1; i <= n; i++) allowed[scope, list[i]] = 1
                }
                BEGIN {
                  # ipn/prefs.go:1052-1124, tailcfg/tailcfg.go:289-302.
                  fields("Profile", "ID Name NetworkProfile Key UserProfile NodeID LocalUserID ControlURL Created")
                  fields("NetworkProfile", "MagicDNSName DomainName DisplayName")
                  fields("UserProfile", "ID LoginName DisplayName ProfilePicURL Groups")
                }
                function object(scope, name, key, value, count, required, n, i) {
                  take("{")
                  if (substr(doc, pos, 1) == "}") fail()
                  while (1) {
                    name = string_value()
                    # Keys and selection values must be literal, exact upstream names.
                    key = substr(name, 2, length(name) - 2)
                    if (!allowed[scope, key] || seen[scope, tolower(key)]++) fail()
                    take(":")
                    if (scope == "Profile" && (key == "NetworkProfile" || key == "UserProfile")) {
                      object(key)
                    } else if (scope == "UserProfile" && key == "Groups") {
                      take("[")
                      if (substr(doc, pos, 1) != "]") {
                        while (1) {
                          string_value()
                          if (substr(doc, pos, 1) == "]") break
                          take(",")
                        }
                      }
                      take("]")
                    } else if (scope == "UserProfile" && key == "ID") {
                      if (!match(substr(doc, pos), /^-?(0|[1-9][0-9]*)/)) fail()
                      pos += RLENGTH
                    } else {
                      value = string_value()
                      if (scope == "Profile") {
                        if (key == "ID" && value != "\"" id "\"") fail()
                        if (key == "Key" && value != "\"profile-" id "\"") fail()
                        if (key == "LocalUserID" && value != "\"\"") fail()
                        if (key == "Created" && value !~ /^"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})"$/) fail()
                      }
                    }
                    count++
                    if (substr(doc, pos, 1) == "}") break
                    take(",")
                  }
                  take("}")
                  # These fields always serialize, including their zero values.
                  if (scope == "Profile") {
                    n = split("id name networkprofile key userprofile nodeid localuserid controlurl", required, " ")
                    for (i = 1; i <= n; i++) if (!seen[scope, required[i]]) fail()
                  }
                  if (scope == "NetworkProfile" && count != 3) fail()
                  if (scope == "UserProfile" && (!seen[scope, "id"] || !seen[scope, "loginname"] || !seen[scope, "displayname"])) fail()
                }
                NR != 1 { fail() }
                {
                  doc = $0; pos = 1
                  take("{")
                  name = string_value()
                  if (name !~ /^"[0-9a-f]{4}"$/) fail()
                  id = substr(name, 2, 4)
                  take(":")
                  object("Profile")
                  # Exactly one entry: ordinary up/reauth never creates a second profile.
                  take("}")
                  if (pos != length(doc) + 1) fail()
                  print id
                }
              ') || unreadable "the _profiles metadata is not one complete Linux profile with matching ID and Key"
              [ "$profile" = "profile-$id" ] || unreadable "the _current-profile entry does not match the _profiles metadata"
              [ -n "$(entry _machinekey)" ] || unreadable "the registered profile has no machine key"
              printf '%s\n' "$store" | awk -v profile="$profile" -v id="$id" '
                {
                  split($0, fields, "\""); key = fields[2]
                  if (key ~ /^profile-/ && key != profile && key != profile "||_routeInfo") exit 1
                  if (key ~ /^_serve\057/ && key != "_serve/" id) exit 1
                }
              ' || unreadable "the store contains orphan or extra profile keys"
              encoded=$(entry "$profile")
              [ -n "$encoded" ] || unreadable "profile '$profile' is missing or empty"
              decode "$encoded" "profile '$profile'"
              registered=$(printf '%s' "$decoded" | awk '
                function fail() { bad = 1; exit 1 }
                function allow_keys(scope, keys, fields, n, i) {
                  n = split(keys, fields, " ")
                  for (i = 1; i <= n; i++) allowed[scope, fields[i]] = 1
                }
                BEGIN {
                  # v1.102.5: ipn/prefs.go:59-349, types/persist/persist.go:21-36,
                  # tailcfg/tailcfg.go:289-302, drive/remote.go:29-49,
                  # and feature/tpm/attestation.go:144-147. Use JSON names, including Config and who.
                  allow_keys("Prefs", "ControlURL RouteAll ExitNodeID ExitNodeIP AutoExitNode InternalExitNodePrior ExitNodeAllowLANAccess CorpDNS RunSSH RunWebClient WantRunning LoggedOut ShieldsUp AdvertiseTags Hostname NotepadURLs ForceDaemon Egg AdvertiseRoutes AdvertiseServices Sync NoSNAT NoStatefulFiltering NetfilterMode OperatorUser ProfileName AutoUpdate AppConnector PostureChecking NetfilterKind RemoteConfig DriveShares RelayServerPort RelayServerStaticEndpoints Config")
                  allow_keys("AutoUpdate", "Check Apply")
                  allow_keys("AppConnector", "Advertise")
                  allow_keys("Config", "PrivateNodeKey OldPrivateNodeKey UserProfile NetworkLockKey NodeID AttestationKey DisallowedTKAStateIDs")
                  allow_keys("UserProfile", "ID LoginName DisplayName ProfilePicURL Groups")
                  allow_keys("AttestationKey", "tpmPrivate tpmPublic")
                  allow_keys("DriveShare", "name path who bookmarkData")
                  objects["Prefs", "AutoUpdate"] = "AutoUpdate"
                  objects["Prefs", "AppConnector"] = "AppConnector"
                  objects["Prefs", "Config"] = "Config"
                  objects["Config", "UserProfile"] = "UserProfile"
                  objects["Config", "AttestationKey"] = "AttestationKey"
                }
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
                  depth = 1; kind[depth] = "object"; scope[depth] = "Prefs"; node[depth] = ++serial
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
                    if (!allowed[scope[depth], key] || seen[node[depth], tolower(key)]++) fail()
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
                    child_scope = ""
                    if (line == "{") {
                      child_scope = (kind[depth] == "array" && scope[depth] == "DriveShares" ? "DriveShare" : objects[scope[depth], key])
                      if (!child_scope) fail()
                    } else if (scope[depth] == "Prefs" && key == "DriveShares") child_scope = "DriveShares"
                    depth++
                    kind[depth] = (line == "{" ? "object" : "array")
                    scope[depth] = child_scope
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
            else
              # Only the empty store or a machine key can precede registration.
              # Empty selectors, metadata and profile-scoped state are incomplete.
              printf '%s\n' "$store" | awk '
                NF { split($0, fields, "\""); if (fields[2] != "_machinekey" || !fields[4]) exit 1 }
              ' || unreadable "the store without a current profile is not pre-registration state"
            fi
          fi
        fi
        exec containerboot
        """.ReplaceLineEndings("\n");
}
