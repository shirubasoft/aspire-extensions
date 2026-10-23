using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Aspire.Hosting.TigerBeetle;

// TigerBeetle accepts a port, a numeric IP address, or a numeric IP endpoint. It does not resolve DNS names.
internal static class TigerBeetleAddresses
{
    public static void Validate(string addresses, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addresses, paramName);

        foreach (var address in addresses.Split(',', StringSplitOptions.TrimEntries))
        {
            ValidateEntry(address, paramName);
        }
    }

    public static int Count(string addresses) => addresses.Split(',', StringSplitOptions.TrimEntries).Length;

    public static bool IsValid(string address) =>
        IsNumber(address) ? IsPort(address) : IsIPAddressOrEndpoint(address);

    private static void ValidateEntry(string address, string paramName)
    {
        if (address.Length == 0)
        {
            throw new ArgumentException("TigerBeetle addresses cannot contain empty entries.", paramName);
        }

        if (!IsValid(address))
        {
            throw new ArgumentException(
                $"'{address}' is not a valid TigerBeetle address. Use a port, a numeric IPv4 or IPv6 address, or a numeric IP address with a port.",
                paramName);
        }
    }

    private static bool IsNumber(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool IsPort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && IsPortNumber(port);

    private static bool IsPortNumber(int port) => port is >= 1 and <= 65535;

    private static bool IsIPAddressOrEndpoint(string address) =>
        IPAddress.TryParse(address, out _) || IsEndpoint(address);

    private static bool IsEndpoint(string address) =>
        address.StartsWith('[') ? IsBracketedIPv6Endpoint(address[1..]) : IsIPv4Endpoint(address);

    private static bool IsBracketedIPv6Endpoint(string addressAfterOpeningBracket) =>
        IsHostAndPort(addressAfterOpeningBracket.Split("]:"), AddressFamily.InterNetworkV6);

    private static bool IsIPv4Endpoint(string address) =>
        IsHostAndPort(address.Split(':'), AddressFamily.InterNetwork);

    private static bool IsHostAndPort(string[] parts, AddressFamily family) =>
        parts.Length == 2 && IsHostAndPort(parts[0], parts[1], family);

    // IPAddress.TryParse accepts a bracketed IPv6 host, so reject brackets left inside the host.
    private static bool IsHostAndPort(string host, string port, AddressFamily family) =>
        !host.AsSpan().ContainsAny('[', ']') && IsIPAddress(host, family) && IsPort(port);

    private static bool IsIPAddress(string host, AddressFamily family) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == family;
}
