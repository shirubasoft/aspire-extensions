#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Multirepo.Tests;

internal sealed class RecordingReportingStep : IReportingStep
{
    public List<RecordingReportingTask> Tasks { get; } = [];

    public string? CompletionText { get; private set; }

    public CompletionState? RecordedCompletionState { get; private set; }

    public Task<IReportingTask> CreateTaskAsync(
        string statusText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = new RecordingReportingTask(statusText);
        Tasks.Add(task);
        return Task.FromResult<IReportingTask>(task);
    }

    public Task<IReportingTask> CreateTaskAsync(
        MarkdownString statusText,
        CancellationToken cancellationToken = default) =>
        CreateTaskAsync(statusText.Value, cancellationToken);

    [Obsolete("Use Log(LogLevel, string) or Log(LogLevel, MarkdownString) instead.")]
    public void Log(LogLevel logLevel, string message, bool enableMarkdown)
    {
    }

    public void Log(LogLevel logLevel, string message)
    {
    }

    public void Log(LogLevel logLevel, MarkdownString message)
    {
    }

    public Task CompleteAsync(
        string completionText,
        CompletionState completionState = CompletionState.Completed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompletionText = completionText;
        RecordedCompletionState = completionState;
        return Task.CompletedTask;
    }

    public Task CompleteAsync(
        MarkdownString completionText,
        CompletionState completionState = CompletionState.Completed,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(completionText.Value, completionState, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingReportingTask(string initialStatusText) : IReportingTask
{
    public string InitialStatusText { get; } = initialStatusText;

    public List<string> Updates { get; } = [];

    public string? CompletionMessage { get; private set; }

    public CompletionState? RecordedCompletionState { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task UpdateAsync(
        string statusText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Updates.Add(statusText);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(
        MarkdownString statusText,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(statusText.Value, cancellationToken);

    public Task CompleteAsync(
        string? completionMessage = null,
        CompletionState completionState = CompletionState.Completed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CompletionMessage = completionMessage;
        RecordedCompletionState = completionState;
        return Task.CompletedTask;
    }

    public Task CompleteAsync(
        MarkdownString completionMessage,
        CompletionState completionState = CompletionState.Completed,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(completionMessage.Value, completionState, cancellationToken);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
