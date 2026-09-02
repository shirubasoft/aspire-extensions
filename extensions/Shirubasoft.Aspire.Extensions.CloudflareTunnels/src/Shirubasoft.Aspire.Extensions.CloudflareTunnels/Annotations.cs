using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal sealed record CloudflareTunnelCredentialsAnnotation(
    ParameterResource ApiToken,
    ParameterResource AccountId) : IResourceAnnotation;
