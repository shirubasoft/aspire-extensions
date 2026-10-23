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
    string? Branch = null,
    string? Target = null,
    IReadOnlyList<string>? SwitchArguments = null);

internal sealed record ModuleRepositoryBranchOutcome(string Repository, string Status, string Detail, bool Changed = false);

// One branch switch at a time per AppHost, whichever resource started it.
internal sealed class ModuleRepositoryBranchSwitchGate
{
    private int _active;

    public bool TryEnter() => Interlocked.Exchange(ref _active, 1) == 0;

    public void Exit() => Volatile.Write(ref _active, 0);
}

internal static class ModuleRepositoryBranchSwitching
{
    internal const string CommandName = "switch-module-branches";
    internal const string AllInputName = "all";
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
                    registry.Options.RepositoryCommandTimeout),
                registry.BranchSwitchGate),
            new CommandOptions
            {
                Description = "Shows the current branch of every module repository and switches them together.",
                IconName = "Branch"
            });
    }

    internal static async Task<ExecuteCommandResult> ExecuteAsync(
        ExecuteCommandContext context,
        IEnumerable<ModuleRepositoryRequirement> requirements,
        ModuleRepositoryInitializationSettings settings,
        ModuleRepositoryBranchSwitchGate gate)
    {
        var interaction = context.Services.GetRequiredService<IInteractionService>();
        if (!interaction.IsAvailable)
        {
            return CommandResults.Failure("Switching branches requires the Aspire dashboard.");
        }

        if (!gate.TryEnter())
        {
            return CommandResults.Failure("A branch switch is already in progress.");
        }

        try
        {
            var git = new GitSession(settings, context.CancellationToken);
            var repositories = await git.InspectAsync(requirements).ConfigureAwait(false);
            return await PromptAndSwitchAsync(interaction, repositories, git).ConfigureAwait(false);
        }
        finally
        {
            gate.Exit();
        }
    }

    private static async Task<ExecuteCommandResult> PromptAndSwitchAsync(
        IInteractionService interaction,
        IReadOnlyList<ModuleRepositoryBranchStatus> repositories,
        GitSession git)
    {
        if (repositories.All(repository => repository.Blocker is not null))
        {
            return CommandResults.Failure(
                "No module repository can switch branches.",
                CreateDetails(FormatPlan(repositories.Select(repository => Plan(repository, null)))));
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
            .. repositories.Select(CreateRepositoryInput)
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
                .OrderByDescending(branch => IsCurrent(repository, branch))
                .Select(branch => KeyValuePair.Create(branch, IsCurrent(repository, branch) ? $"{branch} (current)" : branch))
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
        if (context.Inputs.All(input => string.IsNullOrEmpty(input.Value)))
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
        return repositories
            .Select((repository, index) => Plan(repository, GetSelectedBranch(inputs, GetRepositoryInputName(index)) ?? all))
            .ToArray();
    }

    private static string? GetSelectedBranch(InteractionInputCollection inputs, string name) =>
        string.IsNullOrWhiteSpace(inputs[name].Value) ? null : inputs[name].Value!.Trim();

    internal static ModuleRepositoryBranchPlanItem Plan(ModuleRepositoryBranchStatus repository, string? branch) =>
        IsUnchanged(repository, branch)
            ? new(repository, ModuleRepositoryBranchChange.Keep, repository.Description)
            : repository.Blocker is null
                ? PlanChange(repository, branch)
                : new(repository, ModuleRepositoryBranchChange.Skip, repository.Blocker);

    private static bool IsUnchanged(ModuleRepositoryBranchStatus repository, [NotNullWhen(false)] string? branch) =>
        branch is null || IsCurrent(repository, branch);

    private static ModuleRepositoryBranchPlanItem PlanChange(ModuleRepositoryBranchStatus repository, string branch)
    {
        var from = FormatBranch(repository.CurrentBranch);
        return repository.LocalBranches.Contains(branch, StringComparer.Ordinal)
            ? Change(repository, $"{from} → `{branch}`", branch, $"refs/heads/{branch}", [branch])
            : PlanTrackingBranch(repository, branch, from);
    }

    private static string FormatBranch(string? branch) => branch is null ? "Detached HEAD" : $"`{branch}`";

    private static ModuleRepositoryBranchPlanItem PlanTrackingBranch(
        ModuleRepositoryBranchStatus repository,
        string branch,
        string from) =>
        repository.RemoteBranches.Contains(branch, StringComparer.Ordinal)
            ? Change(
                repository,
                $"{from} → `{branch}`, a new local branch tracking `origin/{branch}`",
                branch,
                $"refs/remotes/origin/{branch}",
                ["--create", branch, "--track", $"origin/{branch}"])
            : new(repository, ModuleRepositoryBranchChange.Skip, $"No `{branch}` branch locally or on origin.");

    private static ModuleRepositoryBranchPlanItem Change(
        ModuleRepositoryBranchStatus repository,
        string summary,
        string branch,
        string target,
        string[] arguments) =>
        new(
            repository,
            ModuleRepositoryBranchChange.Change,
            summary,
            branch,
            target,
            ["switch", "--recurse-submodules", "--no-overwrite-ignore", .. arguments]);

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
            return CommandResults.Success("Nothing to switch.", CreateDetails(FormatPlan(plan)));
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
        var details = CreateDetails(
            string.Join('\n', outcomes.Select(outcome => $"- **{outcome.Repository}** ({outcome.Status}): {outcome.Detail}")) +
            (outcomes.Any(outcome => outcome.Changed)
                ? "\n\nRestart or rebuild resources from the changed repositories to run their new source."
                : string.Empty));
        return outcomes.All(outcome => outcome.Status != AttentionStatus)
            ? CommandResults.Success("Branch switch complete.", details)
            : CommandResults.Failure("Branch switch needs attention.", details);
    }

    private static CommandResultData CreateDetails(string markdown) =>
        new() { Value = markdown, Format = CommandResultFormat.Markdown, DisplayImmediately = true };

    private sealed class GitSession(ModuleRepositoryInitializationSettings settings, CancellationToken cancellationToken)
    {
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

        // Nobody can answer a terminal or credential manager prompt from the dashboard, so git fails instead.
        private static readonly Dictionary<string, string?> NonInteractive = new(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never"
        };

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
                GitHubGitAuthentication.ConfigureCredentialHelper(
                    ["fetch", "--prune", "origin"],
                    requirement.Repository,
                    settings.GitHubCliPath),
                FetchTimeout).ConfigureAwait(false);
            var references = await RunAsync(
                requirement,
                ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes/origin"],
                settings.CommandTimeout).ConfigureAwait(false);
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
                ["status", "--porcelain=v2", "--branch", "--untracked-files=normal"],
                settings.CommandTimeout).ConfigureAwait(false);
            var lines = status.StandardOutput.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return status.IsSuccess
                ? new CheckoutState(
                    GetHeader(lines, "head"),
                    GetHeader(lines, "oid"),
                    lines.Any(line => !line.StartsWith('#')) ? DirtyBlocker : null)
                : new CheckoutState(null, null, $"Couldn't read the checkout: {FormatError(status)}");
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
            var error = CheckReviewedState(item, before) ?? await SwitchCleanCheckoutAsync(item).ConfigureAwait(false);

            // A failed git command can still move HEAD, and a hook can move it again after a successful one,
            // so report the checkout as it is now.
            var after = await ReadStateAsync(item.Repository.Requirement).ConfigureAwait(false);
            var problem = DescribeProblem(item, after, error);
            var changed = !string.Equals(after.Commit, before.Commit, StringComparison.Ordinal);
            return problem is null
                ? new(item.Repository.Name, PlanStatuses[(int)item.Change], item.Summary, changed)
                : new(item.Repository.Name, AttentionStatus, problem, changed);
        }

        private static string? CheckReviewedState(ModuleRepositoryBranchPlanItem item, CheckoutState state) =>
            state.Blocker ?? (string.Equals(state.Branch, item.Repository.CurrentBranch, StringComparison.Ordinal)
                ? null
                : $"Was on {FormatBranch(item.Repository.CurrentBranch)} when you reviewed the plan, so nothing changed.");

        private static string? DescribeProblem(ModuleRepositoryBranchPlanItem item, CheckoutState after, string? error) =>
            error is null
                ? CheckSwitchedState(item, after)
                : string.Join(' ', new[] { after.Location, error }.OfType<string>());

        private static string? CheckSwitchedState(ModuleRepositoryBranchPlanItem item, CheckoutState state) =>
            state.Commit is null
                ? state.Blocker
                : string.Equals(state.Branch, item.Branch, StringComparison.Ordinal)
                    ? null
                    : $"Ended on {FormatBranch(state.Branch)} instead of `{item.Branch}`.";

        private async Task<string?> SwitchCleanCheckoutAsync(ModuleRepositoryBranchPlanItem item)
        {
            var requirement = item.Repository.Requirement;

            // The recursive switch moves existing submodules; the update then clones submodules the branch adds,
            // the same way initialization does.
            return await FindOverwriteAsync(requirement, string.Empty, "HEAD", item.Target!).ConfigureAwait(false)
                ?? await RunStepAsync(requirement, "Switch", item.SwitchArguments!).ConfigureAwait(false)
                ?? await RunStepAsync(
                    requirement,
                    "Submodule update",
                    RepositorySynchronizer.CreateSubmoduleUpdateCommand(
                        requirement.RepositoryPath,
                        settings.GitExecutablePath,
                        settings.GitHubCliPath,
                        requirement.Repository).Arguments).ConfigureAwait(false);
        }

        // git switch --no-overwrite-ignore protects the checkout's own files, but the recursive switch moves
        // each submodule with read-tree, which overwrites ignored files. Before switching, look for anything
        // in the way of a path that a submodule's new commit adds, or of a submodule that the branch adds.
        private async Task<string?> FindOverwriteAsync(
            ModuleRepositoryRequirement requirement,
            string submodule,
            string from,
            string to)
        {
            var diff = await RunAsync(
                requirement,
                ["-C", submodule, "diff", "--raw", "-z", "--no-renames", "--no-abbrev", "--ignore-submodules=none", from, to, "--"],
                settings.CommandTimeout).ConfigureAwait(false);
            var changes = TreeChange.Parse(diff.StandardOutput, submodule);
            return diff.IsSuccess
                ? FindOccupiedPath(requirement, submodule, changes) ??
                    await FindSubmoduleOverwriteAsync(requirement, changes).ConfigureAwait(false)
                : $"Couldn't check what the switch would overwrite: {FormatError(diff)}";
        }

        private static string? FindOccupiedPath(
            ModuleRepositoryRequirement requirement,
            string submodule,
            IEnumerable<TreeChange> changes) =>
            changes
                .Where(change => change.Status == "A")
                .Where(change => submodule.Length > 0 || change.IsSubmodule)
                .Select(change => change.Path)
                .FirstOrDefault(path => IsOccupied(requirement.RepositoryPath, path)) is { } path
                ? $"Switching would overwrite `{path}`, so nothing changed. Move it, then switch again."
                : null;

        // Git replaces whatever is at an added path, and a file where the path needs a directory.
        private static bool IsOccupied(string repository, string path) =>
            Path.Exists(Path.Combine(repository, path)) ||
            Enumerable.Range(0, path.Length)
                .Where(index => path[index] == '/')
                .Any(index => File.Exists(Path.Combine(repository, path[..index])));

        private async Task<string?> FindSubmoduleOverwriteAsync(
            ModuleRepositoryRequirement requirement,
            IEnumerable<TreeChange> changes)
        {
            var submodules = changes
                .Where(change => change.IsSubmodule && Path.Exists(Path.Combine(requirement.RepositoryPath, change.Path, ".git")))
                .ToArray();
            foreach (var submodule in submodules)
            {
                var overwrite = await FindOverwriteAsync(requirement, $"{submodule.Path}/", "HEAD", submodule.Commit)
                    .ConfigureAwait(false);
                if (overwrite is not null)
                {
                    return overwrite;
                }
            }

            return null;
        }

        private async Task<string?> RunStepAsync(
            ModuleRepositoryRequirement requirement,
            string step,
            IReadOnlyList<string> arguments) =>
            FormatFailure(step, await RunAsync(requirement, arguments, settings.CommandTimeout).ConfigureAwait(false));

        private static string? FormatFailure(string step, ModuleCliResult result) =>
            result.IsSuccess ? null : $"{step} failed: {FormatError(result)}";

        private static string FormatError(ModuleCliResult result) =>
            string.Join(
                ' ',
                (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith("hint:", StringComparison.Ordinal)));

        private async Task<ModuleCliResult> RunAsync(
            ModuleRepositoryRequirement requirement,
            IReadOnlyList<string> arguments,
            TimeSpan timeout)
        {
            try
            {
                return await ModuleCliRunner.RunAsync(
                    settings.GitExecutablePath,
                    arguments,
                    requirement.RepositoryPath,
                    timeout,
                    $"git in {Path.GetFileName(requirement.RepositoryPath)}",
                    cancellationToken,
                    static _ => { },
                    NonInteractive).ConfigureAwait(false);
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

        // "git diff --raw -z" writes ":<old mode> <new mode> <old id> <new id> <status>\0<path>\0" per change.
        private sealed record TreeChange(string Status, bool IsSubmodule, string Commit, string Path)
        {
            public static TreeChange[] Parse(string output, string submodule) =>
                output
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Chunk(2)
                    .Select(entry => (Fields: entry[0].Split(' '), Path: submodule + entry[1]))
                    .Select(entry => new TreeChange(
                        entry.Fields[4],
                        entry.Fields[1] == "160000",
                        entry.Fields[3],
                        entry.Path))
                    .ToArray();
        }
    }
}
