namespace Aspire.Hosting.Multirepo.Tests;

internal sealed class InMemoryModuleRepositoryStateStore : IModuleRepositoryStateStore
{
    private readonly Dictionary<string, ModuleRepositoryInitializationState> _states =
        new(StringComparer.Ordinal);

    public string? StateFilePath => null;

    public Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states.TryGetValue(requirement.StepKey, out var state);
        return Task.FromResult(state);
    }

    public Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states[requirement.StepKey] = state;
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingModuleRepositoryStateStore(string stateFilePath)
    : IModuleRepositoryStateStore
{
    public string? StateFilePath { get; } = stateFilePath;

    public Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Repository state must not be read.");

    public Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Repository state must not be written.");
}

internal sealed class DiscardingModuleRepositoryStateStore(string stateFilePath)
    : IModuleRepositoryStateStore
{
    public string? StateFilePath { get; } = stateFilePath;

    public Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ModuleRepositoryInitializationState?>(null);
    }

    public Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
