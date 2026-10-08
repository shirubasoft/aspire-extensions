using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREPIPELINES001

// A compute environment outside Aspire's integrations. Before start, it assigns each compute
// resource a deployment target. Its deployment step carries DeploymentTag and belongs to the
// environment or, like Azure Container Apps, to each deployment target.
internal sealed class TestComputeEnvironmentResource(string name)
    : Resource(name), IComputeEnvironmentResource
{
#pragma warning disable ASPIRECOMPUTE002
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference) =>
        ReferenceExpression.Create($"{endpointReference.Resource.Name}");
#pragma warning restore ASPIRECOMPUTE002
}

public enum DeploymentStepOwner
{
    Environment,
    DeploymentTarget,
}

internal static class TestComputeEnvironmentExtensions
{
    public static IResourceBuilder<TestComputeEnvironmentResource> AddTestComputeEnvironment(
        this IDistributedApplicationBuilder builder,
        string name,
        string deploymentTag,
        DeploymentStepOwner owner = DeploymentStepOwner.Environment,
        Func<Task>? deploy = null)
    {
        var environment = builder.AddResource(new TestComputeEnvironmentResource(name));

        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            foreach (var resource in @event.Model.GetComputeResources())
            {
                resource.Annotations.Add(new DeploymentTargetAnnotation(
                    new PipelineProbeResource($"{resource.Name}-{name}"))
                {
                    ComputeEnvironment = environment.Resource,
                });
            }

            return Task.CompletedTask;
        });

        return environment.WithPipelineStepFactory(context => CreateDeploymentSteps(
            context.PipelineContext.Model,
            environment.Resource,
            deploymentTag,
            owner,
            deploy ?? (() => Task.CompletedTask)));
    }

    private static IEnumerable<PipelineStep> CreateDeploymentSteps(
        DistributedApplicationModel model,
        TestComputeEnvironmentResource environment,
        string deploymentTag,
        DeploymentStepOwner owner,
        Func<Task> deploy)
    {
        IEnumerable<IResource> owners = owner == DeploymentStepOwner.Environment
            ? [environment]
            : model.GetComputeResources()
                .Select(resource => resource.GetDeploymentTargetAnnotation(environment)?.DeploymentTarget)
                .OfType<IResource>();

        return owners.Select(stepOwner => new PipelineStep
        {
            Name = $"deploy-{stepOwner.Name}",
            Action = _ => deploy(),
            DependsOnSteps = [WellKnownPipelineSteps.Publish],
            RequiredBySteps = [WellKnownPipelineSteps.Deploy],
            Tags = [deploymentTag],
            Resource = stepOwner,
        });
    }
}

#pragma warning restore ASPIREPIPELINES001
