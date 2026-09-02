using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;

namespace Aspire.Hosting.Testing;

/// <summary>Exports Docker Compose deployment values for use by external tests.</summary>
public static class DockerComposeTestConfigurationExtensions
{
    /// <summary>
    /// Exports an externally reachable endpoint to the environment-specific Docker Compose environment file.
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithTestEndpoint(
        this IResourceBuilder<DockerComposeEnvironmentResource> environment,
        string resourceName,
        EndpointReference endpoint,
        string host = "localhost",
        string? healthCheckPath = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ValidateHealthCheckPath(healthCheckPath);
        var annotation = endpoint.EndpointAnnotation;
        ValidateExternalEndpoint(endpoint, annotation);
        var port = ResolveHostPort(endpoint, annotation);
        var endpointName = endpoint.EndpointName;
        var variableName = DockerComposeDeploymentTestingBuilder.GetEndpointVariableName(resourceName, endpointName);
        var endpointValue = new UriBuilder(annotation.UriScheme, host, port).Uri.AbsoluteUri;
        var export = new TestEndpointExport(
            resourceName,
            endpointName,
            variableName,
            endpointValue,
            healthCheckPath,
            endpoint.Resource);
        return environment.ConfigureEnvFile(values => AddEndpointVariables(values, export));
    }

    private static void ValidateHealthCheckPath(string? healthCheckPath)
    {
        if (healthCheckPath is null)
        {
            return;
        }

        if (!TestEndpointHealthPath.IsRootRelative(healthCheckPath))
        {
            throw new ArgumentException(
                $"The health check path '{healthCheckPath}' must be a root-relative URI path.",
                nameof(healthCheckPath));
        }
    }

    private static void ValidateExternalEndpoint(EndpointReference endpoint, EndpointAnnotation annotation)
    {
        if (!annotation.IsExternal)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Resource.Name}/{endpoint.EndpointName}' must be external before it can be exported to tests.");
        }
    }

    private static int ResolveHostPort(EndpointReference endpoint, EndpointAnnotation annotation)
    {
        if (annotation.Port is not int port)
        {
            port = AvailableHostPortAllocator.Allocate();
            annotation.Port = port;
            return port;
        }

        ValidateHostPort(endpoint, port);
        return port;
    }

    private static void ValidateHostPort(EndpointReference endpoint, int port)
    {
        if ((uint)(port - 1) >= 65535u)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Resource.Name}/{endpoint.EndpointName}' has invalid host port '{port}'.");
        }
    }

    private static void AddEndpointVariables(
        IDictionary<string, CapturedEnvironmentVariable> values,
        TestEndpointExport export)
    {
        values[export.VariableName] = new CapturedEnvironmentVariable
        {
            Name = export.VariableName,
            Description = $"External test endpoint {export.ResourceName}/{export.EndpointName}",
            DefaultValue = export.EndpointValue,
            Resource = export.Resource
        };
        AddHealthCheckVariable(values, export);
    }

    private static void AddHealthCheckVariable(
        IDictionary<string, CapturedEnvironmentVariable> values,
        TestEndpointExport export)
    {
        if (export.HealthCheckPath is null)
        {
            return;
        }

        var variableName = DockerComposeDeploymentTestingBuilder
            .GetEndpointHealthPathVariableName(export.ResourceName, export.EndpointName);
        values[variableName] = new CapturedEnvironmentVariable
        {
            Name = variableName,
            Description = $"External test endpoint health check {export.ResourceName}/{export.EndpointName}",
            DefaultValue = export.HealthCheckPath,
            Resource = export.Resource
        };
    }

    private sealed record TestEndpointExport(
        string ResourceName,
        string EndpointName,
        string VariableName,
        string EndpointValue,
        string? HealthCheckPath,
        IResource Resource);

    /// <summary>
    /// Exports a parameter or other Aspire value provider to the environment-specific Docker Compose environment file.
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithTestValue(
        this IResourceBuilder<DockerComposeEnvironmentResource> environment,
        string configurationKey,
        IValueProvider value)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        ArgumentNullException.ThrowIfNull(value);

        var variableName = DockerComposeDeploymentTestingBuilder.GetValueVariableName(configurationKey);
        return environment.ConfigureEnvFile(values =>
        {
            values[variableName] = new CapturedEnvironmentVariable
            {
                Name = variableName,
                Description = $"External test configuration value {configurationKey}",
                Source = value,
                Resource = value as IResource
            };
        });
    }

    /// <summary>
    /// Exports a resource connection string to the environment-specific Docker Compose environment file.
    /// </summary>
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithTestConnectionString<T>(
        this IResourceBuilder<DockerComposeEnvironmentResource> environment,
        string connectionName,
        IResourceBuilder<T> resource)
        where T : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(resource);

        var configurationKey = $"ConnectionStrings:{connectionName}";
        var variableName = DockerComposeDeploymentTestingBuilder.GetValueVariableName(configurationKey);
        return environment.ConfigureEnvFile(values =>
        {
            values[variableName] = new CapturedEnvironmentVariable
            {
                Name = variableName,
                Description = $"External test connection string {connectionName}",
                Source = resource.Resource.ConnectionStringExpression,
                Resource = resource.Resource
            };
        });
    }
}
