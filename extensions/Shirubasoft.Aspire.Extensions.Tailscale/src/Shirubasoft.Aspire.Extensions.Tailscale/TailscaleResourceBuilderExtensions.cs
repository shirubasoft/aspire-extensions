using System.Buffers;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Exposes Aspire resources on a Tailscale tailnet.
/// </summary>
public static class TailscaleResourceBuilderExtensions
{
    /// <summary>
    /// Adds a tailnet that resources join through Tailscale sidecar nodes.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The tailnet resource name.</param>
    /// <param name="tags">The tags that sidecar nodes advertise. The default is <c>tag:apps</c>.</param>
    /// <returns>The tailnet resource builder.</returns>
    public static IResourceBuilder<TailnetResource> AddTailnet(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var secret = builder
            .AddParameter($"{name}-oauth-client-secret", secret: true)
            .WithDescription("The Tailscale OAuth client secret that registers sidecar nodes.");
        var tailnet = new TailnetResource(
            name,
            secret.Resource,
            ValidateTags(tags ?? [TailscaleSidecarDefaults.DefaultTag]));

        return builder.AddResource(tailnet)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Tailnet",
                State = KnownResourceStates.Running,
                Properties =
                [
                    new("Tags", string.Join(", ", tailnet.Tags)),
                ],
            });
    }

    /// <summary>
    /// Serves a resource endpoint on the tailnet with HTTPS through a Tailscale sidecar node.
    /// </summary>
    /// <typeparam name="T">The target resource type.</typeparam>
    /// <param name="builder">The target resource builder.</param>
    /// <param name="tailnet">The tailnet resource builder.</param>
    /// <param name="hostname">
    /// The node hostname on the tailnet. Run mode appends <c>-dev</c> to it.
    /// </param>
    /// <param name="endpointName">The endpoint name. The default is <c>http</c>.</param>
    /// <param name="tags">The tags that the node advertises. The default is the tailnet tags.</param>
    /// <returns>The target resource builder.</returns>
    public static IResourceBuilder<T> WithTailscale<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<TailnetResource> tailnet,
        string hostname,
        string endpointName = "http",
        IReadOnlyList<string>? tags = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tailnet);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ValidateHostname(hostname);

        var isRunMode = builder.ApplicationBuilder.ExecutionContext.IsRunMode;
        var sidecar = new TailscaleSidecarResource(
            $"{builder.Resource.Name}-ts",
            tailnet.Resource,
            builder.Resource,
            GetExistingEndpoint(builder, endpointName),
            GetNodeHostname(hostname, isRunMode),
            ValidateTags(tags ?? tailnet.Resource.Tags));

        AddSidecar(builder.ApplicationBuilder, sidecar, isRunMode);

        return builder;
    }

    private static void AddSidecar(
        IDistributedApplicationBuilder builder,
        TailscaleSidecarResource sidecar,
        bool isRunMode)
    {
        var tags = string.Join(',', sidecar.Tags);
        var node = builder.AddResource(sidecar)
            .WithImage(TailscaleContainerImageTags.Image, TailscaleContainerImageTags.Tag)
            .WithImageRegistry(TailscaleContainerImageTags.Registry)
            .WithParentRelationship(sidecar.Target)
            .WithReferenceRelationship(sidecar.Tailnet)
            .WaitFor(builder.CreateResourceBuilder((IResource)sidecar.Target))
            .WithEntrypoint(TailscaleSidecarDefaults.Entrypoint)
            .WithArgs("-c", TailscaleSidecarDefaults.StartScript)
            .WithEnvironment("TS_HOSTNAME", sidecar.Hostname)
            .WithEnvironment("TS_AUTH_ONCE", "true")
            .WithEnvironment("TS_USERSPACE", "true")
            .WithEnvironment("TS_EXTRA_ARGS", $"--advertise-tags={tags}")
            .WithEnvironment(TailscaleSidecarDefaults.TagsVariable, tags)
            .WithEnvironment("TS_SERVE_CONFIG", TailscaleSidecarDefaults.ServeConfigPath)
            .WithEnvironment("TS_AUTHKEY", GetAuthKey(sidecar.Tailnet.OAuthClientSecret, isRunMode))
            .WithEnvironment(context => ConfigureServeAsync(context, sidecar));

        if (!isRunMode)
        {
            node.WithVolume($"{sidecar.Name}-state", TailscaleSidecarDefaults.StateDirectory)
                .WithEnvironment("TS_STATE_DIR", TailscaleSidecarDefaults.StateDirectory);
        }
    }

    // A development node never takes the production hostname.
    internal static string GetNodeHostname(string hostname, bool isRunMode) =>
        isRunMode ? hostname + TailscaleSidecarDefaults.DevelopmentHostnameSuffix : hostname;

    // An OAuth client secret acts as an auth key and accepts these query parameters.
    private static ReferenceExpression GetAuthKey(ParameterResource secret, bool isRunMode)
    {
        var ephemeral = isRunMode ? "true" : "false";
        return ReferenceExpression.Create($"{secret}?ephemeral={ephemeral}&preauthorized=true");
    }

    private static async Task ConfigureServeAsync(
        EnvironmentCallbackContext context,
        TailscaleSidecarResource sidecar)
    {
        var proxyUrl = await GetProxyUrlAsync(
            sidecar,
            context.ExecutionContext,
            context.CancellationToken).ConfigureAwait(false);

        context.EnvironmentVariables[TailscaleSidecarDefaults.ServeConfigVariable] =
            TailscaleServeConfig.Create(proxyUrl, GetCertDomain(context.ExecutionContext));
    }

    private static async Task<string> GetProxyUrlAsync(
        TailscaleSidecarResource sidecar,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var proxyUrl = executionContext.IsRunMode
            ? await sidecar.TargetEndpoint.GetValueAsync(cancellationToken).ConfigureAwait(false)
            : await TailscaleProxyTarget
                .GetComposeUrl(sidecar.Target, sidecar.TargetEndpoint.EndpointName)
                .GetValueAsync(cancellationToken)
                .ConfigureAwait(false);

        return proxyUrl ?? throw new InvalidOperationException(
            $"Endpoint '{sidecar.TargetEndpoint.EndpointName}' for resource " +
            $"'{sidecar.Target.Name}' could not be resolved.");
    }

    private static string GetCertDomain(DistributedApplicationExecutionContext executionContext) =>
        executionContext.IsPublishMode
            ? TailscaleServeConfig.ComposeCertDomainPlaceholder
            : TailscaleServeConfig.CertDomainPlaceholder;

    private static EndpointReference GetExistingEndpoint<T>(
        IResourceBuilder<T> builder,
        string endpointName)
        where T : IResourceWithEndpoints
    {
        var endpoint = builder.GetEndpoint(
            endpointName,
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork);

        return endpoint.Exists
            ? endpoint
            : throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' has no endpoint named '{endpointName}'. " +
                "Declare the endpoint before calling WithTailscale.");
    }

    private static void ValidateHostname(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        if (!IsDnsLabel(hostname))
        {
            throw new ArgumentException(
                $"Hostname '{hostname}' must be a lowercase DNS label of at most 59 characters.",
                nameof(hostname));
        }
    }

    // A Tailscale machine name is a DNS label of at most 63 characters. The limit
    // leaves room for the run-mode suffix.
    private static bool IsDnsLabel(string hostname) =>
        hostname.Length <= 59
        && !HasHyphenEdge(hostname)
        && hostname.All(IsLabelCharacter);

    private static bool HasHyphenEdge(string hostname) =>
        hostname.StartsWith('-') || hostname.EndsWith('-');

    private static bool IsLabelCharacter(char character) =>
        char.IsAsciiLetterLower(character)
        || char.IsAsciiDigit(character)
        || character == '-';

    private static IReadOnlyList<string> ValidateTags(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0 || !tags.All(IsTag))
        {
            throw new ArgumentException(
                "Provide at least one Tailscale tag. A tag is 'tag:' followed by a letter " +
                "and then letters, digits, or hyphens.",
                nameof(tags));
        }

        return [.. tags];
    }

    // tailcfg.CheckTag: "tag:", a letter, then letters, digits, or hyphens. Anything
    // else would reach "tailscale up" as extra arguments or fail registration.
    private static bool IsTag(string? tag) =>
        tag is not null
        && tag.StartsWith("tag:", StringComparison.Ordinal)
        && IsTagName(tag.AsSpan(4));

    private static bool IsTagName(ReadOnlySpan<char> name) =>
        !name.IsEmpty
        && char.IsAsciiLetter(name[0])
        && name.IndexOfAnyExcept(TagNameCharacters) < 0;

    private static readonly SearchValues<char> TagNameCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-");
}
