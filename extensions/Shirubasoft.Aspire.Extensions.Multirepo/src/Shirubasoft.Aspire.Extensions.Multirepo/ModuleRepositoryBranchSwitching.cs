using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal enum ModuleRepositoryBranchChange
{
    Change,
    Skip,
    Keep
}

internal sealed record ModuleRepositoryBranchStatus(
    ModuleRepositoryRequirement Requirement,
    string? CurrentBranch,
    IReadOnlyList<string> LocalBranches,
    IReadOnlyList<string> RemoteBranches,
    string? Location,
    string? Blocker = null,
    string? FetchWarning = null)
{
    public string Name => Path.GetFileName(Requirement.RepositoryPath);

    public IEnumerable<string> Branches => LocalBranches.Union(RemoteBranches, StringComparer.Ordinal);

    public string Description => string.Join(' ', new[] { Location, Blocker }.OfType<string>());

    public static ModuleRepositoryBranchStatus Blocked(ModuleRepositoryRequirement requirement, string blocker) =>
        new(requirement, CurrentBranch: null, [], [], Location: null, blocker);
}

internal sealed record ModuleRepositoryBranchPlanItem(
    ModuleRepositoryBranchStatus Repository,
    ModuleRepositoryBranchChange Change,
    string Summary,
    IReadOnlyList<string> SwitchArguments,
    bool FastForward = false);

internal static class ModuleRepositoryBranchSwitching
{
    internal const string CommandName = "switch-branches";
    internal const string AllInputName = "all";
    internal const string FastForwardInputName = "fast-forward";
    internal const string DirtyBlocker = "Has uncommitted changes; commit or stash them first.";

    private const string SwitchedSection = "Switched";
    private const string AttentionSection = "Needs attention";

    private static readonly string[] PlanSections = ["Will change", "Skipped", "Unchanged"];

    private static readonly string[] SectionOrder =
        [PlanSections[0], SwitchedSection, AttentionSection, PlanSections[1], PlanSections[2]];

    public static void AddCommand<TResource>(
        IResourceBuilder<TResource> resource,
        ModuleApplicationRegistry registry,
        ModuleRepositoryPreflightScope scope)
        where TResource : IResource
    {
        if (scope.Repositories.Count == 0)
        {
            return;
        }

        resource.WithCommand(
            CommandName,
            "Switch branches",
            context => ExecuteAsync(
                context,
                registry.RepositoryPlans!.Requirements,
                new ModuleRepositoryInitializationSettings(
                    registry.Options.GitExecutablePath,
                    registry.Options.GitHubCliPath,
                    registry.Options.RepositoryCommandTimeout)),
            new CommandOptions
            {
                Description = "Shows the current branch of every module repository and switches them together.",
                IconName = "Branch"
            });
    }

    internal static async Task<ExecuteCommandResult> ExecuteAsync(
        ExecuteCommandContext context,
        IEnumerable<ModuleRepositoryRequirement> requirements,
        ModuleRepositoryInitializationSettings settings)
    {
        var interaction = context.Services.GetRequiredService<IInteractionService>();
        if (!interaction.IsAvailable)
        {
            return CommandResults.Failure("Switching branches requires the Aspire dashboard.");
        }

        var git = new GitSession(settings, context.Logger, context.CancellationToken);
        var repositories = await git.InspectAsync(requirements).ConfigureAwait(false);
        return await PromptAndSwitchAsync(interaction, repositories, git).ConfigureAwait(false);
    }

    private static async Task<ExecuteCommandResult> PromptAndSwitchAsync(
        IInteractionService interaction,
        IReadOnlyList<ModuleRepositoryBranchStatus> repositories,
        GitSession git)
    {
        if (repositories.All(repository => repository.Blocker is not null))
        {
            return CreateResult(
                success: false,
                "No module repository can switch branches.",
                repositories.Select(repository => (PlanSections[2], repository.Name, repository.Description)));
        }

        var selection = await interaction.PromptInputsAsync(
            "Switch module branches",
            "Choose one branch for all repositories, or pick a branch per repository. " +
            "Nothing changes until you confirm the plan in the next step.",
            CreateInputs(repositories),
            new InputsDialogInteractionOptions
            {
                PrimaryButtonText = "Review plan",
                EnableMessageMarkdown = true,
                ValidationCallback = ValidateSelection
            },
            git.CancellationToken).ConfigureAwait(false);
        return selection.Canceled
            ? CommandResults.Canceled()
            : await ConfirmAndSwitchAsync(interaction, CreatePlan(repositories, selection.Data), git)
                .ConfigureAwait(false);
    }

