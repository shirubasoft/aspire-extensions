namespace Aspire.Hosting.TigerBeetle;

internal static class ShellQuoting
{
    public static string Quote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    public static string QuoteEach(IEnumerable<string> arguments) =>
        string.Concat(arguments.Select(argument => $" {Quote(argument)}"));

    public static string Option(string name, string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : $" {name}={Quote(value)}";
}
