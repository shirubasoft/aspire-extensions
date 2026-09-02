using ActionsToolkit.Core.Services;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Shirubasoft.Aspire.Extensions.Multirepo.Tool;

internal sealed class WorkflowCommandService(
    IProcessRunner processRunner,
    IConfiguration configuration,
    ICoreService githubActions,
    string workingDirectory,
    TextWriter output,
    TextWriter error)
{
    public string DefaultGitHubCliPath =>
        configuration[$"{ModularAppHostsOptions.ConfigurationSectionName}:GitHubCliPath"] ?? "gh";

    public async Task<int> DispatchAsync(
        string repository,
        string workflow,
        string? reference,
        string workflowDocumentPath,
        string workflowDocumentInput,
        IReadOnlyList<string> rawInputs,
        string githubCliPath,
        CancellationToken cancellationToken)
    {
        ValidateRequiredValue(repository, "--repository");
        ValidateRequiredValue(workflow, "--workflow");
        ValidateRequiredValue(workflowDocumentPath, "--workflow-document");
        ValidateRequiredValue(workflowDocumentInput, "--workflow-document-input");
        ValidateRequiredValue(githubCliPath, "--gh-path");

        var document = await ModuleImageWorkflowDocument.LoadAsync(
            Path.GetFullPath(workflowDocumentPath, workingDirectory),
            cancellationToken).ConfigureAwait(false);
        var payload = CreatePayload(rawInputs, workflowDocumentInput, document);
        var dispatchArguments = CreateDispatchArguments(workflow, repository, reference);
        var dispatch = await processRunner.RunAsync(
            new ProcessInvocation(
                githubCliPath,
                dispatchArguments,
                workingDirectory,
                OutputMode: ProcessOutputMode.Capture,
                StandardInput: payload),
            cancellationToken).ConfigureAwait(false);
        if (!dispatch.IsSuccess)
        {
            await WriteProcessFailureAsync("GitHub workflow dispatch", dispatch).ConfigureAwait(false);
            return ToolExitCode.Failure;
        }

        var run = TryGetRun(dispatch.StandardOutput);
        if (run is null)
        {
            await error.WriteLineAsync(
                "GitHub CLI did not return the created workflow run URL. Version 2.87.0 or newer is required.")
                .ConfigureAwait(false);
            return ToolExitCode.Failure;
        }

        await ReportRunAsync(run).ConfigureAwait(false);

        var watch = await processRunner.RunAsync(
            new ProcessInvocation(
                githubCliPath,
                ["run", "watch", run.Id, "--repo", repository, "--compact", "--exit-status"],
                workingDirectory,
                OutputMode: ProcessOutputMode.Stream),
            cancellationToken).ConfigureAwait(false);
        return watch.ExitCode;
    }

    private static string CreatePayload(
        IReadOnlyList<string> rawInputs,
        string workflowDocumentInput,
        ModuleImageWorkflowDocument document)
    {
        var inputs = ParseInputs(rawInputs);
        AddWorkflowDocument(inputs, workflowDocumentInput, document);
        ValidateInputCount(inputs.Count);
        var payload = JsonSerializer.Serialize(inputs);
        ValidatePayloadLength(payload);
        return payload;
    }

    private static void AddWorkflowDocument(
        Dictionary<string, string> inputs,
        string workflowDocumentInput,
        ModuleImageWorkflowDocument document)
    {
        if (!inputs.TryAdd(workflowDocumentInput, document.ToJson()))
        {
            throw new ToolUsageException(
                $"Workflow input '{workflowDocumentInput}' is reserved for the module image workflow document.");
        }
    }

    private static void ValidateInputCount(int inputCount)
    {
        if (inputCount > 10)
        {
            throw new ToolUsageException("GitHub workflow dispatch supports at most 10 inputs.");
        }
    }

    private static void ValidatePayloadLength(string payload)
    {
        if (payload.Length > ModuleImageWorkflowDocument.MaximumJsonLength)
        {
            throw new ToolUsageException(
                $"The complete workflow input payload exceeds {ModuleImageWorkflowDocument.MaximumJsonLength} characters.");
        }
    }

    private static List<string> CreateDispatchArguments(
        string workflow,
        string repository,
        string? reference)
    {
        var arguments = new List<string>
        {
            "workflow",
            "run",
            workflow,
            "--repo",
            repository,
            "--json"
        };
        if (!string.IsNullOrWhiteSpace(reference))
        {
            arguments.Add("--ref");
            arguments.Add(reference);
        }

        return arguments;
    }

    private async Task ReportRunAsync(WorkflowRun run)
    {
        await output.WriteLineAsync($"Dispatched {run.Url}").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(configuration["GITHUB_OUTPUT"]))
        {
            return;
        }

        await githubActions.SetOutputAsync("run-id", run.Id).ConfigureAwait(false);
        await githubActions.SetOutputAsync("run-url", run.Url).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseInputs(IReadOnlyList<string> rawInputs)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawInput in rawInputs)
        {
            var input = ParseInput(rawInput);
            if (!inputs.TryAdd(input.Key, input.Value))
            {
                throw new ToolUsageException($"Workflow input '{input.Key}' is specified more than once.");
            }
        }

        return inputs;
    }

    private static KeyValuePair<string, string> ParseInput(string rawInput)
    {
        var separator = rawInput.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw new ToolUsageException(
                $"Workflow input '{rawInput}' must use the form <name>=<value>.");
        }

        var name = rawInput[..separator];
        ValidateInputName(name);
        return KeyValuePair.Create(name, rawInput[(separator + 1)..]);
    }

    private static void ValidateInputName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw CreateInvalidInputNameException(name);
        }

        ValidateInputNameLineBreaks(name);
    }

    private static void ValidateInputNameLineBreaks(string name)
    {
        ValidateInputNameCarriageReturns(name);
        ValidateInputNameNewlines(name);
    }

    private static void ValidateInputNameCarriageReturns(string name)
    {
        if (name.Contains('\r', StringComparison.Ordinal))
        {
            throw CreateInvalidInputNameException(name);
        }
    }

    private static void ValidateInputNameNewlines(string name)
    {
        if (name.Contains('\n', StringComparison.Ordinal))
        {
            throw CreateInvalidInputNameException(name);
        }
    }

    private static ToolUsageException CreateInvalidInputNameException(string name) =>
        new($"Workflow input name '{name}' is invalid.");

    private static WorkflowRun? TryGetRun(string standardOutput)
    {
        var candidate = standardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        var uri = TryParseRunUri(candidate);
        if (uri is null)
        {
            return null;
        }

        var id = TryParseRunId(uri);
        return id is null ? null : new WorkflowRun(id, uri.AbsoluteUri);
    }

    private static Uri? TryParseRunUri(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri : null;

    private static string? TryParseRunId(Uri uri)
    {
        var id = uri.Segments.LastOrDefault()?.Trim('/');
        return long.TryParse(id, out _) ? id : null;
    }

    private async Task WriteProcessFailureAsync(
        string operation,
        ProcessExecutionResult result)
    {
        await error.WriteLineAsync($"{operation} failed with exit code {result.ExitCode}.")
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            await error.WriteLineAsync(result.StandardError.Trim()).ConfigureAwait(false);
        }
    }

    private static void ValidateRequiredValue(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolUsageException($"{option} cannot be empty.");
        }
    }

    private sealed record WorkflowRun(string Id, string Url);
}
