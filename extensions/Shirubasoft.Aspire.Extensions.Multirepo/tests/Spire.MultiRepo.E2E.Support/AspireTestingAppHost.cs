using System.Reflection;
using System.Runtime.Loader;
using Aspire.Hosting.Testing;

namespace Spire.MultiRepo.E2E.Support;

internal sealed record AppHostResourceSnapshot(string Health, string Marker);

internal static class AspireTestingAppHost
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim EnvironmentGate = new(1, 1);

    public static async Task<AppHostResourceSnapshot> ReadResourceAsync(
        string appHostProject,
        string resourceName,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostProject);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(environment);

        await EnvironmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var environmentScope = ProcessEnvironmentScope.Apply(environment);
            var assemblyPath = GetAppHostAssemblyPath(appHostProject);
            var resolver = new AssemblyDependencyResolver(assemblyPath);
            Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
            {
                var dependencyPath = resolver.ResolveAssemblyToPath(name);
                return dependencyPath is null
                    ? null
                    : context.LoadFromAssemblyPath(dependencyPath);
            }

            AssemblyLoadContext.Default.Resolving += ResolveAssembly;
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                var entryPoint = assembly.EntryPoint?.DeclaringType ?? throw new InvalidOperationException(
                    $"The isolated AppHost assembly '{assemblyPath}' has no entry-point type.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(StartupTimeout);
                await using var builder = await DistributedApplicationTestingBuilder.CreateAsync(
                    entryPoint,
                    [],
                    timeout.Token).ConfigureAwait(false);
                await using var application = await builder.BuildAsync(timeout.Token)
                    .WaitAsync(StartupTimeout, timeout.Token).ConfigureAwait(false);

                await application.StartAsync(timeout.Token)
                    .WaitAsync(StartupTimeout, timeout.Token).ConfigureAwait(false);
                await application.ResourceNotifications.WaitForResourceHealthyAsync(
                    resourceName,
                    timeout.Token).WaitAsync(StartupTimeout, timeout.Token).ConfigureAwait(false);
                using var client = application.CreateHttpClient(resourceName, "http");
                var health = await client.GetStringAsync("/health.txt", timeout.Token).ConfigureAwait(false);
                var marker = await client.GetStringAsync("/marker.txt", timeout.Token).ConfigureAwait(false);
                return new AppHostResourceSnapshot(health.Trim(), marker.Trim());
            }
            finally
            {
                AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
            }
        }
        finally
        {
            EnvironmentGate.Release();
        }
    }

    private static string GetAppHostAssemblyPath(string appHostProject)
    {
        var projectPath = Path.GetFullPath(appHostProject);
        var projectDirectory = Path.GetDirectoryName(projectPath) ?? throw new InvalidOperationException(
            $"Unable to determine the directory containing AppHost project '{projectPath}'.");
        var assemblyPath = Path.Combine(
            projectDirectory,
            "bin",
            "Release",
            "net10.0",
            $"{Path.GetFileNameWithoutExtension(projectPath)}.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new InvalidOperationException(
                $"The isolated AppHost assembly '{assemblyPath}' does not exist. Build the fixture before starting it.");
        }

        return assemblyPath;
    }

    private sealed class ProcessEnvironmentScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _originalValues;

        private ProcessEnvironmentScope(IReadOnlyDictionary<string, string?> originalValues)
        {
            _originalValues = originalValues;
        }

        public static ProcessEnvironmentScope Apply(IReadOnlyDictionary<string, string?> values)
        {
            var originalValues = values.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            return new ProcessEnvironmentScope(originalValues);
        }

        public void Dispose()
        {
            foreach (var (key, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
