using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.TigerBeetle;

// Injects TigerBeetle's structured connection properties as <CONNECTION_NAME>_<PROPERTY> environment variables.
internal static class TigerBeetleReferenceEnvironment
{
    public static bool IncludesConnectionProperties(IResource consumer) =>
        !consumer.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var annotation) ||
        annotation.Flags.HasFlag(ReferenceEnvironmentInjectionFlags.ConnectionProperties);

    public static void Apply(EnvironmentCallbackContext context, TigerBeetleResource resource, string connectionName)
    {
        var prefix = Prefix(connectionName);
        foreach (var property in resource.GetConnectionProperties())
        {
            context.EnvironmentVariables[$"{prefix}{property.Key.ToUpperInvariant()}"] = property.Value;
        }
    }

    public static string Prefix(string connectionName) =>
        connectionName.Length == 0 ? string.Empty : $"{EncodeName(connectionName).ToUpperInvariant()}_";

    private static string EncodeName(string name)
    {
        var encoded = string.Concat(name.Select(EncodeCharacter));
        return char.IsAsciiDigit(encoded[0]) ? $"_{encoded}" : encoded;
    }

    private static char EncodeCharacter(char character) => char.IsAsciiLetterOrDigit(character) ? character : '_';
}
