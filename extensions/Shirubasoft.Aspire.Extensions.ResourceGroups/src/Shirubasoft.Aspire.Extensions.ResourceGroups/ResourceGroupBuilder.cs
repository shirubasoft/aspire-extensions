using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

internal sealed class ResourceGroupBuilder : IResourceGroupBuilder
{
    private readonly IDistributedApplicationBuilder _applicationBuilder;
    private readonly IResourceBuilder<ResourceGroupResource> _resourceBuilder;
    private readonly List<GroupParentRegistration> _groupParents = [];

    public ResourceGroupBuilder(IResourceBuilder<ResourceGroupResource> resourceBuilder)
    {
        _resourceBuilder = resourceBuilder;
        _applicationBuilder = resourceBuilder.ApplicationBuilder;
        _applicationBuilder.Eventing.Subscribe<BeforeStartEvent>(ApplyParentPrecedenceAsync);
    }

    ResourceGroupResource IResourceBuilder<ResourceGroupResource>.Resource => _resourceBuilder.Resource;

    IDistributedApplicationBuilder IResourceBuilder<ResourceGroupResource>.ApplicationBuilder => _applicationBuilder;

    ConfigurationManager IDistributedApplicationBuilder.Configuration => _applicationBuilder.Configuration;

    string IDistributedApplicationBuilder.AppHostDirectory => _applicationBuilder.AppHostDirectory;

    Assembly? IDistributedApplicationBuilder.AppHostAssembly => _applicationBuilder.AppHostAssembly;

    IHostEnvironment IDistributedApplicationBuilder.Environment => _applicationBuilder.Environment;

    IServiceCollection IDistributedApplicationBuilder.Services => _applicationBuilder.Services;

    IDistributedApplicationEventing IDistributedApplicationBuilder.Eventing => _applicationBuilder.Eventing;

    DistributedApplicationExecutionContext IDistributedApplicationBuilder.ExecutionContext =>
        _applicationBuilder.ExecutionContext;

    IResourceCollection IDistributedApplicationBuilder.Resources => _applicationBuilder.Resources;

#pragma warning disable ASPIREPIPELINES001
    IDistributedApplicationPipeline IDistributedApplicationBuilder.Pipeline => _applicationBuilder.Pipeline;
#pragma warning restore ASPIREPIPELINES001

#pragma warning disable ASPIREFILESYSTEM001
    IFileSystemService IDistributedApplicationBuilder.FileSystemService => _applicationBuilder.FileSystemService;
#pragma warning restore ASPIREFILESYSTEM001

#pragma warning disable ASPIREUSERSECRETS001
    IUserSecretsManager IDistributedApplicationBuilder.UserSecretsManager => _applicationBuilder.UserSecretsManager;
#pragma warning restore ASPIREUSERSECRETS001

    IResourceBuilder<T> IDistributedApplicationBuilder.AddResource<T>(T resource)
    {
        var resourceBuilder = _applicationBuilder.AddResource(resource);
        if (HasParent(resource))
        {
            return resourceBuilder;
        }

        var relationship = new ResourceRelationshipAnnotation(_resourceBuilder.Resource, "Parent");
        resourceBuilder.WithAnnotation(relationship);
        _groupParents.Add(new(resource, relationship));
        return new GroupedResourceBuilder<T>(resourceBuilder, relationship);
    }

    IResourceBuilder<T> IDistributedApplicationBuilder.CreateResourceBuilder<T>(T resource) =>
        _applicationBuilder.CreateResourceBuilder(resource);

    DistributedApplication IDistributedApplicationBuilder.Build() => _applicationBuilder.Build();

    IResourceBuilder<ResourceGroupResource> IResourceBuilder<ResourceGroupResource>.WithAnnotation<TAnnotation>(
        TAnnotation annotation,
        ResourceAnnotationMutationBehavior behavior)
    {
        _resourceBuilder.WithAnnotation(annotation, behavior);
        return this;
    }

    private Task ApplyParentPrecedenceAsync(BeforeStartEvent _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyParentPrecedence();
        return Task.CompletedTask;
    }

    internal void ApplyParentPrecedence()
    {
        foreach (var registration in _groupParents)
        {
            if (HasAnotherParent(registration))
            {
                registration.Resource.Annotations.Remove(registration.Relationship);
            }
        }
    }

    private static bool HasParent(IResource resource) =>
        resource is IResourceWithParent
        || resource.Annotations.OfType<ResourceRelationshipAnnotation>().Any(IsParentRelationship);

    private static bool HasAnotherParent(GroupParentRegistration registration) =>
        registration.Resource is IResourceWithParent
        || registration.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Any(relationship =>
                !ReferenceEquals(relationship, registration.Relationship)
                && IsParentRelationship(relationship));

    private static bool IsParentRelationship(ResourceRelationshipAnnotation relationship) =>
        string.Equals(relationship.Type, "Parent", StringComparison.Ordinal);

    private sealed record GroupParentRegistration(
        IResource Resource,
        ResourceRelationshipAnnotation Relationship);

    private sealed class GroupedResourceBuilder<T>(
        IResourceBuilder<T> resourceBuilder,
        ResourceRelationshipAnnotation groupRelationship) : IResourceBuilder<T>
        where T : IResource
    {
        public IDistributedApplicationBuilder ApplicationBuilder => resourceBuilder.ApplicationBuilder;

        public T Resource => resourceBuilder.Resource;

        public IResourceBuilder<T> WithAnnotation<TAnnotation>(
            TAnnotation annotation,
            ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append)
            where TAnnotation : IResourceAnnotation
        {
            if (annotation is ResourceRelationshipAnnotation relationship
                && IsParentRelationship(relationship))
            {
                Resource.Annotations.Remove(groupRelationship);
            }

            resourceBuilder.WithAnnotation(annotation, behavior);
            return this;
        }
    }
}
