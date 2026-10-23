#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIRETERMINAL001

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleRepositoryBranchSwitchingTests
{
    [Fact]
    public async Task Switches_every_repository_to_one_branch_and_reports_each_outcome()
    {
        using var workspace = BranchWorkspace.Create();
        var alpha = await workspace.CloneAsync("alpha", "feature/x");
        var bravo = await workspace.CloneAsync("bravo", "feature/x");
        await GitAsync(bravo.RepositoryPath, "branch", "--track", "feature/x", "origin/feature/x");
        await workspace.PushCommitAsync("bravo", "feature/x");
        var charlie = await workspace.CloneAsync("charlie");
        var delta = await workspace.CloneAsync("delta", "feature/x");
        await File.WriteAllTextAsync(Path.Combine(delta.RepositoryPath, "notes.txt"), "work in progress", TestContext.Current.CancellationToken);
        var echo = workspace.Register("echo", revision: "v1");
        var foxtrot = workspace.Register("foxtrot");
        var interaction = new ScriptedInteractionService(inputs =>
        {
            inputs[0].Value = "feature/x";
            inputs[^1].Value = bool.TrueString;
        });

        var result = await ExecuteAsync(interaction, alpha, bravo, charlie, delta, echo, foxtrot);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Switched branches in 2 repositories.", result.Message);
        var inputs = interaction.Inputs!;
        Assert.Equal(
            ["all", "repository-0", "repository-1", "repository-2", "repository-3", "repository-4", "repository-5", "fast-forward"],
            inputs.Select(input => input.Name));
        Assert.Equal(
            [KeyValuePair.Create("main", "main (in 3 of 3)"), KeyValuePair.Create("feature/x", "feature/x (in 2 of 3)")],
            inputs[0].Options);
        Assert.All(inputs.SkipLast(1), input => Assert.True(input.AllowCustomChoice));
        Assert.Equal("alpha", inputs[1].Label);
        Assert.Equal(
            [KeyValuePair.Create("main", "main (current)"), KeyValuePair.Create("feature/x", "feature/x (from origin)")],
            inputs[1].Options);
        Assert.Equal(
            [KeyValuePair.Create("main", "main (current)"), KeyValuePair.Create("feature/x", "feature/x")],
            inputs[2].Options);
        Assert.Equal("On `main`. Used by `alpha`.", inputs[1].Description);
        Assert.True(inputs[4].Disabled);
        Assert.Equal($"On `main`. {ModuleRepositoryBranchSwitching.DirtyBlocker} Used by `delta`.", inputs[4].Description);
        Assert.True(inputs[5].Disabled);
        Assert.False(interaction.SelectionWasRejected);
        Assert.Equal(
            """
            **Will change**

            - **alpha**: `main` → `feature/x`, a new local branch tracking `origin/feature/x`, then fast-forward to upstream
            - **bravo**: `main` → `feature/x`, then fast-forward to upstream

            **Skipped**

            - **charlie**: No `feature/x` branch locally or on origin.
            - **delta**: Has uncommitted changes; commit or stash them first.
            - **echo**: Pinned to revision `v1`; change `RepositoryRevision` to move it.
            - **foxtrot**: Not initialized; run `aspire do initialize` first.
            """,
            interaction.ConfirmationMessage,
            ignoreLineEndingDifferences: true);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.DisplayImmediately);
        Assert.Equal(CommandResultFormat.Markdown, result.Data.Format);
        Assert.StartsWith(
            """
            **Switched**

            - **alpha**: `main` → `feature/x`, a new local branch tracking `origin/feature/x`, up to date with upstream
            - **bravo**: `main` → `feature/x`, up to date with upstream

            **Skipped**
            """.ReplaceLineEndings("\n"),
            result.Data.Value.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.EndsWith(
            "Restart or rebuild resources from the switched repositories to run their new source.",
            result.Data.Value,
            StringComparison.Ordinal);
        Assert.Equal("feature/x", await GitAsync(alpha.RepositoryPath, "branch", "--show-current"));
        Assert.Equal("origin/feature/x", await GitAsync(alpha.RepositoryPath, "rev-parse", "--abbrev-ref", "@{upstream}"));
        Assert.Equal(
            await GitAsync(bravo.RepositoryPath, "rev-parse", "origin/feature/x"),
            await GitAsync(bravo.RepositoryPath, "rev-parse", "HEAD"));
        Assert.Equal("main", await GitAsync(charlie.RepositoryPath, "branch", "--show-current"));
        Assert.Equal("main", await GitAsync(delta.RepositoryPath, "branch", "--show-current"));
        Assert.True(File.Exists(Path.Combine(delta.RepositoryPath, "notes.txt")));
    }

    [Fact]
    public async Task Repository_choice_overrides_the_all_repositories_choice()
    {
        using var workspace = BranchWorkspace.Create();
        var alpha = await workspace.CloneAsync("alpha", "feature/x");
        var bravo = await workspace.CloneAsync("bravo", "feature/x");
        var interaction = new ScriptedInteractionService(inputs =>
        {
            inputs[0].Value = "feature/x";
            inputs[2].Value = "main";
        });

        var result = await ExecuteAsync(interaction, alpha, bravo);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Switched branches in 1 repository.", result.Message);
        Assert.Contains("**Unchanged**\n\n- **bravo**: On `main`.", interaction.ConfirmationMessage, StringComparison.Ordinal);
        Assert.Equal("feature/x", await GitAsync(alpha.RepositoryPath, "branch", "--show-current"));
        Assert.Equal("main", await GitAsync(bravo.RepositoryPath, "branch", "--show-current"));
    }

    [Fact]
    public async Task Cancelling_either_dialog_leaves_every_checkout_unchanged()
    {
        using var workspace = BranchWorkspace.Create();
        var alpha = await workspace.CloneAsync("alpha", "feature/x");

        var canceledSelection = await ExecuteAsync(new ScriptedInteractionService(select: null), alpha);
        var declined = new ScriptedInteractionService(inputs => inputs[0].Value = "feature/x", confirm: false);
        var canceledConfirmation = await ExecuteAsync(declined, alpha);

        Assert.True(canceledSelection.Canceled);
        Assert.True(canceledConfirmation.Canceled);
        Assert.NotNull(declined.ConfirmationMessage);
        Assert.Equal("main", await GitAsync(alpha.RepositoryPath, "branch", "--show-current"));
    }

    [Fact]
    public async Task Selection_that_changes_nothing_reports_the_plan_without_confirmation()
    {
        using var workspace = BranchWorkspace.Create();
        var alpha = await workspace.CloneAsync("alpha");
        await GitAsync(alpha.RepositoryPath, "remote", "set-url", "origin", Path.Combine(workspace.Path, "missing.git"));
        var interaction = new ScriptedInteractionService(inputs => inputs[0].Value = "main");

        var result = await ExecuteAsync(interaction, alpha);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Nothing to switch.", result.Message);
        Assert.Equal("**Unchanged**\n\n- **alpha**: On `main`.", result.Data?.Value.ReplaceLineEndings("\n"));
        Assert.Null(interaction.ConfirmationMessage);
        Assert.Contains("Couldn't fetch from origin", interaction.Inputs![1].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repositories_that_cannot_switch_are_reported_without_prompting()
    {
        using var workspace = BranchWorkspace.Create();
        var interaction = new ScriptedInteractionService(_ => throw new InvalidOperationException("No prompt expected."));

        var result = await ExecuteAsync(interaction, workspace.Register("alpha", revision: "v1"), workspace.Register("bravo"));

        Assert.False(result.Success);
        Assert.Equal("No module repository can switch branches.", result.Message);
        Assert.Equal(
            "**Unchanged**\n\n" +
            "- **alpha**: Pinned to revision `v1`; change `RepositoryRevision` to move it.\n" +
            "- **bravo**: Not initialized; run `aspire do initialize` first.",
            result.Data?.Value.ReplaceLineEndings("\n"));
        Assert.Null(interaction.Inputs);
    }

    [Fact]
    public async Task Failed_steps_are_reported_per_repository_without_discarding_work()
    {
        using var workspace = BranchWorkspace.Create();
        var locked = await workspace.CloneAsync("alpha", "feature/x");
        var diverged = await workspace.CloneAsync("bravo", "feature/x");
        await GitAsync(diverged.RepositoryPath, "switch", "--create", "feature/x", "--track", "origin/feature/x");
        await File.WriteAllTextAsync(Path.Combine(diverged.RepositoryPath, "local.txt"), "local", TestContext.Current.CancellationToken);
        await GitAsync(diverged.RepositoryPath, "add", "local.txt");
        await GitAsync(diverged.RepositoryPath, "commit", "-m", "local");
        await GitAsync(diverged.RepositoryPath, "switch", "main");
        await workspace.PushCommitAsync("bravo", "feature/x");
        var edited = await workspace.CloneAsync("charlie", "feature/x");
        var interaction = new ScriptedInteractionService(
            inputs =>
            {
                inputs[0].Value = "feature/x";
                inputs[^1].Value = bool.TrueString;
            },
            onConfirm: () =>
            {
                File.WriteAllText(Path.Combine(locked.RepositoryPath, ".git", "index.lock"), string.Empty);
                File.WriteAllText(Path.Combine(edited.RepositoryPath, "draft.txt"), "draft");
            });

        var result = await ExecuteAsync(interaction, locked, diverged, edited);

        Assert.False(result.Success);
        Assert.Equal("Branch switch needs attention in 3 repositories.", result.Message);
        var details = result.Data!.Value.ReplaceLineEndings("\n");
        Assert.StartsWith("**Needs attention**\n\n- **alpha**: Switch failed; nothing changed: ", details, StringComparison.Ordinal);
        Assert.Contains("index.lock", details, StringComparison.Ordinal);
        Assert.Contains("- **bravo**: `main` → `feature/x`, but fast-forward failed: ", details, StringComparison.Ordinal);
        Assert.EndsWith(
            "- **charlie**: Uncommitted changes appeared after review; nothing changed.",
            details,
            StringComparison.Ordinal);
        Assert.Equal("main", await GitAsync(locked.RepositoryPath, "branch", "--show-current"));
        Assert.Equal("feature/x", await GitAsync(diverged.RepositoryPath, "branch", "--show-current"));
        Assert.Equal("local", await GitAsync(diverged.RepositoryPath, "log", "-1", "--format=%s"));
        Assert.Equal("main", await GitAsync(edited.RepositoryPath, "branch", "--show-current"));
        Assert.True(File.Exists(Path.Combine(edited.RepositoryPath, "draft.txt")));
    }

    [Fact]
    public void Plan_keeps_unselected_repositories_and_describes_each_change()
    {
        using var workspace = TemporaryDirectory.Create();
        var requirement = new ModuleRepositoryPlanRegistry(CreateAppHost(workspace.Path))
            .Register("orders", Path.Combine(workspace.Path, "origin.git"), Path.Combine(workspace.Path, "orders"), null, false)
            .Requirement;
        var onMain = new ModuleRepositoryBranchStatus(requirement, "main", ["main"], ["main", "feature/x"], "On `main`.");
        var detached = onMain with { CurrentBranch = null, Location = "Detached HEAD." };

        Assert.Equal(ModuleRepositoryBranchChange.Keep, ModuleRepositoryBranchSwitching.Plan(onMain, null, fastForward: true).Change);
        Assert.Equal(ModuleRepositoryBranchChange.Keep, ModuleRepositoryBranchSwitching.Plan(onMain, "main", fastForward: false).Change);
        var update = ModuleRepositoryBranchSwitching.Plan(onMain, "main", fastForward: true);
        Assert.Equal((ModuleRepositoryBranchChange.Change, "Stays on `main`", 0), (update.Change, update.Summary, update.SwitchArguments.Count));
        var track = ModuleRepositoryBranchSwitching.Plan(detached, "feature/x", fastForward: false);
        Assert.Equal("Detached HEAD → `feature/x`, a new local branch tracking `origin/feature/x`", track.Summary);
        Assert.Equal(["switch", "--create", "feature/x", "--track", "origin/feature/x"], track.SwitchArguments);
        Assert.Equal(["switch", "main"], ModuleRepositoryBranchSwitching.Plan(detached, "main", fastForward: false).SwitchArguments);
    }

    [Fact]
    public async Task Selection_requires_at_least_one_branch()
    {
        var inputs = new[]
        {
            new InteractionInput { Name = ModuleRepositoryBranchSwitching.AllInputName, InputType = InputType.Choice },
            new InteractionInput { Name = ModuleRepositoryBranchSwitching.FastForwardInputName, InputType = InputType.Boolean, Value = bool.TrueString }
        };

        Assert.True(await ValidateAsync(inputs));
        inputs[0].Value = "main";
        Assert.False(await ValidateAsync(inputs));
    }

    [Fact]
    public async Task Run_mode_adds_the_command_to_repository_backed_module_resources()
    {
        using var appHost = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(appHost.Path, ".git"));
        var run = CreateModularBuilder(appHost.Path, []);
        var publish = CreateModularBuilder(appHost.Path, ["--publisher", "manifest"]);

        var command = Assert.Single(
            run.Resources.Single(resource => resource.Name == "orders-api").Annotations.OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == ModuleRepositoryBranchSwitching.CommandName);
        Assert.Equal("Switch module branches", command.DisplayName);
        Assert.DoesNotContain(
            run.Resources.Single(resource => resource.Name == "independent-api").Annotations.OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == ModuleRepositoryBranchSwitching.CommandName);
        Assert.DoesNotContain(
            publish.Resources.SelectMany(resource => resource.Annotations).OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == ModuleRepositoryBranchSwitching.CommandName);
        var result = await command.ExecuteCommand(CreateContext(new ScriptedInteractionService(null) { IsAvailable = false }));
        Assert.False(result.Success);
        Assert.Equal("Switching branches requires the Aspire dashboard.", result.Message);
    }

    private static IDistributedApplicationBuilder CreateModularBuilder(string appHostPath, string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = args,
            AssemblyName = typeof(ModuleRepositoryBranchSwitchingTests).Assembly.FullName,
            ProjectDirectory = appHostPath,
            DisableDashboard = true
        });
        builder.ExportModule("orders", definition =>
        {
            definition.WithRepository("https://example.test/acme/orders.git");
            definition.RequiresRepository();
            definition.AddContainer("orders-api", "busybox");
        });
        builder.ExportModule("independent", definition => definition.AddContainer("independent-api", "busybox"));
        builder.ImportModule("orders");
        builder.ImportModule("independent");
        return builder;
    }

    private static string CreateAppHost(string workspace)
    {
        var appHost = Path.Combine(workspace, "consumer", "src", "AppHost");
        Directory.CreateDirectory(Path.Combine(workspace, "consumer", ".git"));
        Directory.CreateDirectory(appHost);
        return appHost;
    }

    private static async Task<bool> ValidateAsync(IReadOnlyList<InteractionInput> inputs)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new InputsDialogValidationContext
        {
            Inputs = new InteractionInputCollection(inputs),
            CancellationToken = TestContext.Current.CancellationToken,
            Services = services
        };
        await ModuleRepositoryBranchSwitching.ValidateSelection(context);
        return (bool)typeof(InputsDialogValidationContext)
            .GetProperty("HasErrors", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context)!;
    }

    private static Task<ExecuteCommandResult> ExecuteAsync(
        ScriptedInteractionService interaction,
        params ModuleRepositoryRequirement[] requirements) =>
        ModuleRepositoryBranchSwitching.ExecuteAsync(
            CreateContext(interaction),
            requirements,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)));

    private static ExecuteCommandContext CreateContext(IInteractionService interaction) =>
        new()
        {
            Services = new ServiceCollection().AddSingleton(interaction).BuildServiceProvider(),
            ResourceName = "orders-api",
            CancellationToken = TestContext.Current.CancellationToken,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        };

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await CliCommand.Wrap("git")
            .WithArguments(
            [
                "-c", "user.name=Branch Switching Tests",
                "-c", "user.email=branches@example.test",
                "-c", "commit.gpgsign=false",
                .. arguments
            ])
            .WithWorkingDirectory(workingDirectory)
            .ExecuteBufferedAsync(TestContext.Current.CancellationToken);
        return result.StandardOutput.Trim();
    }

    private sealed class BranchWorkspace(TemporaryDirectory directory, ModuleRepositoryPlanRegistry registry) : IDisposable
    {
        public string Path => directory.Path;

        public static BranchWorkspace Create()
        {
            var directory = TemporaryDirectory.Create();
            Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "origins"));
            return new BranchWorkspace(directory, new ModuleRepositoryPlanRegistry(CreateAppHost(directory.Path)));
        }

        public ModuleRepositoryRequirement Register(string name, string? revision = null) =>
            registry.Register(
                    name,
                    GetOrigin(name),
                    System.IO.Path.Combine(Path, name),
                    revision,
                    updateOnInitialize: false)
                .Requirement;

        public async Task<ModuleRepositoryRequirement> CloneAsync(string name, params string[] branches)
        {
            var seed = System.IO.Path.Combine(Path, "seeds", name);
            Directory.CreateDirectory(seed);
            await GitAsync(seed, "init", "--initial-branch", "main");
            await File.WriteAllTextAsync(System.IO.Path.Combine(seed, "README.md"), name, TestContext.Current.CancellationToken);
            await GitAsync(seed, "add", "README.md");
            await GitAsync(seed, "commit", "-m", "initial");
            await GitAsync(Path, "init", "--bare", "--initial-branch", "main", GetOrigin(name));
            await GitAsync(seed, "remote", "add", "origin", GetOrigin(name));
            await GitAsync(seed, ["push", "origin", "main", .. branches.Select(branch => $"main:{branch}")]);
            var requirement = Register(name);
            await GitAsync(Path, "clone", GetOrigin(name), requirement.RepositoryPath);
            return requirement;
        }

        public async Task PushCommitAsync(string name, string branch)
        {
            var seed = System.IO.Path.Combine(Path, "seeds", name);
            await GitAsync(seed, "switch", "--create", branch);
            await File.WriteAllTextAsync(System.IO.Path.Combine(seed, "remote.txt"), branch, TestContext.Current.CancellationToken);
            await GitAsync(seed, "add", "remote.txt");
            await GitAsync(seed, "commit", "-m", "remote");
            await GitAsync(seed, "push", "--force", "origin", branch);
        }

        public void Dispose() => directory.Dispose();

        private string GetOrigin(string name) => System.IO.Path.Combine(Path, "origins", $"{name}.git");
    }

    private sealed class ScriptedInteractionService(
        Action<IReadOnlyList<InteractionInput>>? select,
        bool confirm = true,
        Action? onConfirm = null) : IInteractionService
    {
        public bool IsAvailable { get; init; } = true;

        public IReadOnlyList<InteractionInput>? Inputs { get; private set; }

        public bool SelectionWasRejected { get; private set; }

        public string? ConfirmationMessage { get; private set; }

        public async Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(
            string title,
            string? message,
            IReadOnlyList<InteractionInput> inputs,
            InputsDialogInteractionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Inputs = inputs;
            if (select is null)
            {
                return InteractionResult.Cancel<InteractionInputCollection>();
            }

            select(inputs);
            using var services = new ServiceCollection().BuildServiceProvider();
            var validation = new InputsDialogValidationContext
            {
                Inputs = new InteractionInputCollection(inputs),
                CancellationToken = cancellationToken,
                Services = services
            };
            await options!.ValidationCallback!(validation);
            SelectionWasRejected = (bool)typeof(InputsDialogValidationContext)
                .GetProperty("HasErrors", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(validation)!;
            return InteractionResult.Ok(new InteractionInputCollection(inputs));
        }

        public Task<InteractionResult<bool>> PromptConfirmationAsync(
            string title,
            string message,
            MessageBoxInteractionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ConfirmationMessage = message.ReplaceLineEndings("\n");
            onConfirm?.Invoke();
            return Task.FromResult(confirm ? InteractionResult.Ok(true) : InteractionResult.Cancel<bool>());
        }

        public Task<InteractionResult<bool>> PromptMessageBoxAsync(
            string title,
            string message,
            MessageBoxInteractionOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InteractionResult<InteractionInput>> PromptInputAsync(
            string title,
            string? message,
            string inputLabel,
            string placeHolder,
            InputsDialogInteractionOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InteractionResult<InteractionInput>> PromptInputAsync(
            string title,
            string? message,
            InteractionInput input,
            InputsDialogInteractionOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InteractionResult<bool>> PromptNotificationAsync(
            string title,
            string message,
            NotificationInteractionOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InteractionResult<bool>> PromptProgressAsync(
            string message,
            ProgressInteractionOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InteractionResult<bool>> PromptTerminalAsync(
            string message,
            AspireTerminal terminal,
            TerminalInteractionOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
