using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.TigerBeetle;

// The AMQP 0.9.1 connection settings that `tigerbeetle amqp` receives as separate options.
internal sealed record AmqpConnectionInfo(
    string Scheme,
    string Host,
    int Port,
    string UserName,
    string Password,
    string VirtualHost,
    bool UsesTls)
{
    private const string DefaultCredential = "guest";

    public static AmqpConnectionInfo Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var uri = ParseUri(connectionString);
        var credentials = uri.GetComponents(UriComponents.UserInfo, UriFormat.UriEscaped).Split(':', 2);
        var usesTls = uri.Scheme == "amqps";

        return new AmqpConnectionInfo(
            uri.Scheme,
            uri.Host,
            ResolvePort(uri, usesTls),
            DecodeOrDefault(credentials[0], DefaultCredential),
            DecodeOrDefault(credentials.ElementAtOrDefault(1), DefaultCredential),
            DecodeOrDefault(uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped), "/"),
            usesTls);
    }

    private static Uri ParseUri(string connectionString) =>
        Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) && IsAmqpUriWithHost(uri)
            ? uri
            : throw new DistributedApplicationException(
                "The AMQP connection string must be an absolute amqp:// or amqps:// URI with a host.");

    private static bool IsAmqpUriWithHost(Uri uri) =>
        IsAmqpScheme(uri.Scheme) && !string.IsNullOrWhiteSpace(uri.Host);

    private static bool IsAmqpScheme(string scheme) => scheme is "amqp" or "amqps";

    private static int ResolvePort(Uri uri, bool usesTls) => uri.Port > 0 ? uri.Port : DefaultPort(usesTls);

    private static int DefaultPort(bool usesTls) => usesTls ? 5671 : 5672;

    private static string DecodeOrDefault(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : Uri.UnescapeDataString(value);
}
