using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Testing;

/// <summary>Creates distributed test builders with an authenticated dashboard for diagnostics.</summary>
public static class DiagnosticsTestingBuilder
{
    /// <summary>Creates a test builder and enables its dashboard before Aspire registers services.</summary>
    /// <typeparam name="TEntryPoint">The AppHost entry point or generated project type.</typeparam>
    /// <param name="args">Arguments passed to the AppHost.</param>
    /// <param name="configureBuilder">Optional application and host configuration.</param>
    /// <param name="cancellationToken">Cancels builder creation.</param>
    /// <returns>A caller-owned builder with the dashboard enabled.</returns>
    public static Task<IDistributedApplicationTestingBuilder> CreateAsync<TEntryPoint>(
        string[]? args = null,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? configureBuilder = null,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class =>
        DistributedApplicationTestingBuilder.CreateAsync<TEntryPoint>(args ?? [], (options, settings) =>
        {
            configureBuilder?.Invoke(options, settings);
            options.DisableDashboard = false;
        }, cancellationToken);
}
