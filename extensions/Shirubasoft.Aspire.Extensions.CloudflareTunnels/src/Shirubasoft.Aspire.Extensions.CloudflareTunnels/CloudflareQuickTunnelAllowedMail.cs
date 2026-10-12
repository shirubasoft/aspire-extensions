using System.Collections.Immutable;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Holds the normalized recipient rules that cloudflared receives as <c>--allowed-mail</c> values.
/// </summary>
internal sealed record CloudflareQuickTunnelAllowedMailAnnotation(
    ImmutableArray<string> Rules) : IResourceAnnotation;

/// <summary>
/// Validates Quick Tunnel recipient rules with the limits that cloudflared enforces at startup.
/// </summary>
internal static class CloudflareQuickTunnelAllowedMail
{
    internal const int MaxRules = 100;
    internal const int MaxRuleBytes = 320;
    private const string WildcardPrefix = "*@";
    private const string ParameterName = "recipients";

    public static ImmutableArray<string> Merge(
        ImmutableArray<string> existing,
        IEnumerable<string> values)
    {
        var added = values.SelectMany(SplitRules).Select(NormalizeRule).ToArray();
        if (added.Length == 0)
        {
            throw new ArgumentException("At least one allowed mail rule is required.", ParameterName);
        }

        var rules = existing.Concat(added).Distinct(StringComparer.Ordinal).ToImmutableArray();
        return rules.Length <= MaxRules
            ? rules
            : throw new ArgumentException(
                $"Cloudflare Quick Tunnels accept at most {MaxRules} allowed mail rules.",
                ParameterName);
    }

    internal static string NormalizeRule(string value)
    {
        var rule = value.Trim().ToLowerInvariant();
        if (Encoding.UTF8.GetByteCount(rule) > MaxRuleBytes)
        {
            throw new ArgumentException(
                $"An allowed mail rule exceeds the {MaxRuleBytes}-byte limit.",
                ParameterName);
        }

        return IsValidRule(rule)
            ? rule
            : throw new ArgumentException(
                "Each allowed mail rule must be an email address or a '*@domain' wildcard.",
                ParameterName);
    }

    private static IEnumerable<string> SplitRules(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, ParameterName);
        return value.Split(',');
    }

    private static bool IsValidRule(string rule) =>
        rule.StartsWith(WildcardPrefix, StringComparison.Ordinal)
            ? IsValidDomain(rule[WildcardPrefix.Length..])
            : IsValidAddress(rule);

    private static bool IsValidAddress(string rule) =>
        MailAddress.TryCreate(rule, out var address)
        && address.Address == rule
        && IsValidDomain(address.Host);

    private static bool IsValidDomain(string domain)
    {
        try
        {
            // IDNs must be supplied as punycode, as cloudflared requires.
            return new IdnMapping { UseStd3AsciiRules = true }.GetAscii(domain) == domain;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