    private static InteractionInput[] CreateInputs(IReadOnlyList<ModuleRepositoryBranchStatus> repositories)
    {
        var switchable = repositories.Where(repository => repository.Blocker is null).ToArray();
        return
        [
            new InteractionInput
            {
                Name = AllInputName,
                Label = "All repositories",
                Description = "Applies to every repository left on *Same as all repositories*.",
                EnableDescriptionMarkdown = true,
                InputType = InputType.Choice,
                // A combobox filters long branch lists as the user types. The dashboard also cannot
                // focus a plain choice when it is a dialog's first input.
                AllowCustomChoice = true,
                Placeholder = "Keep current branches",
                Options = switchable
                    .SelectMany(repository => repository.Branches)
                    .GroupBy(branch => branch, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => KeyValuePair.Create(
                        group.Key,
                        $"{group.Key} (in {group.Count()} of {switchable.Length})"))
                    .ToArray()
            },
            .. repositories.Select(CreateRepositoryInput),
            new InteractionInput
            {
                Name = FastForwardInputName,
                Label = "Fast-forward to upstream",
                Description = "After switching, fast-forwards each branch to its upstream. " +
                    "Never merges, rebases, or discards commits.",
                InputType = InputType.Boolean,
                Value = bool.FalseString
            }
        ];
    }

    private static InteractionInput CreateRepositoryInput(ModuleRepositoryBranchStatus repository, int index) =>
        new()
        {
            Name = GetRepositoryInputName(index),
            Label = repository.Name,
            Description = string.Join(
                ' ',
                new[] { repository.Description, repository.FetchWarning, $"Used by {FormatModules(repository.Requirement)}." }
                    .Where(part => !string.IsNullOrEmpty(part))),
            EnableDescriptionMarkdown = true,
            InputType = InputType.Choice,
            AllowCustomChoice = true,
            Placeholder = repository.Blocker is null ? "Same as all repositories" : "Can't switch",
            Disabled = repository.Blocker is not null,
            Options = repository.LocalBranches
                .Where(branch => IsCurrent(repository, branch))
                .Select(branch => KeyValuePair.Create(branch, $"{branch} (current)"))
                .Concat(repository.LocalBranches
                    .Where(branch => !IsCurrent(repository, branch))
                    .Select(branch => KeyValuePair.Create(branch, branch)))
                .Concat(repository.RemoteBranches
                    .Except(repository.LocalBranches, StringComparer.Ordinal)
                    .Select(branch => KeyValuePair.Create(branch, $"{branch} (from origin)")))
                .ToArray()
        };

    private static bool IsCurrent(ModuleRepositoryBranchStatus repository, string branch) =>
        string.Equals(branch, repository.CurrentBranch, StringComparison.Ordinal);

    private static string FormatModules(ModuleRepositoryRequirement requirement) =>
        string.Join(", ", requirement.ModuleNames
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(module => $"`{module}`"));

    private static string GetRepositoryInputName(int index) => $"repository-{index}";

    internal static Task ValidateSelection(InputsDialogValidationContext context)
    {
        if (context.Inputs.All(input => input.InputType != InputType.Choice || string.IsNullOrEmpty(input.Value)))
        {
            context.AddValidationError(
                AllInputName,
                "Choose a branch for all repositories, or for at least one repository.");
        }

        return Task.CompletedTask;
    }

    private static ModuleRepositoryBranchPlanItem[] CreatePlan(
        IReadOnlyList<ModuleRepositoryBranchStatus> repositories,
        InteractionInputCollection inputs)
    {
        var all = GetSelectedBranch(inputs, AllInputName);
        _ = bool.TryParse(inputs[FastForwardInputName].Value, out var fastForward);
        return repositories
            .Select((repository, index) => Plan(
                repository,
                GetSelectedBranch(inputs, GetRepositoryInputName(index)) ?? all,
                fastForward))
            .ToArray();
    }

    private static string? GetSelectedBranch(InteractionInputCollection inputs, string name) =>
        string.IsNullOrWhiteSpace(inputs[name].Value) ? null : inputs[name].Value!.Trim();

