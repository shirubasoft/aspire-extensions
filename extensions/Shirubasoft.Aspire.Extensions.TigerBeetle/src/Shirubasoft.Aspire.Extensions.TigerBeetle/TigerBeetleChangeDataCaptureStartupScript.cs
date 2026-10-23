using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.TigerBeetle;

// Resolves the TigerBeetle and AMQP endpoints inside the container, because `tigerbeetle amqp` accepts only
// numeric addresses, then decodes the AMQP URI into the separate options that the command requires.
internal static class TigerBeetleChangeDataCaptureStartupScript
{
    public const string TigerBeetleAddressesEnvironmentVariable = "TIGERBEETLE_CDC_ADDRESSES";
    public const string AmqpUriEnvironmentVariable = "TIGERBEETLE_CDC_AMQP_URI";

    // Raw literals inherit checkout line endings; the container's shell requires LF.
    private static readonly string ResolveConnectionScript = $$"""
        set -eu
        uri_decode() {
          printf '%s\n' "$1" | awk '
            function hex_digit(value) {
              if (value >= "0" && value <= "9") return value + 0
              value = tolower(value)
              return index("abcdef", value) + 9
            }
            {
              output = ""
              for (position = 1; position <= length($0); position++) {
                character = substr($0, position, 1)
                if (character == "%" && position + 2 <= length($0)) {
                  first = substr($0, position + 1, 1)
                  second = substr($0, position + 2, 1)
                  if (first ~ /^[0-9A-Fa-f]$/ && second ~ /^[0-9A-Fa-f]$/) {
                    output = output sprintf("%c", hex_digit(first) * 16 + hex_digit(second))
                    position += 2
                    continue
                  }
                }
                output = output character
              }
              printf "%s", output
            }'
        }
        resolve_endpoint() {
          endpoint="$(printf '%s' "$1" | awk '{$1=$1};1')"
          case "$endpoint" in
            \[*\]:*) host="${endpoint#\[}"; host="${host%%\]*}"; port="${endpoint##*:}" ;;
            *:*:*) printf '%s\n' "$endpoint"; return ;;
            *:*) host="${endpoint%:*}"; port="${endpoint##*:}" ;;
            *) host="$endpoint"; port="" ;;
          esac
          resolved="$(getent ahostsv4 "$host" | awk 'NR == 1 { print $1 }')"
          if [ -z "$resolved" ]; then
            resolved="$(getent ahosts "$host" | awk 'NR == 1 { print $1 }')"
          fi
          if [ -z "$resolved" ]; then
            echo "Could not resolve CDC endpoint host '$host'." >&2
            return 1
          fi
          if [ -z "$port" ]; then
            printf '%s\n' "$resolved"
          elif printf '%s' "$resolved" | grep -q ':'; then
            printf '[%s]:%s\n' "$resolved" "$port"
          else
            printf '%s:%s\n' "$resolved" "$port"
          fi
        }
        amqp_uri="${{{AmqpUriEnvironmentVariable}}%%#*}"
        amqp_uri="${amqp_uri%%\?*}"
        case "$amqp_uri" in
          amqp://*) scheme="amqp"; default_port="5672" ;;
          amqps://*)
            scheme="amqps"
            default_port="5671"
            echo "WARNING: TigerBeetle CDC does not support native AMQP TLS. Configure a TLS tunnel; this connection will be attempted without TLS." >&2
            ;;
          *) echo "The CDC AMQP connection string must use amqp:// or amqps://." >&2; exit 1 ;;
        esac
        authority_and_path="${amqp_uri#*://}"
        authority="${authority_and_path%%/*}"
        if [ "$authority" = "$authority_and_path" ]; then
          encoded_vhost=""
        else
          encoded_vhost="${authority_and_path#*/}"
        fi
        case "$authority" in
          *@*) user_info="${authority%@*}"; host_port="${authority##*@}" ;;
          *) user_info=""; host_port="$authority" ;;
        esac
        case "$user_info" in
          *:*) encoded_user="${user_info%%:*}"; encoded_password="${user_info#*:}" ;;
          *) encoded_user="$user_info"; encoded_password="" ;;
        esac
        if [ -n "$encoded_user" ]; then amqp_user="$(uri_decode "$encoded_user")"; else amqp_user="guest"; fi
        if [ -n "$encoded_password" ]; then amqp_password="$(uri_decode "$encoded_password")"; else amqp_password="guest"; fi
        if [ -n "$encoded_vhost" ]; then amqp_vhost="$(uri_decode "$encoded_vhost")"; else amqp_vhost="/"; fi
        case "$host_port" in
          \[*\]:*) amqp_host_name="${host_port#\[}"; amqp_host_name="${amqp_host_name%%\]*}"; amqp_port="${host_port##*:}" ;;
          \[*\]) amqp_host_name="${host_port#\[}"; amqp_host_name="${amqp_host_name%\]}"; amqp_port="$default_port" ;;
          *:*:*) echo "An IPv6 AMQP host must be enclosed in brackets." >&2; exit 1 ;;
          *:*) amqp_host_name="${host_port%:*}"; amqp_port="${host_port##*:}" ;;
          *) amqp_host_name="$host_port"; amqp_port="$default_port" ;;
        esac
        amqp_host_name="$(uri_decode "$amqp_host_name")"
        case "$amqp_port" in ''|*[!0-9]*) echo "The CDC AMQP port is invalid." >&2; exit 1 ;; esac
        if [ "$amqp_port" -lt 1 ] || [ "$amqp_port" -gt 65535 ]; then echo "The CDC AMQP port is invalid." >&2; exit 1; fi
        case "$amqp_host_name" in *:*) amqp_endpoint="[$amqp_host_name]:$amqp_port" ;; *) amqp_endpoint="$amqp_host_name:$amqp_port" ;; esac
        tigerbeetle_addresses=""
        old_ifs="$IFS"
        IFS=,
        for endpoint in ${{{TigerBeetleAddressesEnvironmentVariable}}}; do
          resolved="$(resolve_endpoint "$endpoint")"
          if [ -z "$tigerbeetle_addresses" ]; then
            tigerbeetle_addresses="$resolved"
          else
            tigerbeetle_addresses="$tigerbeetle_addresses,$resolved"
          fi
        done
        IFS="$old_ifs"
        amqp_host="$(resolve_endpoint "$amqp_endpoint")"
        """.ReplaceLineEndings("\n");

