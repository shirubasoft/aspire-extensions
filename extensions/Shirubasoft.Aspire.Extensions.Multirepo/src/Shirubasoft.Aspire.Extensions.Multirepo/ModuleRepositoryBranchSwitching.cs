using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

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

internal sealed record ModuleRepositoryBranchOutcome(string Repository, string Status, string Detail, bool Changed = false);

internal static class ModuleRepositoryBranchSwitching
{
    internal const string CommandName = "switch-module-branches";
    internal const string AllInputName = "all";
    internal const string FastForwardInputName = "fast-forward";
    internal const string DirtyBlocker = "Has uncommitted changes; commit or stash them first.";

    private const string AttentionStatus = "needs attention";

    private static readonly string[] PlanSections = ["Will change", "Skipped", "Unchanged"];
    private static readonly string[] PlanStatuses = ["done", "skipped", "unchanged"];

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
            "Switch module branches",
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

        var git = new GitSession(settings, context.CancellationToken);
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
                repositories
                    .Select(repository => new ModuleRepositoryBranchOutcome(
                        repository.Name,
                        PlanStatuses[(int)ModuleRepositoryBranchChange.Skip],
                        repository.Description))
                    .ToArray());
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
        IsUnchanged(repository, branch, fastForward)
            ? new(repository, ModuleRepositoryBranchChange.Keep, repository.Description, [])
            : repository.Blocker is null
                ? PlanChange(repository, branch, fastForward)
                : new(repository, ModuleRepositoryBranchChange.Skip, repository.Blocker, []);

    private static bool IsUnchanged(
        ModuleRepositoryBranchStatus repository,
        [NotNullWhen(false)] string? branch,
        bool fastForward) =>
        branch is null || (IsCurrent(repository, branch) && !fastForward);

    private static ModuleRepositoryBranchPlanItem PlanChange(
        ModuleRepositoryBranchStatus repository,
        string branch,
        bool fastForward)
    {
        if (IsCurrent(repository, branch))
        {
            return Change(repository, $"Stays on `{branch}`", [], fastForward);
        }

        var from = FormatBranch(repository.CurrentBranch);
        return repository.LocalBranches.Contains(branch, StringComparer.Ordinal)
            ? Change(repository, $"{from} → `{branch}`", ["switch", "--no-overwrite-ignore", branch], fastForward)
            : PlanTrackingBranch(repository, branch, from, fastForward);
    }

    private static string FormatBranch(string? branch) => branch is null ? "Detached HEAD" : $"`{branch}`";

    private static ModuleRepositoryBranchPlanItem PlanTrackingBranch(
        ModuleRepositoryBranchStatus repository,
        string branch,
        string from,
        bool fastForward) =>
        repository.RemoteBranches.Contains(branch, StringComparer.Ordinal)
            ? Change(
                repository,
                $"{from} → `{branch}`, a new local branch tracking `origin/{branch}`",
                ["switch", "--no-overwrite-ignore", "--create", branch, "--track", $"origin/{branch}"],
                fastForward)
            : new(repository, ModuleRepositoryBranchChange.Skip, $"No `{branch}` branch locally or on origin.", []);

    private static ModuleRepositoryBranchPlanItem Change(
        ModuleRepositoryBranchStatus repository,
        string summary,
        IReadOnlyList<string> switchArguments,
        bool fastForward) =>
        new(
            repository,
            ModuleRepositoryBranchChange.Change,
            fastForward ? $"{summary}, then fast-forward to upstream" : summary,
            switchArguments,
            fastForward);

    private static string FormatPlan(IEnumerable<ModuleRepositoryBranchPlanItem> plan) =>
        string.Join(
            "\n\n",
            plan
                .GroupBy(item => item.Change)
                .OrderBy(group => group.Key)
                .Select(group =>
                    $"**{PlanSections[(int)group.Key]}**\n\n" +
                    string.Join('\n', group.Select(item => $"- **{item.Repository.Name}**: {item.Summary}"))));

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
        return confirmation.Data
            ? await SwitchAsync(plan, git).ConfigureAwait(false)
            : CommandResults.Canceled();
    }

    private static async Task<ExecuteCommandResult> SwitchAsync(ModuleRepositoryBranchPlanItem[] plan, GitSession git)
    {
        var outcomes = new List<ModuleRepositoryBranchOutcome>();
        foreach (var item in plan)
        {
            outcomes.Add(item.Change == ModuleRepositoryBranchChange.Change
                ? await git.SwitchAsync(item).ConfigureAwait(false)
                : new(item.Repository.Name, PlanStatuses[(int)item.Change], item.Summary));
        }

        return CreateResult(outcomes);
    }

    private static ExecuteCommandResult CreateResult(IReadOnlyCollection<ModuleRepositoryBranchOutcome> outcomes)
    {
        var success = outcomes.All(outcome => outcome.Status != AttentionStatus);
        return CreateResult(
            success,
            success ? "Branch switch complete." : "Branch switch needs attention.",
            outcomes);
    }

    private static ExecuteCommandResult CreateResult(
        bool success,
        string message,
        IReadOnlyCollection<ModuleRepositoryBranchOutcome> outcomes)
    {
        var details = new CommandResultData
        {
            Value = string.Join(
                '\n',
                outcomes.Select(outcome => $"- **{outcome.Repository}** ({outcome.Status}): {outcome.Detail}")) +
                (outcomes.Any(outcome => outcome.Changed)
                    ? "\n\nRestart or rebuild resources from the changed repositories to run their new source."
                    : string.Empty),
            Format = CommandResultFormat.Markdown,
            DisplayImmediately = true
        };
        return success
            ? CommandResults.Success(message, details)
            : CommandResults.Failure(message, details);
    }

    private sealed class GitSession(ModuleRepositoryInitializationSettings settings, CancellationToken cancellationToken)
    {
        private static readonly string[] FastForwardArguments =
            ["merge", "--ff-only", "--no-autostash", "--no-overwrite-ignore", "@{upstream}"];

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
                ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes/origin"]).ConfigureAwait(false);
            var state = await ReadStateAsync(requirement).ConfigureAwait(false);
            var names = references.StandardOutput.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new ModuleRepositoryBranchStatus(
                requirement,
                state.Branch,
                GetBranches(names, "refs/heads/"),
                GetBranches(names, "refs/remotes/origin/"),
                state.Location,
                state.Blocker,
                fetch.IsSuccess
                    ? null
                    : $"Couldn't fetch from origin, so remote branches may be out of date: {FormatError(fetch)}");
        }

        private static string[] GetBranches(IEnumerable<string> references, string prefix) =>
            references
                .Where(reference => reference.StartsWith(prefix, StringComparison.Ordinal))
                .Select(reference => reference[prefix.Length..])
                .Where(branch => !string.Equals(branch, "HEAD", StringComparison.Ordinal))
                .ToArray();

        private async Task<CheckoutState> ReadStateAsync(ModuleRepositoryRequirement requirement)
        {
            var status = await RunAsync(
                requirement,
                "Read status",
                ["status", "--porcelain=v2", "--branch", "--untracked-files=normal"]).ConfigureAwait(false);
            var lines = status.StandardOutput.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new CheckoutState(
                GetHeader(lines, "head"),
                GetHeader(lines, "oid"),
                status.IsSuccess
                    ? lines.Any(line => !line.StartsWith('#')) ? DirtyBlocker : null
                    : $"Couldn't read the checkout: {FormatError(status)}");
        }

        // Porcelain v2 headers are "# branch.head <name>" and "# branch.oid <commit>"; a detached HEAD
        // or an unborn branch reports a parenthesized placeholder instead.
        private static string? GetHeader(IEnumerable<string> lines, string name)
        {
            var prefix = $"# branch.{name} ";
            return lines
                .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
                .Select(line => line[prefix.Length..])
                .FirstOrDefault(value => !value.StartsWith('('));
        }

        public async Task<ModuleRepositoryBranchOutcome> SwitchAsync(ModuleRepositoryBranchPlanItem item)
        {
            var before = await ReadStateAsync(item.Repository.Requirement).ConfigureAwait(false);
            var error = before.Blocker ?? await SwitchCleanCheckoutAsync(item).ConfigureAwait(false);

            // A failed git command can still move HEAD, for example when a post-checkout hook fails,
            // so report the checkout as it is now.
            var after = await ReadStateAsync(item.Repository.Requirement).ConfigureAwait(false);
            var changed = !string.Equals(after.Commit, before.Commit, StringComparison.Ordinal);
            return error is null
                ? new(item.Repository.Name, PlanStatuses[(int)item.Change], item.Summary, changed)
                : new(
                    item.Repository.Name,
                    AttentionStatus,
                    string.Join(' ', new[] { after.Location, error }.OfType<string>()),
                    changed);
        }

        private async Task<string?> SwitchCleanCheckoutAsync(ModuleRepositoryBranchPlanItem item)
        {
            var error = await RunStepAsync(item.Repository.Requirement, "Switch", item.SwitchArguments)
                .ConfigureAwait(false);
            return error ?? await FastForwardAsync(item).ConfigureAwait(false);
        }

        private async Task<string?> FastForwardAsync(ModuleRepositoryBranchPlanItem item)
        {
            if (!item.FastForward)
            {
                return null;
            }

            // Hooks can edit the checkout after the switch, so check it again right before merging.
            var state = await ReadStateAsync(item.Repository.Requirement).ConfigureAwait(false);
            return state.Blocker is null
                ? await RunStepAsync(item.Repository.Requirement, "Fast-forward", FastForwardArguments).ConfigureAwait(false)
                : $"Fast-forward skipped. {state.Blocker}";
        }

        private async Task<string?> RunStepAsync(
            ModuleRepositoryRequirement requirement,
            string step,
            IReadOnlyList<string> arguments)
        {
            if (arguments.Count == 0)
            {
                return null;
            }

            var result = await RunAsync(requirement, step, arguments).ConfigureAwait(false);
            return result.IsSuccess ? null : $"{step} failed: {FormatError(result)}";
        }

        private static string FormatError(ModuleCliResult result) =>
            string.Join(
                ' ',
                (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith("hint:", StringComparison.Ordinal)));

        private async Task<ModuleCliResult> RunAsync(
            ModuleRepositoryRequirement requirement,
            string operation,
            IReadOnlyList<string> arguments)
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
                    static _ => { }).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or Win32Exception)
            {
                return new ModuleCliResult(-1, string.Empty, exception.Message);
            }
        }

        private sealed record CheckoutState(string? Branch, string? Commit, string? Blocker)
        {
            public string? Location => Commit is null ? null : Branch is null ? "Detached HEAD." : $"On `{Branch}`.";
        }
    }
}