    internal static ModuleRepositoryBranchPlanItem Plan(
        ModuleRepositoryBranchStatus repository,
        string? branch,
        bool fastForward) =>
        branch is null || IsUnchanged(repository, branch, fastForward)
            ? new(repository, ModuleRepositoryBranchChange.Keep, repository.Description, [])
            : PlanSelection(repository, branch, fastForward);

    private static bool IsUnchanged(ModuleRepositoryBranchStatus repository, string branch, bool fastForward) =>
        IsCurrent(repository, branch) && !fastForward;

    private static ModuleRepositoryBranchPlanItem PlanSelection(
        ModuleRepositoryBranchStatus repository,
        string branch,
        bool fastForward) =>
        repository.Blocker is null
            ? PlanChange(repository, branch, fastForward)
            : new(repository, ModuleRepositoryBranchChange.Skip, repository.Blocker, []);

    private static ModuleRepositoryBranchPlanItem PlanChange(
        ModuleRepositoryBranchStatus repository,
        string branch,
        bool fastForward)
    {
        if (IsCurrent(repository, branch))
        {
            return new(repository, ModuleRepositoryBranchChange.Change, $"Stays on `{branch}`", [], fastForward);
        }

        var from = FormatBranch(repository.CurrentBranch);
        return repository.LocalBranches.Contains(branch, StringComparer.Ordinal)
            ? new(repository, ModuleRepositoryBranchChange.Change, $"{from} → `{branch}`", ["switch", branch], fastForward)
            : PlanTrackingBranch(repository, branch, from, fastForward);
    }

    private static string FormatBranch(string? branch) => branch is null ? "Detached HEAD" : $"`{branch}`";

    private static ModuleRepositoryBranchPlanItem PlanTrackingBranch(
        ModuleRepositoryBranchStatus repository,
        string branch,
        string from,
        bool fastForward) =>
        repository.RemoteBranches.Contains(branch, StringComparer.Ordinal)
            ? new(
                repository,
                ModuleRepositoryBranchChange.Change,
                $"{from} → `{branch}`, a new local branch tracking `origin/{branch}`",
                ["switch", "--create", branch, "--track", $"origin/{branch}"],
                fastForward)
            : new(repository, ModuleRepositoryBranchChange.Skip, $"No `{branch}` branch locally or on origin.", []);

    private static string FormatPlan(IEnumerable<ModuleRepositoryBranchPlanItem> plan) =>
        FormatSections(plan.Select(item => (
            PlanSections[(int)item.Change],
            item.Repository.Name,
            item.Summary + FormatFastForward(item, ", then fast-forward to upstream"))));

    private static string FormatFastForward(ModuleRepositoryBranchPlanItem item, string text) =>
        item.FastForward ? text : string.Empty;

    private static async Task<ExecuteCommandResult> ConfirmAndSwitchAsync(
        IInteractionService interaction,
        ModuleRepositoryBranchPlanItem[] plan,
        GitSession git)
    {
        if (plan.All(item => item.Change != ModuleRepositoryBranchChange.Change))
        {
            return CommandResults.Success(
                "Nothing to switch.",
                FormatPlan(plan),
                CommandResultFormat.Markdown,
                displayImmediately: true);
        }

        var confirmation = await interaction.PromptConfirmationAsync(
            "Review branch switch",
            FormatPlan(plan),
            new MessageBoxInteractionOptions
            {
                PrimaryButtonText = "Switch branches",
                SecondaryButtonText = "Cancel",
                EnableMessageMarkdown = true,
                Intent = MessageIntent.Confirmation
            },
            git.CancellationToken).ConfigureAwait(false);
        return IsConfirmed(confirmation)
            ? await SwitchAsync(plan, git).ConfigureAwait(false)
            : CommandResults.Canceled();
    }

    private static bool IsConfirmed(InteractionResult<bool> confirmation) =>
        confirmation is { Canceled: false, Data: true };

