using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Testing;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class DockerComposeTestConfigurationExtensionsTests
{
    [Fact]
    public void WithTestEndpoint_exports_an_encoded_endpoint_and_health_path()
    {
        var builder = CreateBuilder();
        var environment = builder.AddDockerComposeEnvironment("compose");
        var api = builder.AddContainer("catalog", "nginx")
            .WithHttpEndpoint(targetPort: 80, port: 5101, name: "http")
            .WithExternalHttpEndpoints();

        var result = environment.WithTestEndpoint(
            "catalog-api",
            api.GetEndpoint("http"),
            healthCheckPath: "/health/ready");
        var values = CaptureEnvironmentVariables(environment.Resource);

        Assert.Same(environment, result);
        var endpointName = DockerComposeDeploymentTestingBuilder.GetEndpointVariableName("catalog-api", "http");
        var healthName = DockerComposeDeploymentTestingBuilder
            .GetEndpointHealthPathVariableName("catalog-api", "http");
        Assert.Equal("http://localhost:5101/", values[endpointName].DefaultValue);
        Assert.Equal("/health/ready", values[healthName].DefaultValue);
        Assert.Same(api.Resource, values[endpointName].Resource);
        Assert.Same(api.Resource, values[healthName].Resource);
    }

    [Fact]
    public void WithTestEndpoint_supports_https_and_a_custom_host()
    {
        var builder = CreateBuilder();
        var environment = builder.AddDockerComposeEnvironment("compose");
        var api = builder.AddContainer("orders", "nginx")
            .WithHttpsEndpoint(targetPort: 443, port: 5443, name: "https")
            .WithExternalHttpEndpoints();

        environment.WithTestEndpoint("orders-api", api.GetEndpoint("https"), host: "test-host");
        var values = CaptureEnvironmentVariables(environment.Resource);

        var endpointName = DockerComposeDeploymentTestingBuilder.GetEndpointVariableName("orders-api", "https");
        Assert.Equal("https://test-host:5443/", values[endpointName].DefaultValue);
        Assert.DoesNotContain(
            DockerComposeDeploymentTestingBuilder.GetEndpointHealthPathVariableName("orders-api", "https"),
            values.Keys);
    }

    [Fact]
    public void WithTestEndpoint_preserves_multiple_named_endpoints_for_one_resource()
    {
        var builder = CreateBuilder();
        var environment = builder.AddDockerComposeEnvironment("compose");
        var api = builder.AddContainer("catalog", "nginx")
            .WithHttpEndpoint(targetPort: 80, port: 5101, name: "public")
            .WithHttpEndpoint(targetPort: 81, port: 5102, name: "admin")
            .WithExternalHttpEndpoints();

        environment
            .WithTestEndpoint("catalog-api", api.GetEndpoint("public"))
            .WithTestEndpoint("catalog-api", api.GetEndpoint("admin"));
        var values = CaptureEnvironmentVariables(environment.Resource);

        Assert.Equal(
            "http://localhost:5101/",
            values[DockerComposeDeploymentTestingBuilder.GetEndpointVariableName("catalog-api", "public")].DefaultValue);
        Assert.Equal(
            "http://localhost:5102/",
            values[DockerComposeDeploymentTestingBuilder.GetEndpointVariableName("catalog-api", "admin")].DefaultValue);
    }

    [Fact]
    public void WithTestValue_exports_an_encoded_configuration_key_and_source()
    {
        var builder = CreateBuilder();
        var environment = builder.AddDockerComposeEnvironment("compose");
        var parameter = builder.AddParameter("api-key", secret: true);
        const string configurationKey = "Parameters:api key/ü";

        var result = environment.WithTestValue(configurationKey, parameter.Resource);
        var values = CaptureEnvironmentVariables(environment.Resource);

        Assert.Same(environment, result);
        var variableName = DockerComposeDeploymentTestingBuilder.GetValueVariableName(configurationKey);
        var captured = Assert.Single(values);
        Assert.Equal(variableName, captured.Key);
        Assert.Equal(variableName, captured.Value.Name);
        Assert.Same(parameter.Resource, captured.Value.Source);
        Assert.Same(parameter.Resource, captured.Value.Resource);
    }

    [Fact]
    public void WithTestConnectionString_exports_the_standard_configuration_key()
    {
        var builder = CreateBuilder();
        var environment = builder.AddDockerComposeEnvironment("compose");
        var connectionString = builder.AddConnectionString("catalog-database");

        var result = environment.WithTestConnectionString("catalog", connectionString);
        var values = CaptureEnvironmentVariables(environment.Resource);

        Assert.Same(environment, result);
        var variableName = DockerComposeDeploymentTestingBuilder
            .GetValueVariableName("ConnectionStrings:catalog");
        var captured = Assert.Single(values);
        Assert.Equal(variableName, captured.Key);
        Assert.Equal(
            connectionString.Resource.ConnectionStringExpression.ValueExpression,
            Assert.IsType<ReferenceExpression>(captured.Value.Source).ValueExpression);
        Assert.Same(connectionString.Resource, captured.Value.Resource);
    }

    [Fact]
    public void WithTestEndpoint_rejects_an_internal_endpoint()
    {
        var builder = CreateBuilder();
        var environment = builder.AddDockerComposeEnvironment("compose");
        var api = builder.AddContainer("catalog", "nginx")
            .WithHttpEndpoint(targetPort: 80, port: 5101, name: "http");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            environment.WithTestEndpoint("catalog", api.GetEndpoint("http")));

        Assert.Contains("must be external", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTestEndpoint_allocates_a_host_port_when_the_endpoint_omits_one()
    {
        var builder = CreateBuilder();
        var environment = builder.AddDockerComposeEnvironment("compose");
        var api = builder.AddContainer("catalog", "nginx")
            .WithHttpEndpoint(targetPort: 80, name: "http")
            .WithExternalHttpEndpoints();

        environment.WithTestEndpoint("catalog", api.GetEndpoint("http"));
        var values = CaptureEnvironmentVariables(environment.Resource);
        var variable = values[
            DockerComposeDeploymentTestingBuilder.GetEndpointVariableName("catalog", "http")];
        var exportedEndpoint = new Uri(Assert.IsType<string>(variable.DefaultValue));

        Assert.InRange(exportedEndpoint.Port, 1, 65535);
        Assert.Equal(exportedEndpoint.Port, api.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(annotation => annotation.Name == "http")
            .Port);
    }

    [Theory]
    [InlineData("https://example.test/health")]
    [InlineData("health")]
    [InlineData("//evil.example/health")]
    [InlineData("/\\evil.example/health")]
    public void WithTestEndpoint_rejects_a_health_check_that_is_not_root_relative(string healthCheckPath)
    {
        var builder = CreateBuilder();
        var environment = builder.AddDockerComposeEnvironment("compose");
        var api = builder.AddContainer("catalog", "nginx")
            .WithHttpEndpoint(targetPort: 80, port: 5101, name: "http")
            .WithExternalHttpEndpoints();

        var exception = Assert.Throws<ArgumentException>(() =>
            environment.WithTestEndpoint(
                "catalog",
                api.GetEndpoint("http"),
                healthCheckPath: healthCheckPath));

        Assert.Equal("healthCheckPath", exception.ParamName);
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true,
            ProjectDirectory = AppContext.BaseDirectory
        });

    private static Dictionary<string, CapturedEnvironmentVariable> CaptureEnvironmentVariables(
        DockerComposeEnvironmentResource environment)
    {
        var configureProperty = typeof(DockerComposeEnvironmentResource).GetProperty(
            "ConfigureEnvFile",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var configure = Assert.IsType<Action<IDictionary<string, CapturedEnvironmentVariable>>>(
            configureProperty?.GetValue(environment));
        var values = new Dictionary<string, CapturedEnvironmentVariable>(StringComparer.Ordinal);
        configure(values);
        return values;
    }
}
