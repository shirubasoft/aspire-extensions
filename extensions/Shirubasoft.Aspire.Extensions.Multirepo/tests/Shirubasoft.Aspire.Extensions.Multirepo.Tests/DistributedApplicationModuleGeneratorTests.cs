using System.Collections.Immutable;
using Aspire.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Aspire.Hosting.Multirepo.Tests;

public sealed class DistributedApplicationModuleGeneratorTests
{
    [Fact]
    public void Generator_caches_equivalent_models_and_invalidates_changed_models()
    {
        const string moduleSource = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("orders")]
            public static partial class OrdersModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("cache", "redis");
                }
            }
            """;
        const string initialUnrelatedSource = """
            public static class Unrelated
            {
                public const int Value = 1;
            }
            """;
        const string updatedUnrelatedSource = """
            public static class Unrelated
            {
                public const int Value = 2;
            }
            """;
        var cancellationToken = TestContext.Current.CancellationToken;
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var moduleTree = CSharpSyntaxTree.ParseText(
            moduleSource,
            parseOptions,
            path: "OrdersModule.cs",
            cancellationToken: cancellationToken);
        var unrelatedTree = CSharpSyntaxTree.ParseText(
            initialUnrelatedSource,
            parseOptions,
            path: "Unrelated.cs",
            cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create(
            "GeneratorCachingTests",
            [moduleTree, unrelatedTree],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DistributedApplicationModuleGenerator().AsSourceGenerator()],
            additionalTexts: [],
            parseOptions: parseOptions,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation, cancellationToken);
        var updatedUnrelatedTree = CSharpSyntaxTree.ParseText(
            updatedUnrelatedSource,
            parseOptions,
            path: "Unrelated.cs",
            cancellationToken: cancellationToken);
        compilation = compilation.ReplaceSyntaxTree(unrelatedTree, updatedUnrelatedTree);
        driver = driver.RunGenerators(compilation, cancellationToken);

        var generatorResult = Assert.Single(driver.GetRunResult().Results);
        var modelStep = Assert.Single(generatorResult.TrackedSteps["ModuleModels"]);
        var modelOutput = Assert.Single(modelStep.Outputs);
        Assert.Equal(IncrementalStepRunReason.Unchanged, modelOutput.Reason);
        var sourceOutputs = generatorResult.TrackedOutputSteps
            .SelectMany(step => step.Value)
            .SelectMany(step => step.Outputs)
            .ToArray();
        Assert.NotEmpty(sourceOutputs);
        Assert.All(sourceOutputs, output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));

        var updatedModuleTree = CSharpSyntaxTree.ParseText(
            moduleSource.Replace("\"cache\"", "\"database\"", StringComparison.Ordinal),
            parseOptions,
            path: "OrdersModule.cs",
            cancellationToken: cancellationToken);
        compilation = compilation.ReplaceSyntaxTree(moduleTree, updatedModuleTree);
        driver = driver.RunGenerators(compilation, cancellationToken);

        generatorResult = Assert.Single(driver.GetRunResult().Results);
        modelStep = Assert.Single(generatorResult.TrackedSteps["ModuleModels"]);
        modelOutput = Assert.Single(modelStep.Outputs);
        Assert.Equal(IncrementalStepRunReason.Modified, modelOutput.Reason);
        var generatedSource = Assert.Single(generatorResult.GeneratedSources).SourceText.ToString();
        Assert.Contains("> Database =>", generatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("> Cache =>", generatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_caches_equivalent_models_with_diagnostics()
    {
        const string moduleSource = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("orders")]
            public static partial class OrdersModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                }
            }
            """;
        const string initialUnrelatedSource = "public static class Unrelated { public const int Value = 1; }";
        const string updatedUnrelatedSource = "public static class Unrelated { public const int Value = 2; }";
        var cancellationToken = TestContext.Current.CancellationToken;
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var moduleTree = CSharpSyntaxTree.ParseText(
            moduleSource,
            parseOptions,
            path: "OrdersModule.cs",
            cancellationToken: cancellationToken);
        var unrelatedTree = CSharpSyntaxTree.ParseText(
            initialUnrelatedSource,
            parseOptions,
            path: "Unrelated.cs",
            cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create(
            "GeneratorDiagnosticCachingTests",
            [moduleTree, unrelatedTree],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DistributedApplicationModuleGenerator().AsSourceGenerator()],
            additionalTexts: [],
            parseOptions: parseOptions,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation, cancellationToken);
        var updatedUnrelatedTree = CSharpSyntaxTree.ParseText(
            updatedUnrelatedSource,
            parseOptions,
            path: "Unrelated.cs",
            cancellationToken: cancellationToken);
        compilation = compilation.ReplaceSyntaxTree(unrelatedTree, updatedUnrelatedTree);
        driver = driver.RunGenerators(compilation, cancellationToken);

        var generatorResult = Assert.Single(driver.GetRunResult().Results);
        var modelStep = Assert.Single(generatorResult.TrackedSteps["ModuleModels"]);
        var modelOutput = Assert.Single(modelStep.Outputs);
        Assert.Equal(IncrementalStepRunReason.Unchanged, modelOutput.Reason);
        Assert.Contains(generatorResult.Diagnostics, diagnostic => diagnostic.Id == "SAMHSG006");
    }

    [Fact]
    public void Generator_creates_a_typed_module_for_every_declared_resource()
    {
        const string source = """
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting;

            namespace GeneratedSample;

            [GenerateDistributedApplicationModule(Name, Version = "2")]
            public static partial class OrdersModule
            {
                public const string Name = "orders";
                public const string CacheResourceName = "orders-cache";

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddProject("orders-api", "Orders.Api.csproj", ModuleProjectPathBase.Repository);
                    module.AddContainer(CacheResourceName, "redis");
                    module.AddResource<ParameterResource>("region", context =>
                        throw new System.NotSupportedException());
                    module.AddResource<ContainerResource>(
                        "database",
                        context => throw new System.NotSupportedException(),
                        new ModuleImageCommandOptions("database", "dotnet", "publish"));
                }
            }

            public static class Consumer
            {
                public static OrdersModule.Module Import(
                    Aspire.Hosting.IDistributedApplicationBuilder builder) =>
                    builder.ImportOrdersModule();
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("Module AddOrdersModule(", generated);
        Assert.Contains("this global::Aspire.Hosting.IDistributedApplicationBuilder builder", generated);
        Assert.Contains("this global::Aspire.Hosting.IDistributedApplicationModuleBuilder moduleBuilder", generated);
        Assert.Contains("DefineModule(builder, \"orders\", \"2\", null, Define)", generated);
        Assert.Contains("moduleBuilder.AddModule(\"orders\", \"2\", null, Define)", generated);
        Assert.Contains("moduleBuilder.ImportModule(\"orders\", \"2\", null, Define, options)", generated);
        Assert.Contains("return AddOrdersModule(builder, module)", generated);
        Assert.Contains("Module ImportOrdersModule(", generated);
        Assert.Contains("ModuleImportOptions options", generated);
        Assert.Contains("Module : global::Aspire.Hosting.DistributedApplicationModuleReference", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.IResourceWithEndpoints> OrdersApi", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ContainerResource> Cache", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ParameterResource> Region", generated);
        Assert.Contains("IResourceBuilder<global::Aspire.Hosting.ApplicationModel.ContainerResource> Database", generated);
        Assert.Contains("ImportModule(builder, \"orders\", options)", generated);
        Assert.Contains(
            "/// <summary>Defines and adds module &apos;orders&apos; version &apos;2&apos; and returns its typed resources.</summary>",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "/// <param name=\"builder\">The receiving Aspire application builder.</param>",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "aspire do initialize --apphost &lt;path&gt; --non-interactive",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "/// <exception cref=\"global::System.InvalidOperationException\">",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "Gets the &apos;orders-api&apos; resource declared by module &apos;orders&apos;.",
            generated,
            StringComparison.Ordinal);
        Assert.DoesNotContain("global::System.Threading.Tasks.Task", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("global::System.Threading.CancellationToken", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("await", generated, StringComparison.Ordinal);
        Assert.Contains(
            typeof(DistributedApplicationModuleGenerator).Assembly.GetName().Version!.ToString(),
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_creates_strongly_typed_composition_apis_for_use_in_other_module_definitions()
    {
        const string source = """
            using Aspire.Hosting;

            namespace GeneratedSample;

            [GenerateDistributedApplicationModule("catalog", Version = "2")]
            public static partial class CatalogModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("api", "catalog");
                }
            }

            [GenerateDistributedApplicationModule("orders")]
            public static partial class OrdersModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    var catalog = module.AddCatalogModule();
                    _ = catalog.Api;
                    module.AddContainer("api", "orders");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains(result.GeneratedSources, generated =>
            generated.Contains("public static Module Reference(", StringComparison.Ordinal) &&
            generated.Contains("GetRequiredModule(\"catalog\", \"2\")", StringComparison.Ordinal) &&
            generated.Contains("moduleBuilder.AddModule(\"catalog\", \"2\", null, Define)", StringComparison.Ordinal) &&
            generated.Contains("moduleBuilder.ImportModule(\"catalog\", \"2\", null, Define, options)", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_requires_a_static_partial_top_level_class()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("invalid")]
            public static class InvalidModule
            {
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG001");
    }

    [Theory]
    [InlineData("public partial class InvalidModule")]
    [InlineData("public static partial class InvalidModule<T>")]
    public void Generator_rejects_non_static_and_generic_module_classes(string declaration)
    {
        var source = $$"""
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("invalid")]
            {{declaration}}
            {
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG001");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_rejects_nested_module_classes()
    {
        const string source = """
            using Aspire.Hosting;

            public static class Outer
            {
                [GenerateDistributedApplicationModule("invalid")]
                public static partial class InvalidModule
                {
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG001");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_rejects_an_empty_module_name()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("   ")]
            public static partial class InvalidModule
            {
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG002");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_rejects_an_empty_module_version()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("invalid", Version = "  ")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("cache", "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG007");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_flows_the_contract_package_identity_into_module_definitions()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("orders", PackageId = "Sample.Orders.Contract")]
            public static partial class OrdersModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("api", "orders");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains(
            "DefineModule(builder, \"orders\", \"1\", \"Sample.Orders.Contract\", Define)",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "String.Equals(module.PackageId, \"Sample.Orders.Contract\"",
            generated,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("contains/slash")]
    public void Generator_rejects_an_invalid_contract_package_id(string packageId)
    {
        var source = $$"""
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("invalid", PackageId = "{{packageId}}")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("api", "invalid");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG009");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_keeps_the_advanced_overload_when_no_conventional_define_method_exists()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("advanced")]
            public static partial class AdvancedModule
            {
                public static void DefineWithInput(IDistributedApplicationModuleBuilder module, string repository)
                {
                    module.AddContainer("cache", "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        var generated = Assert.Single(result.GeneratedSources);
        Assert.DoesNotContain("DefineModule(builder", generated, StringComparison.Ordinal);
        Assert.Contains("IDistributedApplicationModule module)", generated, StringComparison.Ordinal);
        Assert.Contains("String.Equals(module.Name, \"advanced\"", generated, StringComparison.Ordinal);
        Assert.Contains("String.Equals(module.Version, \"1\"", generated, StringComparison.Ordinal);
        Assert.Contains(
            "Expected module 'advanced' with contract version '1' and package ID 'none'.",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_ignores_resource_calls_outside_the_conventional_definition()
    {
        const string source = """
            using System;
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("focused")]
            public static partial class FocusedModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("included", "redis");
                }

                public static void Unrelated(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("excluded-method", "redis");
                }

                public static Action<IDistributedApplicationModuleBuilder> CreateUnrelated() =>
                    module => module.AddContainer("excluded-lambda", "redis");
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("> Included =>", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("ExcludedMethod", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("ExcludedLambda", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_discovers_resources_in_an_advanced_export_lambda()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("advanced")]
            public static partial class AdvancedModule
            {
                public static IDistributedApplicationModule Register(
                    IDistributedApplicationBuilder builder) =>
                    builder.ExportModule("advanced", module =>
                        module.AddContainer("cache", "redis"));
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("> Cache =>", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_rejects_resource_names_that_are_not_compile_time_constants()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module, string resourceName)
                {
                    module.AddContainer(resourceName, "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG003");
    }

    [Fact]
    public void Generator_reports_an_inaccessible_custom_resource_type()
    {
        const string source = """
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting;

            internal sealed class HiddenResource(string name) : Resource(name);

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddResource<HiddenResource>("hidden", context =>
                        context.ApplicationBuilder.AddResource(new HiddenResource(context.ResourceName)));
                }
            }
            """;

        var result = RunGenerator(source);

        var diagnostic = Assert.Single(
            result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "SAMHSG008"));
        Assert.Contains("HiddenResource", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(result.CompilationDiagnostics, diagnostic => diagnostic.Id == "CS0053");
    }

    [Fact]
    public void Generator_rejects_colliding_resource_property_names()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                public const string ApiResourceName = "first-api";
                public const string Api = "second-api";

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer(ApiResourceName, "redis");
                    module.AddContainer(Api, "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "SAMHSG004");
    }

    [Fact]
    public void Generator_rejects_resource_properties_that_collide_with_the_module_contract()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("name", "redis");
                    module.AddContainer("package-id", "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Equal(2, result.GeneratorDiagnostics.Count(diagnostic => diagnostic.Id == "SAMHSG004"));
        Assert.Empty(result.GeneratedSources);
    }

    [Theory]
    [InlineData("public static void AddInvalidModule() { }", "AddInvalidModule")]
    [InlineData("public static int ImportInvalidModule => 0;", "ImportInvalidModule")]
    [InlineData("public static void Reference() { }", "Reference")]
    [InlineData("public sealed class Module { }", "Module")]
    public void Generator_rejects_members_reserved_for_the_generated_api(
        string member,
        string reservedName)
    {
        var source = $$"""
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("invalid")]
            public static partial class InvalidModule
            {
                {{member}}

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("cache", "redis");
                }
            }
            """;

        var result = RunGenerator(source);

        var diagnostic = Assert.Single(
            result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "SAMHSG005"));
        Assert.Contains(reservedName, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_warns_but_still_generates_a_module_without_resources()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("empty")]
            public static partial class EmptyModule
            {
            }
            """;

        var result = RunGenerator(source);

        var diagnostic = Assert.Single(
            result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Id == "SAMHSG006"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("Module AddEmptyModule(", generated);
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Generator_escapes_identifiers_and_preserves_resource_name_constants()
    {
        const string source = """
            using Aspire.Hosting;

            [GenerateDistributedApplicationModule("orders\"north\\region\n")]
            internal static partial class @class
            {
                public const string InventoryResourceName = "inventory-db";

                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer(InventoryResourceName, "postgres");
                    module.AddContainer("2fa-service", "service");
                    module.AddContainer("order_items", "service");
                    module.AddContainer("---", "service");
                }
            }
            """;

        var result = RunGenerator(source);

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("internal static partial class @class", generated);
        Assert.Contains("> Inventory =>", generated);
        Assert.Contains("> _2faService =>", generated);
        Assert.Contains("> OrderItems =>", generated);
        Assert.Contains("> Resource =>", generated);
        Assert.Contains("Module ImportClass(", generated);
        Assert.Contains("ImportModule(builder, \"orders\\\"north\\\\region\\n\", options)", generated);
        Assert.Contains(
            "orders&quot;north\\region&#xA;",
            generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_orders_resources_across_partial_declarations_by_file_then_position()
    {
        const string secondFile = """
            using Aspire.Hosting;

            namespace MultiFile;

            [GenerateDistributedApplicationModule("inventory")]
            internal static partial class InventoryModule
            {
                public static void DefineSecond(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("z-last", "redis");
                }
            }
            """;
        const string firstFile = """
            using Aspire.Hosting;

            namespace MultiFile;

            internal static partial class InventoryModule
            {
                public static void DefineFirst(IDistributedApplicationModuleBuilder module)
                {
                    module.AddContainer("a-first", "redis");
                    module.AddContainer("b-second", "redis");
                }
            }
            """;

        var result = RunGenerator(
            ("02.Inventory.cs", secondFile),
            ("01.Inventory.cs", firstFile));

        Assert.Empty(result.GeneratorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.CompilationDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Contains("internal static partial class InventoryModule", generated);
        Assert.True(generated.IndexOf("> AFirst =>", StringComparison.Ordinal) <
            generated.IndexOf("> BSecond =>", StringComparison.Ordinal));
        Assert.True(generated.IndexOf("> BSecond =>", StringComparison.Ordinal) <
            generated.IndexOf("> ZLast =>", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_suppresses_and_restores_experimental_resource_diagnostics()
    {
        const string source = """
            using System.Diagnostics.CodeAnalysis;
            using Aspire.Hosting.ApplicationModel;
            using Aspire.Hosting;

            [Experimental("SAMPLE001")]
            public sealed class ExperimentalResource(string name) : Resource(name);

            [GenerateDistributedApplicationModule("experimental")]
            public static partial class ExperimentalModule
            {
                public static void Define(IDistributedApplicationModuleBuilder module)
                {
                    module.AddResource<ExperimentalResource>("candidate", context =>
                        context.ApplicationBuilder.AddResource(new ExperimentalResource(context.ResourceName)));
                }
            }
            """;

        var result = RunGenerator(source);

        var generated = Assert.Single(result.GeneratedSources);
        var disable = generated.IndexOf("#pragma warning disable SAMPLE001", StringComparison.Ordinal);
        var resource = generated.IndexOf("IResourceBuilder<global::ExperimentalResource> Candidate", StringComparison.Ordinal);
        var restore = generated.IndexOf("#pragma warning restore SAMPLE001", StringComparison.Ordinal);
        Assert.True(disable >= 0);
        Assert.True(resource > disable);
        Assert.True(restore > resource);
    }

    private static GeneratorTestResult RunGenerator(string source) =>
        RunGenerator(("GeneratorTest.cs", source));

    private static GeneratorTestResult RunGenerator(params (string FilePath, string Source)[] sources)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            sources.Select(source => CSharpSyntaxTree.ParseText(
                source.Source,
                parseOptions,
                path: source.FilePath)),
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DistributedApplicationModuleGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var updatedCompilation,
            out var generatorDiagnostics);

        var runResult = driver.GetRunResult();
        var generatedSources = runResult.Results
            .SelectMany(result => result.GeneratedSources)
            .Select(result => result.SourceText.ToString())
            .ToImmutableArray();

        return new GeneratorTestResult(
            generatedSources,
            generatorDiagnostics,
            updatedCompilation.GetDiagnostics());
    }

    private static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedPlatformAssemblies is not null)
        {
            paths.UnionWith(trustedPlatformAssemblies.Split(Path.PathSeparator));
        }

        paths.UnionWith(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"));

        return paths
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private sealed class GeneratorTestResult(
        ImmutableArray<string> generatedSources,
        ImmutableArray<Diagnostic> generatorDiagnostics,
        ImmutableArray<Diagnostic> compilationDiagnostics)
    {
        public ImmutableArray<string> GeneratedSources { get; } = generatedSources;

        public ImmutableArray<Diagnostic> GeneratorDiagnostics { get; } = generatorDiagnostics;

        public ImmutableArray<Diagnostic> CompilationDiagnostics { get; } = compilationDiagnostics;
    }
}