    private static async Task<ExecuteCommandResult> SwitchAsync(ModuleRepositoryBranchPlanItem[] plan, GitSession git)
    {
        var outcomes = new List<(string Section, string Repository, string Summary)>();
        foreach (var item in plan)
        {
            outcomes.Add(await SwitchAsync(item, git).ConfigureAwait(false));
        }

        var failed = outcomes.Count(outcome => outcome.Section == AttentionSection);
        return CreateResult(
            failed == 0,
            FormatSwitchMessage(plan.Count(IsChange), failed),
            outcomes,
            When(
                outcomes.Any(outcome => outcome.Section == SwitchedSection),
                "Restart or rebuild resources from the switched repositories to run their new source."));
    }

    private static async Task<(string Section, string Repository, string Summary)> SwitchAsync(
        ModuleRepositoryBranchPlanItem item,
        GitSession git)
    {
        if (!IsChange(item))
        {
            return (PlanSections[(int)item.Change], item.Repository.Name, item.Summary);
        }

        var error = await git.SwitchAsync(item).ConfigureAwait(false);
        return error is null
            ? (SwitchedSection, item.Repository.Name, item.Summary + FormatFastForward(item, ", up to date with upstream"))
            : (AttentionSection, item.Repository.Name, error);
    }

    private static string FormatSwitchMessage(int changed, int failed) =>
        failed == 0
            ? $"Switched branches in {FormatRepositoryCount(changed)}."
            : $"Branch switch needs attention in {FormatRepositoryCount(failed)}.";

    private static bool IsChange(ModuleRepositoryBranchPlanItem item) =>
        item.Change == ModuleRepositoryBranchChange.Change;

    private static string FormatRepositoryCount(int count) =>
        count == 1 ? "1 repository" : $"{count} repositories";

    private static string? When(bool condition, string value) => condition ? value : null;

    private static ExecuteCommandResult CreateResult(
        bool success,
        string message,
        IEnumerable<(string Section, string Repository, string Summary)> entries,
        string? footer = null)
    {
        var details = new CommandResultData
        {
            Value = string.Join("\n\n", new[] { FormatSections(entries), footer }.OfType<string>()),
            Format = CommandResultFormat.Markdown,
            DisplayImmediately = true
        };
        return success
            ? CommandResults.Success(message, details)
            : CommandResults.Failure(message, details);
    }

    private static string FormatSections(IEnumerable<(string Section, string Repository, string Summary)> entries) =>
        string.Join(
            "\n\n",
            entries
                .GroupBy(entry => entry.Section, StringComparer.Ordinal)
                .OrderBy(group => Array.IndexOf(SectionOrder, group.Key))
                .Select(group =>
                    $"**{group.Key}**\n\n" +
                    string.Join('\n', group.Select(entry => $"- **{entry.Repository}**: {entry.Summary}"))));