    public static string Build(TigerBeetleChangeDataCaptureResource resource)
    {
        var clusterId = ShellQuoting.Quote(resource.TigerBeetle.ClusterId);
        var publishExchange = ShellQuoting.Quote(resource.PublishExchange);

        return string.Join(
            '\n',
            ResolveConnectionScript + VirtualHostOverride(resource),
            "printf 'TigerBeetle CDC starting: cluster=%s addresses=%s amqp_host=%s vhost=%s publish_exchange=%s\\n' " +
                $"{clusterId} \"$tigerbeetle_addresses\" \"$amqp_host\" \"$amqp_vhost\" {publishExchange}",
            "exec /sbin/tini -- /tigerbeetle amqp --addresses=\"$tigerbeetle_addresses\" " +
                $"--cluster={clusterId} --host=\"$amqp_host\" --vhost=\"$amqp_vhost\" --user=\"$amqp_user\" " +
                $"--password=\"$amqp_password\" --publish-exchange={publishExchange}" +
                ShellQuoting.Option("--publish-routing-key", resource.PublishRoutingKey) +
                ShellQuoting.Option("--timestamp-last", resource.TimestampLast) +
                ShellQuoting.QuoteEach(resource.AdditionalArguments));
    }

    private static string VirtualHostOverride(TigerBeetleChangeDataCaptureResource resource) =>
        resource.VirtualHost is null ? string.Empty : $"\namqp_vhost={ShellQuoting.Quote(resource.VirtualHost)}";
}
