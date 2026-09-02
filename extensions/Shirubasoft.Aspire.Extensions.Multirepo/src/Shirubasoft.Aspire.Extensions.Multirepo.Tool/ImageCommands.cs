using ActionsToolkit.Core.Services;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Shirubasoft.Aspire.Extensions.Multirepo.Tool;

internal sealed class ImageCommandService(
    IProcessRunner processRunner,
    IConfiguration configuration,
    ICoreService githubActions,
    string workingDirectory,
    TextWriter output,
    TextWriter error)
{
    public async Task<int> ApplyAsync(
        string? file,
        string? json,
        string? tag,
        string resourceTags,
        string[] command,
        bool hasCommandSeparator,
        CancellationToken cancellationToken)
    {
        ValidateWorkflowSource(file, json);
        ValidateApplyCommand(command, hasCommandSeparator);
        var document = await LoadWorkflowDocumentAsync(file, json, cancellationToken).ConfigureAwait(false);
        new ImageTagOverrides(tag, resourceTags).Apply(document);
        var environment = CreateWorkflowEnvironment(document);
        var result = await processRunner.RunAsync(
            new ProcessInvocation(
                command[0],
                command[1..],
                workingDirectory,
                environment,
                ProcessOutputMode.Stream),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    private static void ValidateWorkflowSource(string? file, string? json)
    {
        if ((file is null) == (json is null))
        {
            throw new ToolUsageException("Specify exactly one of --file or --json.");
        }
    }

    private static void ValidateApplyCommand(string[] command, bool hasCommandSeparator)
    {
        ValidateCommandSeparator(hasCommandSeparator);
        ValidateCommandLength(command);
        ValidateCommandExecutable(command);
    }

    private static void ValidateCommandSeparator(bool hasCommandSeparator)
    {
        if (!hasCommandSeparator)
        {
            throw new ToolUsageException("Specify a command to run after '--'.");
        }
    }

    private static void ValidateCommandLength(string[] command)
    {
        if (command.Length == 0)
        {
            throw new ToolUsageException("Specify a command to run after '--'.");
        }
    }

    private static void ValidateCommandExecutable(string[] command)
    {
        if (string.IsNullOrWhiteSpace(command[0]))
        {
            throw new ToolUsageException("Specify a command to run after '--'.");
        }
    }

    private Task<ModuleImageWorkflowDocument> LoadWorkflowDocumentAsync(
        string? file,
        string? json,
        CancellationToken cancellationToken) =>
        file is null
            ? Task.FromResult(ModuleImageWorkflowDocument.Parse(json!))
            : ModuleImageWorkflowDocument.LoadAsync(
                Path.GetFullPath(file, workingDirectory),
                cancellationToken);

    private static Dictionary<string, string?> CreateWorkflowEnvironment(
        ModuleImageWorkflowDocument document) =>
        ModuleImageWorkflowConfiguration.Create(document)
            .ToDictionary(
                pair => pair.Key.Replace(":", "__", StringComparison.Ordinal),
                pair => (string?)pair.Value,
                StringComparer.Ordinal);

    public async Task<int> PublishAsync(
        string appHost,
        string[] modules,
        string[] resources,
        bool all,
        string? tag,
        string resourceTags,
        string? outputPath,
        string aspirePath,
        CancellationToken cancellationToken)
    {
        ValidatePublishArguments(appHost, modules, resources, all, aspirePath);
        var appHostPath = Path.GetFullPath(appHost, workingDirectory);
        ValidateAppHostPath(appHostPath);
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"modular-apphosts-{Guid.NewGuid():N}");
        try
        {
            return await PublishCoreAsync(
                appHostPath,
                modules,
                resources,
                tag,
                resourceTags,
                outputPath,
                aspirePath,
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTemporaryPath(temporaryPath);
        }
    }

    private static void ValidateAppHostPath(string appHostPath)
    {
        if (File.Exists(appHostPath))
        {
            return;
        }

        ValidateAppHostDirectory(appHostPath);
    }

    private static void ValidateAppHostDirectory(string appHostPath)
    {
        if (!Directory.Exists(appHostPath))
        {
            throw new ToolUsageException($"AppHost path '{appHostPath}' does not exist.");
        }
    }

    private async Task<int> PublishCoreAsync(
        string appHostPath,
        string[] modules,
        string[] resources,
        string? tag,
        string resourceTags,
        string? outputPath,
        string aspirePath,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        var workflowPath = Path.Combine(temporaryPath, "workflow");
        var producerEnvironment = CreateProducerEnvironment(modules, resources, tag, resourceTags);
        var publish = await RunAspireAsync(
            aspirePath,
            appHostPath,
            "workflow-images",
            workflowPath,
            producerEnvironment,
            cancellationToken).ConfigureAwait(false);
        if (!publish.IsSuccess)
        {
            return await WriteAspireFailureAsync(publish, "Aspire workflow image publish")
                .ConfigureAwait(false);
        }

        await SaveWorkflowDocumentAsync(workflowPath, outputPath, cancellationToken).ConfigureAwait(false);
        return ToolExitCode.Success;
    }

    private static Dictionary<string, string?> CreateProducerEnvironment(
        string[] modules,
        string[] resources,
        string? tag,
        string resourceTags)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        AddSelection(
            environment,
            ModuleImageWorkflowConfiguration.ModuleSelectionConfigurationSectionName,
            modules);
        AddSelection(
            environment,
            ModuleImageWorkflowConfiguration.ResourceSelectionConfigurationSectionName,
            resources);
        AddTagOverrides(environment, new ImageTagOverrides(tag, resourceTags), resourceTags);
        return environment;
    }

    private static void AddSelection(
        Dictionary<string, string?> environment,
        string configurationSection,
        string[] selection)
    {
        var prefix = configurationSection.Replace(":", "__", StringComparison.Ordinal);
        for (var index = 0; index < selection.Length; index++)
        {
            environment[$"{prefix}__{index}"] = selection[index];
        }
    }

    private static void AddTagOverrides(
        Dictionary<string, string?> environment,
        ImageTagOverrides overrides,
        string resourceTags)
    {
        var workflowPrefix = ModuleImageWorkflowConfiguration.ConfigurationSectionName
            .Replace(":", "__", StringComparison.Ordinal);
        AddGlobalTag(environment, workflowPrefix, overrides.GlobalTag);
        AddResourceTags(environment, workflowPrefix, overrides.HasResourceOverrides, resourceTags);
    }

    private static void AddGlobalTag(
        Dictionary<string, string?> environment,
        string workflowPrefix,
        string? globalTag)
    {
        if (globalTag is not null)
        {
            environment[$"{workflowPrefix}__{ModuleImageWorkflowConfiguration.TagConfigurationName}"] = globalTag;
        }
    }

    private static void AddResourceTags(
        Dictionary<string, string?> environment,
        string workflowPrefix,
        bool hasResourceOverrides,
        string resourceTags)
    {
        if (hasResourceOverrides)
        {
            environment[$"{workflowPrefix}__{ModuleImageWorkflowConfiguration.ResourceTagsConfigurationName}"] =
                resourceTags;
        }
    }

    private async Task SaveWorkflowDocumentAsync(
        string workflowPath,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        var document = await ModuleImageWorkflowDocument.LoadAsync(
            Path.Combine(workflowPath, ModuleImageWorkflowDocument.DefaultFileName),
            cancellationToken).ConfigureAwait(false);
        var destination = Path.GetFullPath(
            outputPath ?? "module-image-workflow.json",
            workingDirectory);
        await document.SaveAsync(destination, cancellationToken).ConfigureAwait(false);
        await WriteGitHubOutputsAsync(document, destination).ConfigureAwait(false);
        await output.WriteLineAsync(destination).ConfigureAwait(false);
    }

    private async Task WriteGitHubOutputsAsync(
        ModuleImageWorkflowDocument document,
        string destination)
    {
        if (string.IsNullOrWhiteSpace(configuration["GITHUB_OUTPUT"]))
        {
            return;
        }

        await githubActions.SetOutputAsync("workflow-document", document.ToJson()).ConfigureAwait(false);
        await githubActions.SetOutputAsync("workflow-document-path", destination).ConfigureAwait(false);
    }

    private static void TryDeleteTemporaryPath(string temporaryPath)
    {
        try
        {
            Directory.Delete(temporaryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<ProcessExecutionResult> RunAspireAsync(
        string aspirePath,
        string appHost,
        string step,
        string? outputPath,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        CancellationToken cancellationToken)
    {
        var invocation = AspireCliInvocationResolver.Resolve(
            aspirePath,
            appHost);
        var appHostWorkingDirectory = Directory.Exists(appHost)
            ? appHost
            : Path.GetDirectoryName(appHost)!;
        var arguments = new List<string>(invocation.PrefixArguments)
        {
            "do",
            step,
            "--apphost",
            appHost
        };
        if (outputPath is not null)
        {
            arguments.Add("--output-path");
            arguments.Add(outputPath);
        }

        arguments.Add("--non-interactive");
        return await processRunner.RunAsync(
            new ProcessInvocation(
                invocation.Executable,
                arguments,
                appHostWorkingDirectory,
                environmentVariables,
                ProcessOutputMode.Stream),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> WriteAspireFailureAsync(
        ProcessExecutionResult result,
        string operation)
    {
        await error.WriteLineAsync($"{operation} failed with exit code {result.ExitCode}.")
            .ConfigureAwait(false);
        return ToolExitCode.Failure;
    }

    private static void ValidatePublishArguments(
        string appHost,
        string[] modules,
        string[] resources,
        bool all,
        string aspirePath)
    {
        ValidateAppHostArgument(appHost);
        ValidateAspirePathArgument(aspirePath);
        ValidatePublishSelection(modules, resources, all);
    }

    private static void ValidateAppHostArgument(string appHost)
    {
        if (string.IsNullOrWhiteSpace(appHost))
        {
            throw new ToolUsageException("--apphost cannot be empty.");
        }
    }

    private static void ValidateAspirePathArgument(string aspirePath)
    {
        if (string.IsNullOrWhiteSpace(aspirePath))
        {
            throw new ToolUsageException("--aspire-path cannot be empty.");
        }
    }

    private static void ValidatePublishSelection(string[] modules, string[] resources, bool all)
    {
        var hasSelection = modules.Length > 0 || resources.Length > 0;
        if (all == hasSelection)
        {
            throw new ToolUsageException(
                "Specify one or more --module/--resource values or --all, but not both.");
        }
    }
}
