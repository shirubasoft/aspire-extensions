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
            inputs[3].Value = "main";
            inputs[^1].Value = bool.TrueString;
        });

        var result = await ExecuteAsync(interaction, alpha, bravo, charlie, delta, echo, foxtrot);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Branch switch complete.", result.Message);
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
            - **charlie**: Stays on `main`, then fast-forward to upstream

            **Skipped**

            - **delta**: Has uncommitted changes; commit or stash them first.
            - **echo**: Pinned to revision `v1`; change `RepositoryRevision` to move it.
            - **foxtrot**: Not initialized; run `aspire do initialize` first.
            """,
            interaction.ConfirmationMessage,
            ignoreLineEndingDifferences: true);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.DisplayImmediately);
        Assert.Equal(CommandResultFormat.Markdown, result.Data.Format);
        Assert.Equal(
            """
            - **alpha** (done): `main` → `feature/x`, a new local branch tracking `origin/feature/x`, then fast-forward to upstream
            - **bravo** (done): `main` → `feature/x`, then fast-forward to upstream
            - **charlie** (done): Stays on `main`, then fast-forward to upstream
            - **delta** (skipped): Has uncommitted changes; commit or stash them first.
            - **echo** (skipped): Pinned to revision `v1`; change `RepositoryRevision` to move it.
            - **foxtrot** (skipped): Not initialized; run `aspire do initialize` first.

            Restart or rebuild resources from the changed repositories to run their new source.
            """,
            result.Data.Value,
            ignoreLineEndingDifferences: true);
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
        Assert.Contains("- **bravo** (skipped): Not initialized", result.Data?.Value, StringComparison.Ordinal);
        Assert.Null(interaction.Inputs);
    }

    [Fact]
    public async Task Failed_steps_are_reported_per_repository_without_discarding_work()
    {
        using var workspace = BranchWorkspace.Create();
        var locked = await workspace.CloneAsync("alpha", "feature/x");
        var diverged = await workspace.CloneAsync("bravo", "feature/x");
        await GitAsync(diverged.RepositoryPath, "switch", "--create", "feature/x", "--track", "origin/feature/x");
        await CommitFileAsync(diverged.RepositoryPath, "local.txt", "local");
        await GitAsync(diverged.RepositoryPath, "switch", "main");
        await workspace.PushCommitAsync("bravo", "feature/x");
        var edited = await workspace.CloneAsync("charlie", "feature/x");
        var unreadable = await workspace.CloneAsync("delta", "feature/x");
        var uninspectable = await workspace.CloneAsync("echo", "feature/x");
        var removed = await workspace.CloneAsync("foxtrot", "feature/x");
        await File.WriteAllTextAsync(Path.Combine(uninspectable.RepositoryPath, ".git", "index"), "corrupt", TestContext.Current.CancellationToken);
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
                File.WriteAllText(Path.Combine(unreadable.RepositoryPath, ".git", "index"), "corrupt");
                TemporaryDirectory.DeleteRecursively(removed.RepositoryPath);
                return Task.CompletedTask;
            });

        var result = await ExecuteAsync(interaction, locked, diverged, edited, unreadable, uninspectable, removed);

        Assert.False(result.Success);
        Assert.Equal("Branch switch needs attention.", result.Message);
        var details = result.Data!.Value.ReplaceLineEndings("\n").Split('\n');
        Assert.StartsWith("- **alpha** (needs attention): On `main`. Switch failed: ", details[0], StringComparison.Ordinal);
        Assert.Contains("index.lock", details[0], StringComparison.Ordinal);
        Assert.StartsWith("- **bravo** (needs attention): On `feature/x`. Fast-forward failed: fatal: ", details[1], StringComparison.Ordinal);
        Assert.Equal("- **charlie** (needs attention): On `main`. Has uncommitted changes; commit or stash them first.", details[2]);
        Assert.StartsWith("- **delta** (needs attention): Couldn't read the checkout: ", details[3], StringComparison.Ordinal);
        Assert.StartsWith("- **echo** (skipped): Couldn't read the checkout: ", details[4], StringComparison.Ordinal);
        Assert.StartsWith("- **foxtrot** (needs attention): Couldn't read the checkout: ", details[5], StringComparison.Ordinal);
        Assert.Equal("Restart or rebuild resources from the changed repositories to run their new source.", details[^1]);
        Assert.Equal("main", await GitAsync(locked.RepositoryPath, "branch", "--show-current"));
        Assert.Equal("feature/x", await GitAsync(diverged.RepositoryPath, "branch", "--show-current"));
        Assert.Equal("Add local.txt", await GitAsync(diverged.RepositoryPath, "log", "-1", "--format=%s"));
        Assert.Equal("main", await GitAsync(edited.RepositoryPath, "branch", "--show-current"));
        Assert.True(File.Exists(Path.Combine(edited.RepositoryPath, "draft.txt")));
    }

    [Fact]
    public async Task Overlapping_switches_are_rejected_and_moved_checkouts_are_left_alone()
    {
        using var workspace = BranchWorkspace.Create();
        var alpha = await workspace.CloneAsync("alpha", "feature/x");
        await GitAsync(alpha.RepositoryPath, "branch", "--track", "feature/x", "origin/feature/x");
        var moved = await GitAsync(alpha.RepositoryPath, "rev-parse", "feature/x");
        await workspace.PushCommitAsync("alpha", "feature/x");
        var gate = new ModuleRepositoryBranchSwitchGate();
        ExecuteCommandResult? overlapping = null;
        var interaction = new ScriptedInteractionService(
            inputs =>
            {
                inputs[0].Value = "main";
                inputs[^1].Value = bool.TrueString;
            },
            onConfirm: async () =>
            {
                overlapping = await ExecuteAsync(new ScriptedInteractionService(null), gate, alpha);
                await GitAsync(alpha.RepositoryPath, "switch", "feature/x");
            });

        var result = await ExecuteAsync(interaction, gate, alpha);

        Assert.NotNull(overlapping);
        Assert.False(overlapping.Success);
        Assert.Equal("A branch switch is already in progress.", overlapping.Message);
        Assert.False(result.Success);
        Assert.Equal(
            "- **alpha** (needs attention): On `feature/x`. Was on `main` when you reviewed the plan, so nothing changed.",
            result.Data?.Value);
        Assert.Equal(moved, await GitAsync(alpha.RepositoryPath, "rev-parse", "HEAD"));
        Assert.True((await ExecuteAsync(new ScriptedInteractionService(null), gate, alpha)).Canceled);
    }

    [Fact]
    public async Task Outcomes_report_the_checkout_that_hooks_leave_behind()
    {
        using var workspace = BranchWorkspace.Create();
        var failing = await workspace.CloneAsync("alpha", "feature/x");
        await InstallPostCheckoutHookAsync(failing.RepositoryPath, "echo post-checkout failed >&2; exit 1");
        var editing = await workspace.CloneAsync("bravo", "feature/x");
        await GitAsync(editing.RepositoryPath, "branch", "--track", "feature/x", "origin/feature/x");
        var reviewed = await GitAsync(editing.RepositoryPath, "rev-parse", "feature/x");
        await workspace.PushCommitAsync("bravo", "feature/x");
        await GitAsync(editing.RepositoryPath, "config", "merge.autoStash", "true");
        await InstallPostCheckoutHookAsync(editing.RepositoryPath, "echo hook >> README.md");
        var interaction = new ScriptedInteractionService(inputs =>
        {
            inputs[0].Value = "feature/x";
            inputs[^1].Value = bool.TrueString;
        });

        var result = await ExecuteAsync(interaction, failing, editing);

        Assert.False(result.Success);
        var details = result.Data!.Value.ReplaceLineEndings("\n").Split('\n');
        Assert.Matches(@"^- \*\*alpha\*\* \(needs attention\): On `feature/x`\. Switch failed: .*post-checkout failed$", details[0]);
        Assert.Equal(
            "- **bravo** (needs attention): On `feature/x`. Fast-forward skipped. Has uncommitted changes; commit or stash them first.",
            details[1]);
        Assert.Equal("Restart or rebuild resources from the changed repositories to run their new source.", details[^1]);
        Assert.Equal("feature/x", await GitAsync(failing.RepositoryPath, "branch", "--show-current"));
        Assert.Equal(reviewed, await GitAsync(editing.RepositoryPath, "rev-parse", "HEAD"));
        Assert.Equal("M README.md", await GitAsync(editing.RepositoryPath, "status", "--porcelain"));
        Assert.Empty(await GitAsync(editing.RepositoryPath, "stash", "list"));
    }

    [Fact]
    public async Task Submodules_follow_the_selected_branch()
    {
        using var workspace = BranchWorkspace.Create();
        var child = await workspace.CloneAsync("child");
        await workspace.PushCommitAsync("child", "main");
        var alpha = await workspace.CloneAsync("alpha");
        var submodule = Path.Combine(alpha.RepositoryPath, "child");
        await GitAsync(alpha.RepositoryPath, "-c", "protocol.file.allow=always", "submodule", "add", child.Repository, "child");
        var latest = await GitAsync(submodule, "rev-parse", "HEAD");
        await GitAsync(submodule, "checkout", "--detach", "HEAD~1");
        var pinned = await GitAsync(submodule, "rev-parse", "HEAD");
        await GitAsync(alpha.RepositoryPath, "commit", "--all", "-m", "Pin child");
        await GitAsync(alpha.RepositoryPath, "switch", "--create", "feature/x");
        await GitAsync(submodule, "checkout", "--detach", latest);
        await GitAsync(alpha.RepositoryPath, "commit", "--all", "-m", "Update child");
        await GitAsync(alpha.RepositoryPath, "switch", "main");
        await GitAsync(alpha.RepositoryPath, "submodule", "update");

        foreach (var (branch, commit) in new[] { ("feature/x", latest), ("main", pinned) })
        {
            var result = await ExecuteAsync(new ScriptedInteractionService(inputs => inputs[0].Value = branch), alpha);

            Assert.True(result.Success, result.Data?.Value);
            Assert.Equal(commit, await GitAsync(submodule, "rev-parse", "HEAD"));
            Assert.Empty(await GitAsync(alpha.RepositoryPath, "status", "--porcelain"));
        }
    }

    [Fact]
    public async Task Ignored_files_that_the_selected_branch_tracks_are_kept()
    {
        using var workspace = BranchWorkspace.Create();
        var switching = await workspace.CloneAsync("alpha");
        await CommitFileAsync(switching.RepositoryPath, ".gitignore", "local.env\n");
        await GitAsync(switching.RepositoryPath, "switch", "--create", "feature/x");
        await CommitFileAsync(switching.RepositoryPath, "local.env", "tracked");
        await GitAsync(switching.RepositoryPath, "push", "origin", "main", "feature/x");
        await GitAsync(switching.RepositoryPath, "switch", "main");
        var updating = await workspace.CloneAsync("bravo");
        await CommitFileAsync(updating.RepositoryPath, ".gitignore", "local.env\n");
        var reviewed = await GitAsync(updating.RepositoryPath, "rev-parse", "HEAD");
        await CommitFileAsync(updating.RepositoryPath, "local.env", "tracked");
        await GitAsync(updating.RepositoryPath, "push", "origin", "main");
        await GitAsync(updating.RepositoryPath, "reset", "--hard", reviewed);
        string[] localFiles = [Path.Combine(switching.RepositoryPath, "local.env"), Path.Combine(updating.RepositoryPath, "local.env")];
        await Task.WhenAll(localFiles.Select(file => File.WriteAllTextAsync(file, "local work", TestContext.Current.CancellationToken)));
        var interaction = new ScriptedInteractionService(inputs =>
        {
            inputs[1].Value = "feature/x";
            inputs[2].Value = "main";
            inputs[^1].Value = bool.TrueString;
        });

        var result = await ExecuteAsync(interaction, switching, updating);

        Assert.False(result.Success);
        var details = result.Data!.Value.ReplaceLineEndings("\n").Split('\n');
        Assert.Matches(@"^- \*\*alpha\*\* \(needs attention\): .*Switch failed: .*local\.env", details[0]);
        Assert.Matches(@"^- \*\*bravo\*\* \(needs attention\): .*Fast-forward failed: .*local\.env", details[1]);
        Assert.Equal("main", await GitAsync(switching.RepositoryPath, "branch", "--show-current"));
        Assert.Equal(reviewed, await GitAsync(updating.RepositoryPath, "rev-parse", "HEAD"));
        Assert.All(localFiles, file => Assert.Equal("local work", File.ReadAllText(file)));
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
        Assert.Equal((ModuleRepositoryBranchChange.Change, "Stays on `main`, then fast-forward to upstream", 0), (update.Change, update.Summary, update.SwitchArguments.Count));
        var track = ModuleRepositoryBranchSwitching.Plan(detached, "feature/x", fastForward: false);
        Assert.Equal("Detached HEAD → `feature/x`, a new local branch tracking `origin/feature/x`", track.Summary);
        Assert.Equal(["switch", "--no-overwrite-ignore", "--create", "feature/x", "--track", "origin/feature/x"], track.SwitchArguments);
        Assert.Equal(["switch", "--no-overwrite-ignore", "main"], ModuleRepositoryBranchSwitching.Plan(detached, "main", fastForward: false).SwitchArguments);
        Assert.Equal("No `feature/y` branch locally or on origin.", ModuleRepositoryBranchSwitching.Plan(onMain, "feature/y", fastForward: false).Summary);
    }

    [Fact]
    public async Task Selection_requires_at_least_one_branch()
    {
        var inputs = new[]
        {
            new InteractionInput { Name = ModuleRepositoryBranchSwitching.AllInputName, InputType = InputType.Choice },
            new InteractionInput { Name = ModuleRepositoryBranchSwitching.FastForwardInputName, InputType = InputType.Boolean, Value = bool.TrueString }
        };

        Assert.True(await HasValidationErrorsAsync(ModuleRepositoryBranchSwitching.ValidateSelection, inputs));
        inputs[0].Value = "main";
        Assert.False(await HasValidationErrorsAsync(ModuleRepositoryBranchSwitching.ValidateSelection, inputs));
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

    private static async Task<bool> HasValidationErrorsAsync(
        Func<InputsDialogValidationContext, Task> validate,
        IReadOnlyList<InteractionInput> inputs)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new InputsDialogValidationContext
        {
            Inputs = new InteractionInputCollection(inputs),
            CancellationToken = TestContext.Current.CancellationToken,
            Services = services
        };
        await validate(context);
        return (bool)typeof(InputsDialogValidationContext)
            .GetProperty("HasErrors", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context)!;
    }

    private static Task<ExecuteCommandResult> ExecuteAsync(
        ScriptedInteractionService interaction,
        params ModuleRepositoryRequirement[] requirements) =>
        ExecuteAsync(interaction, new ModuleRepositoryBranchSwitchGate(), requirements);

    private static Task<ExecuteCommandResult> ExecuteAsync(
        ScriptedInteractionService interaction,
        ModuleRepositoryBranchSwitchGate gate,
        params ModuleRepositoryRequirement[] requirements) =>
        ModuleRepositoryBranchSwitching.ExecuteAsync(
            CreateContext(interaction),
            requirements,
            new ModuleRepositoryInitializationSettings("git", "gh", TimeSpan.FromMinutes(1)),
            gate);

    private static ExecuteCommandContext CreateContext(IInteractionService interaction) =>
        new()
        {
            Services = new ServiceCollection().AddSingleton(interaction).BuildServiceProvider(),
            ResourceName = "orders-api",
            CancellationToken = TestContext.Current.CancellationToken,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        };

    private static async Task CommitFileAsync(string repository, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(repository, file), content, TestContext.Current.CancellationToken);
        await GitAsync(repository, "add", "--force", file);
        await GitAsync(repository, "commit", "-m", $"Add {file}");
    }

    private static async Task InstallPostCheckoutHookAsync(string repository, string script)
    {
        var hook = Path.Combine(repository, ".git", "hooks", "post-checkout");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\n{script}\n", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

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
            await CommitFileAsync(seed, "README.md", name);
            await GitAsync(Path, "init", "--bare", "--initial-branch", "main", GetOrigin(name));
            foreach (var branch in branches)
            {
                await CommitAsync(seed, branch, "branch.txt", "--create", branch);
            }

            await GitAsync(seed, "remote", "add", "origin", GetOrigin(name));
            await GitAsync(seed, ["push", "origin", "main", .. branches]);
            var requirement = Register(name);
            await GitAsync(Path, "clone", GetOrigin(name), requirement.RepositoryPath);
            return requirement;
        }

        public async Task PushCommitAsync(string name, string branch)
        {
            var seed = System.IO.Path.Combine(Path, "seeds", name);
            await CommitAsync(seed, branch, "remote.txt", branch);
            await GitAsync(seed, "push", "origin", branch);
        }

        private static async Task CommitAsync(string seed, string branch, string file, params string[] switchArguments)
        {
            await GitAsync(seed, ["switch", .. switchArguments]);
            await CommitFileAsync(seed, file, branch);
            await GitAsync(seed, "switch", "main");
        }

        public void Dispose() => directory.Dispose();

        private string GetOrigin(string name) => System.IO.Path.Combine(Path, "origins", $"{name}.git");
    }

    private sealed class ScriptedInteractionService(
        Action<IReadOnlyList<InteractionInput>>? select,
        bool confirm = true,
        Func<Task>? onConfirm = null) : IInteractionService
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
            SelectionWasRejected = await HasValidationErrorsAsync(options!.ValidationCallback!, inputs);
            return InteractionResult.Ok(new InteractionInputCollection(inputs));
        }

        public async Task<InteractionResult<bool>> PromptConfirmationAsync(
            string title,
            string message,
            MessageBoxInteractionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ConfirmationMessage = message.ReplaceLineEndings("\n");
            await (onConfirm?.Invoke() ?? Task.CompletedTask);
            return confirm ? InteractionResult.Ok(true) : InteractionResult.Cancel<bool>();
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
