using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.Tests;

public sealed class ResourceGroupBuilderExtensionsTests
{
    [Fact]
    public void AddResourceGroupCreatesALogicalRunningResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var group = builder.AddResourceGroup("backend");

        var resource = group.Resource;
        Assert.Equal("backend", resource.Name);
        Assert.Contains(
            resource.Annotations,
            annotation => ReferenceEquals(annotation, ManifestPublishingCallbackAnnotation.Ignore));
        var snapshot = Assert.Single(resource.Annotations.OfType<ResourceSnapshotAnnotation>()).InitialSnapshot;
        Assert.Equal("Resource group", snapshot.ResourceType);
        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Empty(snapshot.Properties);
    }

    [Fact]
    public void StandardBuilderExtensionsRegisterResourcesUnderTheGroup()
    {
        var builder = DistributedApplication.CreateBuilder();
        var group = builder.AddResourceGroup("backend");

        var api = group.AddContainer("api", "alpine");

        Assert.Same(builder, api.ApplicationBuilder);
        Assert.Same(group.Resource, Assert.Single(ParentRelationships(api.Resource)).Resource);
    }

    [Fact]
    public void NestedGroupsBelongToTheirContainingGroup()
    {
        var builder = DistributedApplication.CreateBuilder();
        var backend = builder.AddResourceGroup("backend");

        var data = backend.AddResourceGroup("data");

        Assert.Same(backend.Resource, Assert.Single(ParentRelationships(data.Resource)).Resource);
    }

    [Fact]
    public void ResourceTypesWithAParentKeepTheirParent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var group = builder.AddResourceGroup("backend");
        var owner = builder.AddContainer("owner", "alpine");
        var child = new ChildResource("child", owner.Resource);

        ((IDistributedApplicationBuilder)group).AddResource(child);

        Assert.Empty(ParentRelationships(child));
        Assert.Same(owner.Resource, child.Parent);
    }

    [Fact]
    public void ExistingParentRelationshipsTakePrecedenceDuringRegistration()
    {
        var builder = DistributedApplication.CreateBuilder();
        var group = builder.AddResourceGroup("backend");
        var owner = builder.AddContainer("owner", "alpine");
        var resource = new TestResource("child");
        resource.Annotations.Add(new ResourceRelationshipAnnotation(owner.Resource, "Parent"));

        ((IDistributedApplicationBuilder)group).AddResource(resource);

        Assert.Same(owner.Resource, Assert.Single(ParentRelationships(resource)).Resource);
    }

    [Fact]
    public void ExplicitParentsAppliedByBuilderExtensionsReplaceTheGroup()
    {
        var builder = DistributedApplication.CreateBuilder();
        var group = builder.AddResourceGroup("backend");
        var owner = group.AddContainer("owner", "alpine");

        var child = group
            .AddContainer("child", "alpine")
            .WithParentRelationship(owner);

        Assert.Same(owner.Resource, Assert.Single(ParentRelationships(child.Resource)).Resource);
    }

    [Fact]
    public void DirectParentAnnotationsReplaceTheGroupBeforeStartup()
    {
        var builder = DistributedApplication.CreateBuilder();
        var group = builder.AddResourceGroup("backend");
        var owner = group.AddContainer("owner", "alpine");
        var child = group.AddContainer("child", "alpine");
        child.Resource.Annotations.Add(new ResourceRelationshipAnnotation(owner.Resource, "Parent"));

        Assert.IsType<ResourceGroupBuilder>(group).ApplyParentPrecedence();

        Assert.Same(owner.Resource, Assert.Single(ParentRelationships(child.Resource)).Resource);
    }

    [Fact]
    public void GroupBuilderForwardsApplicationServicesAndResourceConfiguration()
    {
        var builder = DistributedApplication.CreateBuilder();
        var group = builder.AddResourceGroup("backend");

        group.WithIconName("Box");

        Assert.Same(builder.Services, group.Services);
        Assert.Contains(group.Resource.Annotations, annotation => annotation is ResourceIconAnnotation);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AddResourceGroupRejectsBlankNames(string name)
    {
        var builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddResourceGroup(name));
    }

    private static IEnumerable<ResourceRelationshipAnnotation> ParentRelationships(IResource resource) =>
        resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Where(relationship => string.Equals(relationship.Type, "Parent", StringComparison.Ordinal));

    private sealed class ChildResource(string name, IResource parent) : Resource(name), IResourceWithParent
    {
        public IResource Parent { get; } = parent;
    }

    private sealed class TestResource(string name) : Resource(name);
}