    private sealed class GitSession(
        ModuleRepositoryInitializationSettings settings,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        private const string FetchWarning =
            "Couldn't fetch from origin, so remote branches may be out of date; see the console logs.";

        private static readonly string[] FastForwardArguments = ["merge", "--ff-only", "@{upstream}"];

        private static readonly Action<ILogger, string, string, Exception?> LogGitOutput =
            LoggerMessage.Define<string, string>(
                LogLevel.Information,
                new EventId(1, nameof(LogGitOutput)),
                "Repository {Repository}: {Output}");

        public CancellationToken CancellationToken => cancellationToken;

        public async Task<IReadOnlyList<ModuleRepositoryBranchStatus>> InspectAsync(
            IEnumerable<ModuleRepositoryRequirement> requirements)
        {
            var repositories = await Task.WhenAll(requirements.Select(InspectAsync)).ConfigureAwait(false);
            return repositories
                .OrderBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private async Task<ModuleRepositoryBranchStatus> InspectAsync(ModuleRepositoryRequirement requirement)
        {
            if (requirement.Revision is not null)
            {
                return ModuleRepositoryBranchStatus.Blocked(
                    requirement,
                    $"Pinned to revision `{requirement.Revision}`; change `RepositoryRevision` to move it.");
            }

            var isGitRepository = await RepositoryInspector.IsGitRepositoryAsync(
                requirement.RepositoryPath,
                settings.GitExecutablePath,
                settings.CommandTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return isGitRepository
                ? await InspectCheckoutAsync(requirement).ConfigureAwait(false)
                : ModuleRepositoryBranchStatus.Blocked(requirement, "Not initialized; run `aspire do initialize` first.");
        }

        private async Task<ModuleRepositoryBranchStatus> InspectCheckoutAsync(ModuleRepositoryRequirement requirement)
        {
            var fetch = await RunAsync(
                requirement,
                "Fetch origin",
                GitHubGitAuthentication.ConfigureCredentialHelper(
                    ["fetch", "--prune", "origin"],
                    requirement.Repository,
                    settings.GitHubCliPath)).ConfigureAwait(false);
            var references = await RunAsync(
                requirement,
                "List branches",
                ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes/origin"],
                static _ => { }).ConfigureAwait(false);
            var branch = await RepositoryInspector.TryGetBranchAsync(
                requirement.RepositoryPath,
                settings.GitExecutablePath,
                settings.CommandTimeout,
                cancellationToken).ConfigureAwait(false);
            var isDirty = await IsDirtyAsync(requirement).ConfigureAwait(false);
            var names = references.StandardOutput.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new ModuleRepositoryBranchStatus(
                requirement,
                branch,
                GetBranches(names, "refs/heads/"),
                GetBranches(names, "refs/remotes/origin/"),
                FormatLocation(branch),
                When(isDirty, DirtyBlocker),
                When(!fetch.IsSuccess, FetchWarning));
        }

        private static string FormatLocation(string? branch) =>
            branch is null ? "Detached HEAD." : $"On `{branch}`.";

        private static string[] GetBranches(IEnumerable<string> references, string prefix) =>
            references
                .Where(reference => reference.StartsWith(prefix, StringComparison.Ordinal))
                .Select(reference => reference[prefix.Length..])
                .Where(branch => !string.Equals(branch, "HEAD", StringComparison.Ordinal))
                .ToArray();

        public async Task<string?> SwitchAsync(ModuleRepositoryBranchPlanItem item)
        {
            var isDirty = await IsDirtyAsync(item.Repository.Requirement).ConfigureAwait(false);
            return isDirty
                ? "Uncommitted changes appeared after review; nothing changed."
                : await SwitchCleanCheckoutAsync(item).ConfigureAwait(false);
        }

        private Task<bool> IsDirtyAsync(ModuleRepositoryRequirement requirement) =>
            RepositoryInspector.IsDirtyAsync(
                requirement.RepositoryPath,
                settings.GitExecutablePath,
                settings.CommandTimeout,
                requireSuccessfulInspection: true,
                cancellationToken);

        private async Task<string?> SwitchCleanCheckoutAsync(ModuleRepositoryBranchPlanItem item)
        {
            var error = await RunStepAsync(
                item.Repository,
                "Switch branch",
                item.SwitchArguments,
                "Switch failed; nothing changed").ConfigureAwait(false);
            return error ?? await RunStepAsync(
                item.Repository,
                "Fast-forward branch",
                item.FastForward ? FastForwardArguments : [],
                $"{item.Summary}, but fast-forward failed").ConfigureAwait(false);
        }

        private async Task<string?> RunStepAsync(
            ModuleRepositoryBranchStatus repository,
            string operation,
            IReadOnlyList<string> arguments,
            string failure)
        {
            if (arguments.Count == 0)
            {
                return null;
            }

            var result = await RunAsync(repository.Requirement, operation, arguments).ConfigureAwait(false);
            return result.IsSuccess ? null : $"{failure}: {FormatError(result)}";
        }

        private static string FormatError(ModuleCliResult result) =>
            string.Join(
                ' ',
                (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        private Task<ModuleCliResult> RunAsync(
            ModuleRepositoryRequirement requirement,
            string operation,
            IReadOnlyList<string> arguments) =>
            RunAsync(
                requirement,
                operation,
                arguments,
                line => LogGitOutput(logger, requirement.NormalizedRepository, line, null));

        private async Task<ModuleCliResult> RunAsync(
            ModuleRepositoryRequirement requirement,
            string operation,
            IReadOnlyList<string> arguments,
            Action<string> progress)
        {
            try
            {
                return await ModuleCliRunner.RunAsync(
                    settings.GitExecutablePath,
                    arguments,
                    requirement.RepositoryPath,
                    settings.CommandTimeout,
                    $"{operation} in {Path.GetFileName(requirement.RepositoryPath)}",
                    cancellationToken,
                    progress).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                progress(exception.Message);
                return new ModuleCliResult(-1, string.Empty, exception.Message);
            }
        }
    }
}
