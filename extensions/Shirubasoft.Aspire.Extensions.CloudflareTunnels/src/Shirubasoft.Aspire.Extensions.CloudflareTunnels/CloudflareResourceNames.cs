using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Hosting;

// Aspire accepts resource names of up to 64 ASCII letters, digits, and hyphens
// that start with a letter and contain no consecutive or trailing hyphens.
internal static class CloudflareResourceNames
{
    private const int MaxLength = 64;
    private const int HashLength = 8;

    private static readonly Regex ValidName = new(
        "^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex InvalidCharacters = new(
        "[^A-Za-z0-9]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string ForRoute(string tunnelName, string hostname) =>
        Create($"{tunnelName}-route", hostname);

    // The prefix starts with a resource name, so every result starts with a letter.
    // A hostname that cannot appear verbatim gets a shortened name with a hash of the
    // prefix and hostname, which keeps the name stable and distinct from other routes.
    private static string Create(string prefix, string hostname)
    {
        var name = $"{prefix}-{hostname.Replace('.', '-')}";

        return IsValid(name) ? name : CreateHashedName(prefix, hostname);
    }

    private static bool IsValid(string name) =>
        name.Length <= MaxLength && ValidName.IsMatch(name);

    private static string CreateHashedName(string prefix, string hostname)
    {
        var readable = InvalidCharacters.Replace($"{prefix}-{hostname}", "-");
        var shortened = readable[..Math.Min(readable.Length, MaxLength - HashLength - 1)]
            .TrimEnd('-');

        return $"{shortened}-{Hash($"{prefix}/{hostname.ToLowerInvariant()}")}";
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..HashLength];
}
