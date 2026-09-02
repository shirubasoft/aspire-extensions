using Aspire.Hosting;
using System.Text.Json;
using Xunit;

namespace Shirubasoft.Aspire.Extensions.Multirepo.Tool.Tests;

public sealed class WorkflowDocumentationTests
{
    [Fact]
    public async Task Prerelease_workflow_uses_the_guarded_repository_build_and_publish_contract()
    {
        var repositoryRoot = FindMonorepoRoot();
        var wrapperPath = Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "multirepo-prerelease.yml");
        var sharedWorkflowPath = Path.Combine(
            repositoryRoot,
            ".github",
            "workflows",
            "_extension-prerelease.yml");
        var wrapper = await File.ReadAllTextAsync(
            wrapperPath,
            TestContext.Current.CancellationToken);
        var sharedWorkflow = await File.ReadAllTextAsync(
            sharedWorkflowPath,
            TestContext.Current.CancellationToken);
        wrapper = wrapper.ReplaceLineEndings("\n");
        sharedWorkflow = sharedWorkflow.ReplaceLineEndings("\n");

        Assert.Contains("workflow_dispatch:", wrapper, StringComparison.Ordinal);
        Assert.Contains("confirm_publish:", wrapper, StringComparison.Ordinal);
        Assert.Contains("type: boolean", wrapper, StringComparison.Ordinal);
        Assert.Contains(
            "vars.NUGET_PUBLISH_ENABLED == 'true'",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains("inputs.confirm_publish", wrapper, StringComparison.Ordinal);
        Assert.Contains("github.ref == 'refs/heads/main'", wrapper, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", wrapper, StringComparison.Ordinal);
        Assert.Contains("package_specs:", wrapper, StringComparison.Ordinal);
        Assert.Contains("group: prerelease-${{ inputs.package_id }}", sharedWorkflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: false", sharedWorkflow, StringComparison.Ordinal);
        Assert.Contains("PRERELEASE_VERSION: ${{ inputs.version }}", sharedWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "node .github/release/validate-prerelease-version.mjs \"$PRERELEASE_VERSION\"",
            sharedWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--extension '${{ inputs.extension_path }}'",
            sharedWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("--package-version '${{ inputs.version }}'", sharedWorkflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/${{ inputs.package_id }}/*.nupkg", sharedWorkflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/${{ inputs.package_id }}/*.snupkg", sharedWorkflow, StringComparison.Ordinal);
        Assert.Contains("NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}", sharedWorkflow, StringComparison.Ordinal);
        Assert.Contains("publish-release-assets.sh", sharedWorkflow, StringComparison.Ordinal);
        Assert.True(
            sharedWorkflow.IndexOf("actions/upload-artifact", StringComparison.Ordinal) <
            sharedWorkflow.IndexOf("publish-release-assets.sh", StringComparison.Ordinal));
        Assert.DoesNotContain("contents: write", sharedWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release", sharedWorkflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git tag", sharedWorkflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checked_in_workflow_document_example_matches_the_current_contract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examplePath = Path.Combine(
            repositoryRoot,
            "docs",
            "examples",
            ModuleImageWorkflowDocument.DefaultFileName);

        var document = await ModuleImageWorkflowDocument.LoadAsync(
            examplePath,
            TestContext.Current.CancellationToken);

        Assert.Equal(ModuleImageWorkflowDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal(2, document.Images.Count);
    }

    [Fact]
    public async Task Checked_in_schema_tracks_the_current_workflow_document_version()
    {
        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "module-image-workflow.schema.json");
        await using var stream = File.OpenRead(schemaPath);
        using var schema = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: TestContext.Current.CancellationToken);

        var schemaVersion = schema.RootElement
            .GetProperty("properties")
            .GetProperty("schemaVersion")
            .GetProperty("const")
            .GetInt32();
        Assert.Equal(ModuleImageWorkflowDocument.CurrentSchemaVersion, schemaVersion);
    }

    [Theory]
    [InlineData("repo-a-e2e.yml")]
    [InlineData("repo-b-dispatch.yml")]
    [InlineData("repo-b-workflow-call.yml")]
    public async Task Adoption_workflows_do_not_reimplement_tool_orchestration(string fileName)
    {
        var workflowPath = Path.Combine(FindRepositoryRoot(), "docs", "workflows", fileName);
        var workflow = await File.ReadAllTextAsync(
            workflowPath,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("run: |", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("jq ", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gh workflow run", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gh run watch", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_ENV", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_OUTPUT", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("export Aspire__", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--github-env", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--github-output", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repo_A_workflow_runs_the_E2E_command_through_workflow_apply()
    {
        var workflowPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "workflows",
            "repo-a-e2e.yml");
        var workflow = await File.ReadAllTextAsync(
            workflowPath,
            TestContext.Current.CancellationToken);
        workflow = workflow.ReplaceLineEndings("\n");

        var applyIndex = workflow.IndexOf("images apply", StringComparison.Ordinal);
        var commandBoundaryIndex = workflow.IndexOf("\n          --\n", StringComparison.Ordinal);
        var testIndex = workflow.IndexOf("dotnet test", StringComparison.Ordinal);
        Assert.True(applyIndex >= 0);
        Assert.True(commandBoundaryIndex > applyIndex);
        Assert.True(testIndex > commandBoundaryIndex);
    }

    [Fact]
    public async Task Multi_repo_sample_uses_the_same_portable_apply_command()
    {
        var readmePath = Path.Combine(
            FindRepositoryRoot(),
            "samples",
            "MultiRepoE2E",
            "README.md");
        var readme = await File.ReadAllTextAsync(
            readmePath,
            TestContext.Current.CancellationToken);

        Assert.Contains("images apply", readme, StringComparison.Ordinal);
        Assert.Contains("--file artifacts/manual-module-image-workflow.json", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet test", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_ENV", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("export Aspire__", readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_documentation_rejects_removed_contracts_commands_and_lifecycle_claims()
    {
        var repositoryRoot = FindRepositoryRoot();
        var documentationPaths = new List<string>
        {
            Path.Combine(repositoryRoot, "README.md"),
            Path.Combine(FindMonorepoRoot(), "CONTRIBUTING.md"),
            Path.Combine(repositoryRoot, "docs", "modules.md"),
            Path.Combine(repositoryRoot, "docs", "module-images.md"),
            Path.Combine(repositoryRoot, "docs", "e2e-testing.md"),
            Path.Combine(repositoryRoot, "docs", "external-e2e-workflows.md"),
            Path.Combine(repositoryRoot, "src", "Shirubasoft.Aspire.Extensions.Multirepo", "ModuleContracts.cs"),
            Path.Combine(repositoryRoot, "src", "Shirubasoft.Aspire.Extensions.Multirepo", "ModularAppHostsOptions.cs")
        };
        documentationPaths.AddRange(Directory.EnumerateFiles(
            Path.Combine(repositoryRoot, "samples"),
            "README.md",
            SearchOption.AllDirectories));
        documentationPaths.AddRange(Directory.EnumerateFiles(
            Path.Combine(repositoryRoot, "src"),
            "README.md",
            SearchOption.AllDirectories));
        string[] removedContracts =
        [
            "using Shirubasoft.Aspire.Extensions.Multirepo;",
            "ModuleImageManifestDocument",
            "ModuleImageManifestEntry",
            "modular-apphosts manifest",
            "AutoCloneRepositories",
            "RepositoryBasePath",
            "`ProjectName`"
        ];
        string[] removedLifecycleClaims =
        [
            "startup-time checkout",
            "checkout during startup",
            "clones repositories on startup",
            "automatically clones repositories",
            "image installer",
            "service installer",
            "legacy publish command"
        ];

        foreach (var path in documentationPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var documentation = await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken);
            foreach (var removed in removedContracts.Concat(removedLifecycleClaims))
            {
                Assert.DoesNotContain(removed, documentation, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Current_documentation_does_not_publish_a_migration_guide()
    {
        var repositoryRoot = FindRepositoryRoot();
        Assert.False(File.Exists(Path.Combine(repositoryRoot, "docs", "upgrade-guide.md")));

        string[] primaryGuides =
        [
            Path.Combine(repositoryRoot, "README.md"),
            Path.Combine(FindMonorepoRoot(), "CONTRIBUTING.md"),
            Path.Combine(repositoryRoot, "docs", "modules.md"),
            Path.Combine(repositoryRoot, "docs", "module-images.md")
        ];

        foreach (var path in primaryGuides)
        {
            var documentation = await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain("upgrade guide", documentation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("migration guide", documentation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("upgrade-guide.md", documentation, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Shirubasoft.Aspire.Extensions.Multirepo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }

    private static string FindMonorepoRoot() =>
        Path.GetFullPath(Path.Combine(FindRepositoryRoot(), "..", ".."));
}
