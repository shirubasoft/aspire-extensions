using System.Text.Json;
using Aspire.Hosting;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImageWorkflowDocumentTests
{
    [Fact]
    public async Task Save_load_and_compact_round_trip_are_canonical()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Combine(directory.Path, "workflow-images.json");
        var document = new ModuleImageWorkflowDocument();
        document.Images.Add(CreateEntry("orders", "worker"));
        document.Images.Add(CreateEntry("catalog", "api"));

        await document.SaveAsync(path, TestContext.Current.CancellationToken);
        var loaded = await ModuleImageWorkflowDocument.LoadAsync(path, TestContext.Current.CancellationToken);
        var compact = loaded.ToJson();
        var reparsed = ModuleImageWorkflowDocument.Parse(compact);

        Assert.Equal(["catalog/api", "orders/worker"], reparsed.Images.Select(GetIdentity));
        Assert.DoesNotContain(Environment.NewLine, compact, StringComparison.Ordinal);
        Assert.Equal("registry.example.test/acme/catalog-api:candidate", reparsed.Images[0].Reference);
        Assert.Equal(compact, reparsed.ToJson());
    }

    [Fact]
    public void Parse_is_strict_and_rejects_unknown_properties_and_oversized_payloads()
    {
        const string unknownProperty =
            """
            {"schemaVersion":1,"images":[],"unexpected":true}
            """;
        Assert.Throws<JsonException>(() => ModuleImageWorkflowDocument.Parse(unknownProperty));

        var oversized = "{" + new string(' ', ModuleImageWorkflowDocument.MaximumJsonLength) + "}";
        var exception = Assert.Throws<InvalidDataException>(() => ModuleImageWorkflowDocument.Parse(oversized));
        Assert.Contains("workflow input limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_case_insensitive_duplicates_and_incomplete_or_conflicting_identities()
    {
        var duplicate = new ModuleImageWorkflowDocument();
        duplicate.Images.Add(CreateEntry("orders", "api"));
        duplicate.Images.Add(CreateEntry("ORDERS", "API"));
        Assert.Contains("duplicate", Assert.Throws<InvalidDataException>(duplicate.Validate).Message);

        var incomplete = CreateDocument();
        incomplete.Images[0].Registry = string.Empty;
        Assert.Throws<ArgumentException>(incomplete.Validate);

        var conflicting = CreateDocument();
        conflicting.Images[0].Digest = $"sha256:{new string('a', 64)}";
        Assert.Contains("exactly one", Assert.Throws<InvalidDataException>(conflicting.Validate).Message);
    }

    [Fact]
    public void Digest_identity_round_trips_without_a_tag()
    {
        var document = CreateDocument();
        var image = document.Images[0];
        image.Tag = null;
        image.Digest = $"sha256:{new string('b', 64)}";

        var parsed = ModuleImageWorkflowDocument.Parse(document.ToJson());

        var parsedImage = Assert.Single(parsed.Images);
        Assert.Null(parsedImage.Tag);
        Assert.Equal(image.Digest, parsedImage.Digest);
        Assert.EndsWith($"@{image.Digest}", parsedImage.Reference, StringComparison.Ordinal);
    }

    private static ModuleImageWorkflowDocument CreateDocument()
    {
        var document = new ModuleImageWorkflowDocument();
        document.Images.Add(CreateEntry("catalog", "api"));
        return document;
    }

    private static ModuleImageWorkflowEntry CreateEntry(string module, string resource) => new()
    {
        Module = module,
        Resource = resource,
        ResourceKind = ModuleResourceKind.Project,
        Registry = "registry.example.test",
        Repository = $"acme/{module}-{resource}",
        Tag = "candidate"
    };

    private static string GetIdentity(ModuleImageWorkflowEntry entry) => $"{entry.Module}/{entry.Resource}";

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"modular-apphosts-workflow-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
