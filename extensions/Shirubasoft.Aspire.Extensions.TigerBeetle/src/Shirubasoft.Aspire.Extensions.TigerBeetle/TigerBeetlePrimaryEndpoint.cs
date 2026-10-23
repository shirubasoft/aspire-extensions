using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.TigerBeetle;

// Captures the allocated primary endpoint before the container starts so the TCP health check can probe it.
internal sealed class TigerBeetlePrimaryEndpoint(TigerBeetleResource resource)
{
    public (string Host, int Port)? Value { get; private set; }

    public Task CaptureAsync()
    {
        Value = (resource.PrimaryEndpoint.Host, resource.PrimaryEndpoint.Port);
        return Task.CompletedTask;
    }
}
