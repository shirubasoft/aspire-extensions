using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal static class TailscaleServeConfig
{
    // containerboot replaces this placeholder with the node's MagicDNS name.
    public const string CertDomainPlaceholder = "${TS_CERT_DOMAIN}";

    // Docker Compose interpolates ${...} in the generated file; "$$" yields a literal "$".
    public const string ComposeCertDomainPlaceholder = "$${TS_CERT_DOMAIN}";

    public static string Create(string proxyUrl, string certDomain) =>
        new JsonObject
        {
            ["TCP"] = new JsonObject
            {
                ["443"] = new JsonObject { ["HTTPS"] = true },
            },
            ["Web"] = new JsonObject
            {
                [$"{certDomain}:443"] = new JsonObject
                {
                    ["Handlers"] = new JsonObject
                    {
                        ["/"] = new JsonObject { ["Proxy"] = proxyUrl },
                    },
                },
            },
        }.ToJsonString();
}

// Inside the Compose network the sidecar reaches the service on its container
// port. Compose resolves that port with the same API, so the URL stays in sync
// with the generated service definition.
internal static class TailscaleProxyTarget
{
    public static ReferenceExpression GetComposeUrl(
        IResourceWithEndpoints target,
        string endpointName)
    {
        var endpoint = target
            .ResolveEndpoints()
            .FirstOrDefault(resolved => string.Equals(
                resolved.Endpoint.Name,
                endpointName,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Resource '{target.Name}' has no endpoint named '{endpointName}'.");

        return ReferenceExpression.Create(
            $"{endpoint.Endpoint.UriScheme}://{target.Name}:{GetTargetPort(target, endpoint)}");
    }

    private static ReferenceExpression GetTargetPort(
        IResourceWithEndpoints target,
        ResolvedEndpoint endpoint)
    {
        if (endpoint.TargetPort.IsAllocated)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Endpoint.Name}' on resource '{target.Name}' has no fixed " +
                "target port. Set targetPort so the Tailscale sidecar can reach it in Docker Compose.");
        }

        return endpoint.TargetPort.Value is { } port
            ? ReferenceExpression.Create($"{port.ToString(CultureInfo.InvariantCulture)}")
            : ReferenceExpression.Create($"{new ContainerPortReference(target)}");
    }
}
