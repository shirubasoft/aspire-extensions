using Aspire.Hosting;
using System.Text.Json;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class ModuleImageDescriptionDocumentTests
{
    [Fact]
    public async Task Save_and_load_round_trip_preserves_the_complete_document()
    {
        using var directory = TemporaryDirectory.Create();
        var firstPath = Path.Combine(directory.Path, "nested", "first.json");
        var secondPath = Path.Combine(directory.Path, "second.json");
        var document = CreateDocument();

        await document.SaveAsync(firstPath, TestContext.Current.CancellationToken);
        var loaded = await ModuleImageDescriptionDocument.LoadAsync(
            firstPath,
            TestContext.Current.CancellationToken);
        await loaded.SaveAsync(secondPath, TestContext.Current.CancellationToken);

        var firstJson = await File.ReadAllTextAsync(firstPath, TestContext.Current.CancellationToken);
        Assert.EndsWith("\n", firstJson);
        Assert.Equal(
            firstJson,
            await File.ReadAllTextAsync(secondPath, TestContext.Current.CancellationToken));
        Assert.True(firstJson.IndexOf("\"schemaVersion\"", StringComparison.Ordinal) <
            firstJson.IndexOf("\"modules\"", StringComparison.Ordinal));
        Assert.True(firstJson.IndexOf("\"modules\"", StringComparison.Ordinal) <
            firstJson.IndexOf("\"images\"", StringComparison.Ordinal));
        Assert.Contains("\"resourceKind\": \"project\"", firstJson, StringComparison.Ordinal);

        Assert.Equal(ModuleImageDescriptionDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        var module = Assert.Single(loaded.Modules);
        Assert.Equal("catalog", module.Name);
        Assert.Equal("Sample.Catalog.Contract", module.ContractPackageId);
        var image = Assert.Single(loaded.Images);
        Assert.Equal("catalog", image.Module);
        Assert.Equal("api", image.Resource);
        Assert.Equal("imported-api", image.EffectiveResource);
        Assert.Equal(ModuleResourceKind.Project, image.ResourceKind);
        Assert.Equal("registry.example.test", image.Registry);
        Assert.Equal("acme/catalog", image.Repository);
        Assert.Equal("candidate", image.Tag);
        Assert.Null(image.Digest);
        Assert.Equal("registry.example.test/acme/catalog:candidate", image.Reference);
        Assert.Equal("mirror.example.test/acme/catalog:candidate", image.PullReference);
        Assert.Equal("registry.example.test", image.Push!.Registry);
        Assert.Equal("acme/catalog", image.Push.Repository);
        Assert.Equal("candidate", image.Push.Tag);
        Assert.Equal("registry.example.test/acme/catalog:candidate", image.Push.Reference);
        Assert.Equal("dotnet", image.Build!.Command);
        Assert.Equal(["publish", "Catalog.csproj"], image.Build.Arguments);
        Assert.Equal("/work/catalog", image.Build.WorkingDirectory);
        Assert.Equal("https://example.test/acme/catalog.git", image.Build.Repository);
        Assert.Equal("main", image.Build.Revision);
        Assert.Equal("build-imported-api", image.Build.Step);
    }

    [Fact]
    public void Validate_rejects_an_unknown_schema_duplicate_resources_null_images_and_unknown_kinds()
    {
        var schema = CreateDocument();
        schema.SchemaVersion++;
        Assert.Contains("schema version", Assert.Throws<InvalidDataException>(schema.Validate).Message);

        var duplicate = CreateDocument();
        duplicate.Images.Add(CreateImage("IMPORTED-API"));
        Assert.Contains("duplicate effective resource", Assert.Throws<InvalidDataException>(duplicate.Validate).Message);

        var nullImage = new ModuleImageDescriptionDocument();
        nullImage.Images.Add(null!);
        Assert.Throws<ArgumentNullException>(nullImage.Validate);

        var unknownKind = CreateDocument();
        unknownKind.Images[0].ResourceKind = (ModuleResourceKind)42;
        Assert.Contains("Unsupported resource kind", Assert.Throws<InvalidDataException>(unknownKind.Validate).Message);

        var invalidPackage = CreateDocument();
        invalidPackage.Modules[0].ContractPackageId = "invalid/package";
        Assert.Contains("package ID", Assert.Throws<InvalidDataException>(invalidPackage.Validate).Message);

        var duplicateModule = CreateDocument();
        duplicateModule.Modules.Add(new ModuleImageModuleDescription { Name = "CATALOG" });
        Assert.Contains("duplicate module", Assert.Throws<InvalidDataException>(duplicateModule.Validate).Message);

        var unknownModule = CreateDocument();
        unknownModule.Images[0].Module = "missing";
        Assert.Contains("unknown module", Assert.Throws<InvalidDataException>(unknownModule.Validate).Message);

        var nullModule = CreateDocument();
        nullModule.Modules.Add(null!);
        Assert.Throws<ArgumentNullException>(nullModule.Validate);
    }

    [Theory]
    [InlineData(nameof(ModuleImageDescription.Module))]
    [InlineData(nameof(ModuleImageDescription.Resource))]
    [InlineData(nameof(ModuleImageDescription.EffectiveResource))]
    [InlineData(nameof(ModuleImageDescription.Repository))]
    [InlineData(nameof(ModuleImageDescription.Reference))]
    [InlineData(nameof(ModuleImageDescription.PullReference))]
    public void Validate_rejects_missing_required_image_identities(string propertyName)
    {
        var document = CreateDocument();
        var image = document.Images[0];
        switch (propertyName)
        {
            case nameof(ModuleImageDescription.Module):
                image.Module = " ";
                break;
            case nameof(ModuleImageDescription.Resource):
                image.Resource = " ";
                break;
            case nameof(ModuleImageDescription.EffectiveResource):
                image.EffectiveResource = " ";
                break;
            case nameof(ModuleImageDescription.Repository):
                image.Repository = " ";
                break;
            case nameof(ModuleImageDescription.Reference):
                image.Reference = " ";
                break;
            case nameof(ModuleImageDescription.PullReference):
                image.PullReference = " ";
                break;
        }

        Assert.Equal(propertyName, Assert.Throws<ArgumentException>(document.Validate).ParamName);
    }

    [Fact]
    public async Task Load_rejects_null_and_unknown_json_content()
    {
        using var directory = TemporaryDirectory.Create();
        var nullPath = Path.Combine(directory.Path, "null.json");
        await File.WriteAllTextAsync(nullPath, "null", TestContext.Current.CancellationToken);
        var nullException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ModuleImageDescriptionDocument.LoadAsync(nullPath, TestContext.Current.CancellationToken));
        Assert.Contains("empty", nullException.Message, StringComparison.Ordinal);

        var unknownPath = Path.Combine(directory.Path, "unknown.json");
        await File.WriteAllTextAsync(
            unknownPath,
            """{"schemaVersion":1,"images":[],"unexpected":true}""",
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<JsonException>(() =>
            ModuleImageDescriptionDocument.LoadAsync(unknownPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_and_save_reject_blank_paths_before_file_access()
    {
        var document = CreateDocument();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ModuleImageDescriptionDocument.LoadAsync(" ", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            document.SaveAsync(string.Empty, TestContext.Current.CancellationToken));
    }

    private static ModuleImageDescriptionDocument CreateDocument()
    {
        var document = new ModuleImageDescriptionDocument();
        document.Modules.Add(new ModuleImageModuleDescription
        {
            Name = "catalog",
            ContractPackageId = "Sample.Catalog.Contract"
        });
        document.Images.Add(CreateImage("imported-api"));
        return document;
    }

    private static ModuleImageDescription CreateImage(string effectiveResource)
    {
        var build = new ModuleImageBuildDescription
        {
            Command = "dotnet",
            WorkingDirectory = "/work/catalog",
            Repository = "https://example.test/acme/catalog.git",
            Revision = "main",
            Step = "build-imported-api"
        };
        build.Arguments.Add("publish");
        build.Arguments.Add("Catalog.csproj");
        return new ModuleImageDescription
        {
            Module = "catalog",
            Resource = "api",
            EffectiveResource = effectiveResource,
            ResourceKind = ModuleResourceKind.Project,
            Registry = "registry.example.test",
            Repository = "acme/catalog",
            Tag = "candidate",
            Reference = "registry.example.test/acme/catalog:candidate",
            PullReference = "mirror.example.test/acme/catalog:candidate",
            Push = new ModuleImagePushDescription
            {
                Registry = "registry.example.test",
                Repository = "acme/catalog",
                Tag = "candidate"
            },
            Build = build
        };
    }
}
