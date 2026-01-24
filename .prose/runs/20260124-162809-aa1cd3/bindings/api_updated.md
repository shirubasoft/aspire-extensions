# API Contract 7 Updated

## Change Summary

Updated the eventing approach in PLAN.md from the obsolete `IDistributedApplicationLifecycleHook` interface to the modern `Subscribe<BeforeStartEvent>` pattern.

## Changes Made

### Before (Obsolete Pattern)
```csharp
public class SharedResourceBuildSubscriber : IDistributedApplicationLifecycleHook
{
    Task BeforeStartAsync(
        DistributedApplicationModel model,
        CancellationToken cancellationToken);
}
```

### After (Modern Pattern)
```csharp
public class SharedResourceBuildService
{
    public SharedResourceBuildService(
        IRepositoryPathResolver pathResolver,
        IGitOperations gitOperations,
        IContainerImageService containerService,
        ILogger<SharedResourceBuildService> logger);

    public Task OnBeforeStartAsync(
        BeforeStartEvent @event,
        CancellationToken cancellationToken);
}

// Registration via extension method using Eventing.Subscribe<BeforeStartEvent>
public static IDistributedApplicationBuilder AddSharedResourceSupport(
    this IDistributedApplicationBuilder builder)
{
    builder.Services.AddSingleton<SharedResourceBuildService>();
    builder.Eventing.Subscribe<BeforeStartEvent>(
        async (@event, ct) =>
        {
            var service = @event.Services.GetRequiredService<SharedResourceBuildService>();
            await service.OnBeforeStartAsync(@event, ct);
        });
    return builder;
}
```

## Key Differences

1. **Class renamed**: `SharedResourceBuildSubscriber` -> `SharedResourceBuildService` (reflects service-based architecture)
2. **No interface implementation**: Modern pattern uses event subscription instead of implementing a lifecycle hook interface
3. **Explicit constructor dependencies**: Dependencies are now clearly defined in constructor
4. **Event-based registration**: Uses `builder.Eventing.Subscribe<BeforeStartEvent>()` instead of DI-based hook discovery
5. **Service resolution at event time**: The service is resolved from `@event.Services` when the event fires

## File Modified

- `/home/danielreis/code/aspire-extensions/PLAN.md` - Section "7. Eventing Subscriber" renamed to "7. Shared Resource Build Service (Event Subscriber)"
