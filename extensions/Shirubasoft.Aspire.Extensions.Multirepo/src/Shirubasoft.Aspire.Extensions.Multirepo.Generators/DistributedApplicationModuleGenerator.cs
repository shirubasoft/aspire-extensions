using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Aspire.Hosting;

/// <summary>
/// Generates strongly typed accessors for resources declared by distributed application modules.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DistributedApplicationModuleGenerator : IIncrementalGenerator
{
    private const string ValidPackageIdCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_";

    private static readonly string GeneratorVersion =
        typeof(DistributedApplicationModuleGenerator).Assembly.GetName().Version?.ToString() ?? "unknown";

    private const string AttributeMetadataName =
        "Aspire.Hosting.GenerateDistributedApplicationModuleAttribute";

    private const string ModuleBuilderMetadataName =
        "Aspire.Hosting.IDistributedApplicationModuleBuilder";

    private static readonly DiagnosticDescriptor InvalidModuleDeclaration = new(
        "SAMHSG001",
        "Invalid generated module declaration",
        "Type '{0}' must be a top-level, non-generic, static partial class to generate module resources",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidModuleName = new(
        "SAMHSG002",
        "Invalid generated module name",
        "The module name supplied to GenerateDistributedApplicationModule must be a non-empty compile-time string",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidResourceName = new(
        "SAMHSG003",
        "Resource name cannot be generated",
        "The name passed to '{0}' must be a non-empty compile-time string so a typed resource property can be generated",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ResourcePropertyCollision = new(
        "SAMHSG004",
        "Generated resource property name collision",
        "Module resource '{0}' generates property '{1}', which conflicts with another generated module member",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GeneratedMemberCollision = new(
        "SAMHSG005",
        "Generated module member collision",
        "Type '{0}' already declares '{1}', which is reserved for the generated module API",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoResourcesFound = new(
        "SAMHSG006",
        "No module resources found",
        "Type '{0}' does not contain any supported module resource declarations",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidModuleVersion = new(
        "SAMHSG007",
        "Invalid generated module version",
        "The module version supplied to GenerateDistributedApplicationModule must be a non-empty compile-time string",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InaccessibleResourceType = new(
        "SAMHSG008",
        "Resource type is less accessible than the generated module API",
        "Resource type '{0}' cannot be exposed by generated module '{1}'; make the resource type and its containing types at least as accessible as the module",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidPackageId = new(
        "SAMHSG009",
        "Invalid module package ID",
        "The PackageId supplied to GenerateDistributedApplicationModule must be a valid NuGet package ID",
        "Shirubasoft.Aspire.Extensions.Multirepo",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly HashSet<string> ReservedResourcePropertyNames = new(StringComparer.Ordinal)
    {
        "Name",
        "Version",
        "PackageId",
        "Resources",
        "Projects",
        "Containers",
        "GetResource"
    };

    /// <summary>Registers the incremental pipeline that discovers and generates module accessors.</summary>
    /// <param name="context">The initialization context used to register generator inputs and outputs.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeMetadataName,
            static (node, _) => node is ClassDeclarationSyntax,
            static (attributeContext, cancellationToken) => CreateModel(attributeContext, cancellationToken))
            .WithTrackingName("ModuleModels");

        context.RegisterSourceOutput(modules, static (sourceContext, module) => Generate(sourceContext, module));
    }

    private static ModuleModel CreateModel(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var declaration = (ClassDeclarationSyntax)context.TargetNode;
        var moduleLocation = declaration.Identifier.GetLocation();
        var canGenerate = ValidateModuleDeclaration(symbol, declaration, moduleLocation, diagnostics);
        var attribute = context.Attributes[0];
        var moduleName = GetModuleName(attribute, moduleLocation, diagnostics, ref canGenerate);
        var moduleVersion = GetModuleVersion(attribute, moduleLocation, diagnostics, ref canGenerate);
        var packageId = GetPackageId(attribute, moduleLocation, diagnostics, ref canGenerate);
        var moduleBuilderType = context.SemanticModel.Compilation.GetTypeByMetadataName(ModuleBuilderMetadataName);
        var conventionalDefineMethods = GetConventionalDefinitionMethods(symbol, moduleBuilderType);
        var definitionMethods = GetDefinitionMethods(symbol, moduleBuilderType, conventionalDefineMethods);
        var extensionMethodStem = char.ToUpperInvariant(symbol.Name[0]) + symbol.Name.Substring(1);
        var addExtensionMethodName = "Add" + extensionMethodStem;
        var importExtensionMethodName = "Import" + extensionMethodStem;
        ValidateGeneratedMemberNames(
            symbol,
            moduleLocation,
            addExtensionMethodName,
            importExtensionMethodName,
            diagnostics,
            ref canGenerate);
        var resources = CollectResources(
            symbol,
            definitionMethods,
            context.SemanticModel.Compilation,
            diagnostics,
            cancellationToken);
        ValidateCollectedResources(symbol, moduleLocation, resources, diagnostics, ref canGenerate);
        return CreateModuleModel(
            symbol,
            addExtensionMethodName,
            importExtensionMethodName,
            moduleName,
            moduleVersion,
            packageId,
            conventionalDefineMethods,
            resources,
            diagnostics,
            canGenerate);
    }

    private static bool ValidateModuleDeclaration(
        INamedTypeSymbol symbol,
        ClassDeclarationSyntax declaration,
        Location moduleLocation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        if (IsValidModuleDeclaration(symbol, declaration))
        {
            return true;
        }

        diagnostics.Add(new DiagnosticInfo(
            InvalidModuleDeclaration,
            moduleLocation,
            symbol.ToDisplayString()));
        return false;
    }

    private static bool IsValidModuleDeclaration(
        INamedTypeSymbol symbol,
        ClassDeclarationSyntax declaration)
    {
        if (!symbol.IsStatic)
        {
            return false;
        }

        return IsValidPartialModuleDeclaration(symbol, declaration);
    }

    private static bool IsValidPartialModuleDeclaration(
        INamedTypeSymbol symbol,
        ClassDeclarationSyntax declaration)
    {
        if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return false;
        }

        return IsValidTopLevelModuleDeclaration(symbol);
    }

    private static bool IsValidTopLevelModuleDeclaration(INamedTypeSymbol symbol)
    {
        if (symbol.ContainingType is not null)
        {
            return false;
        }

        return symbol.TypeParameters.Length == 0;
    }

    private static string? GetModuleName(
        AttributeData attribute,
        Location moduleLocation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        var moduleName = ReadModuleName(attribute);
        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            return moduleName;
        }

        diagnostics.Add(new DiagnosticInfo(InvalidModuleName, moduleLocation));
        canGenerate = false;
        return moduleName;
    }

    private static string? ReadModuleName(AttributeData attribute) =>
        attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as string
            : null;

    private static string GetModuleVersion(
        AttributeData attribute,
        Location moduleLocation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        var moduleVersion = GetModuleVersionOrDefault(ReadNamedString(attribute, "Version"));
        if (!string.IsNullOrWhiteSpace(moduleVersion))
        {
            return moduleVersion;
        }

        diagnostics.Add(new DiagnosticInfo(InvalidModuleVersion, moduleLocation));
        canGenerate = false;
        return moduleVersion;
    }

    private static string GetModuleVersionOrDefault(string? moduleVersion) =>
        moduleVersion ?? "1";

    private static string? GetPackageId(
        AttributeData attribute,
        Location moduleLocation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        var packageId = ReadNamedString(attribute, "PackageId");
        if (packageId is null)
        {
            return null;
        }

        return ValidatePackageId(packageId, moduleLocation, diagnostics, ref canGenerate);
    }

    private static string ValidatePackageId(
        string packageId,
        Location moduleLocation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        if (IsValidPackageId(packageId))
        {
            return packageId;
        }

        diagnostics.Add(new DiagnosticInfo(InvalidPackageId, moduleLocation));
        canGenerate = false;
        return packageId;
    }

    private static string? ReadNamedString(AttributeData attribute, string name) =>
        attribute.NamedArguments
            .FirstOrDefault(argument => argument.Key == name)
            .Value.Value as string;

    private static ImmutableArray<IMethodSymbol> GetConventionalDefinitionMethods(
        INamedTypeSymbol symbol,
        INamedTypeSymbol? moduleBuilderType) =>
        symbol.GetMembers("Define")
            .OfType<IMethodSymbol>()
            .Where(method => IsConventionalDefinitionMethod(method, moduleBuilderType))
            .ToImmutableArray();

    private static bool IsConventionalDefinitionMethod(
        IMethodSymbol method,
        INamedTypeSymbol? moduleBuilderType)
    {
        if (!method.IsStatic)
        {
            return false;
        }

        return IsConventionalVoidDefinitionMethod(method, moduleBuilderType);
    }

    private static bool IsConventionalVoidDefinitionMethod(
        IMethodSymbol method,
        INamedTypeSymbol? moduleBuilderType)
    {
        if (!method.ReturnsVoid)
        {
            return false;
        }

        if (method.Parameters.Length != 1)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, moduleBuilderType);
    }

    private static ImmutableArray<IMethodSymbol> GetDefinitionMethods(
        INamedTypeSymbol symbol,
        INamedTypeSymbol? moduleBuilderType,
        ImmutableArray<IMethodSymbol> conventionalDefineMethods)
    {
        if (conventionalDefineMethods.Length > 0)
        {
            return conventionalDefineMethods;
        }

        return symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => IsDefinitionMethod(method, moduleBuilderType))
            .ToImmutableArray();
    }

    private static bool IsDefinitionMethod(IMethodSymbol method, INamedTypeSymbol? moduleBuilderType)
    {
        if (!method.IsStatic)
        {
            return false;
        }

        return method.Parameters.Any(parameter =>
            SymbolEqualityComparer.Default.Equals(parameter.Type, moduleBuilderType));
    }

    private static void ValidateGeneratedMemberNames(
        INamedTypeSymbol symbol,
        Location moduleLocation,
        string addExtensionMethodName,
        string importExtensionMethodName,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        foreach (var memberName in GetGeneratedMemberNames(addExtensionMethodName, importExtensionMethodName))
        {
            ValidateGeneratedMemberName(symbol, moduleLocation, memberName, diagnostics, ref canGenerate);
        }
    }

    private static string[] GetGeneratedMemberNames(
        string addExtensionMethodName,
        string importExtensionMethodName) =>
        [addExtensionMethodName, importExtensionMethodName, "Reference", "Module"];

    private static void ValidateGeneratedMemberName(
        INamedTypeSymbol symbol,
        Location moduleLocation,
        string memberName,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        if (symbol.GetMembers(memberName).Length == 0)
        {
            return;
        }

        diagnostics.Add(new DiagnosticInfo(
            GeneratedMemberCollision,
            moduleLocation,
            symbol.ToDisplayString(),
            memberName));
        canGenerate = false;
    }

    private static void ValidateCollectedResources(
        INamedTypeSymbol symbol,
        Location moduleLocation,
        ImmutableArray<ResourceModel> resources,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        AddMissingResourcesDiagnostic(symbol, moduleLocation, resources, diagnostics);
        ValidateResourcePropertyNames(resources, diagnostics, ref canGenerate);
    }

    private static void AddMissingResourcesDiagnostic(
        INamedTypeSymbol symbol,
        Location moduleLocation,
        ImmutableArray<ResourceModel> resources,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        if (resources.Length > 0)
        {
            return;
        }

        diagnostics.Add(new DiagnosticInfo(NoResourcesFound, moduleLocation, symbol.ToDisplayString()));
    }

    private static void ValidateResourcePropertyNames(
        ImmutableArray<ResourceModel> resources,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        var generatedNames = new HashSet<string>(ReservedResourcePropertyNames, StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            ValidateResourcePropertyName(resource, generatedNames, diagnostics, ref canGenerate);
        }
    }

    private static void ValidateResourcePropertyName(
        ResourceModel resource,
        HashSet<string> generatedNames,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        ref bool canGenerate)
    {
        if (generatedNames.Add(resource.PropertyName))
        {
            return;
        }

        diagnostics.Add(new DiagnosticInfo(
            ResourcePropertyCollision,
            resource.Location,
            resource.ResourceName,
            resource.PropertyName));
        canGenerate = false;
    }

    private static ModuleModel CreateModuleModel(
        INamedTypeSymbol symbol,
        string addExtensionMethodName,
        string importExtensionMethodName,
        string? moduleName,
        string moduleVersion,
        string? packageId,
        ImmutableArray<IMethodSymbol> conventionalDefineMethods,
        ImmutableArray<ResourceModel> resources,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        bool canGenerate) =>
        new(
            GetNamespace(symbol),
            EscapeIdentifier(symbol.Name),
            addExtensionMethodName,
            importExtensionMethodName,
            GetGeneratedAccessibility(symbol),
            moduleName ?? string.Empty,
            moduleVersion,
            packageId,
            conventionalDefineMethods.Length > 0,
            resources,
            diagnostics.ToImmutable(),
            canGenerate);

    private static string? GetNamespace(INamedTypeSymbol symbol) =>
        symbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : symbol.ContainingNamespace.ToDisplayString();

    private static string GetGeneratedAccessibility(INamedTypeSymbol symbol) =>
        symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

    private static bool IsValidPackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        return HasValidPackageIdLengthAndCharacters(packageId);
    }

    private static bool HasValidPackageIdLengthAndCharacters(string packageId)
    {
        if (packageId.Length > 100)
        {
            return false;
        }

        return packageId.All(IsValidPackageIdCharacter);
    }

    private static bool IsValidPackageIdCharacter(char character) =>
        ValidPackageIdCharacters.IndexOf(character) >= 0;

    private static ImmutableArray<ResourceModel> CollectResources(
        INamedTypeSymbol moduleSymbol,
        ImmutableArray<IMethodSymbol> definitionMethods,
        Compilation compilation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var resources = new List<ResourceModel>();
        var containerType = compilation.GetTypeByMetadataName(
            "Aspire.Hosting.ApplicationModel.ContainerResource");
        var resourceWithEndpointsType = compilation.GetTypeByMetadataName(
            "Aspire.Hosting.ApplicationModel.IResourceWithEndpoints");

        foreach (var syntaxReference in moduleSymbol.DeclaringSyntaxReferences)
        {
            resources.AddRange(CollectResourcesFromSyntaxReference(
                syntaxReference,
                moduleSymbol,
                definitionMethods,
                compilation,
                containerType,
                resourceWithEndpointsType,
                diagnostics,
                cancellationToken));
        }

        return resources
            .OrderBy(resource => resource.FilePath, StringComparer.Ordinal)
            .ThenBy(resource => resource.SpanStart)
            .ToImmutableArray();
    }

    private static List<ResourceModel> CollectResourcesFromSyntaxReference(
        SyntaxReference syntaxReference,
        INamedTypeSymbol moduleSymbol,
        ImmutableArray<IMethodSymbol> definitionMethods,
        Compilation compilation,
        INamedTypeSymbol? containerType,
        INamedTypeSymbol? resourceWithEndpointsType,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (syntaxReference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration)
        {
            return [];
        }

        return CollectResourcesFromDeclaration(
            declaration,
            moduleSymbol,
            definitionMethods,
            compilation,
            containerType,
            resourceWithEndpointsType,
            diagnostics,
            cancellationToken);
    }

    private static List<ResourceModel> CollectResourcesFromDeclaration(
        TypeDeclarationSyntax declaration,
        INamedTypeSymbol moduleSymbol,
        ImmutableArray<IMethodSymbol> definitionMethods,
        Compilation compilation,
        INamedTypeSymbol? containerType,
        INamedTypeSymbol? resourceWithEndpointsType,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var resources = new List<ResourceModel>();
        var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var resource = TryCreateResource(
                invocation,
                moduleSymbol,
                definitionMethods,
                compilation,
                semanticModel,
                containerType,
                resourceWithEndpointsType,
                diagnostics,
                cancellationToken);
            if (resource is not null)
            {
                resources.Add(resource);
            }
        }

        return resources;
    }

    private static ResourceModel? TryCreateResource(
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol moduleSymbol,
        ImmutableArray<IMethodSymbol> definitionMethods,
        Compilation compilation,
        SemanticModel semanticModel,
        INamedTypeSymbol? containerType,
        INamedTypeSymbol? resourceWithEndpointsType,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!BelongsToModuleDefinition(invocation, definitionMethods, semanticModel, cancellationToken))
        {
            return null;
        }

        return CreateResourceFromInvocation(
            invocation,
            moduleSymbol,
            compilation,
            semanticModel,
            containerType,
            resourceWithEndpointsType,
            diagnostics,
            cancellationToken);
    }

    private static bool BelongsToModuleDefinition(
        InvocationExpressionSyntax invocation,
        ImmutableArray<IMethodSymbol> definitionMethods,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var enclosingSymbol = semanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken);
        if (definitionMethods.Any(method => SymbolEqualityComparer.Default.Equals(enclosingSymbol, method)))
        {
            return true;
        }

        return IsInsideModuleDefinitionLambda(invocation, semanticModel, cancellationToken);
    }

    private static ResourceModel? CreateResourceFromInvocation(
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol moduleSymbol,
        Compilation compilation,
        SemanticModel semanticModel,
        INamedTypeSymbol? containerType,
        INamedTypeSymbol? resourceWithEndpointsType,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var operation = GetModuleBuilderInvocation(invocation, semanticModel, cancellationToken);
        if (operation is null)
        {
            return null;
        }

        return CreateResourceFromOperation(
            invocation,
            operation,
            moduleSymbol,
            compilation,
            containerType,
            resourceWithEndpointsType,
            diagnostics);
    }

    private static IInvocationOperation? GetModuleBuilderInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (semanticModel.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
        {
            return null;
        }

        return IsModuleBuilderInvocation(operation) ? operation : null;
    }

    private static bool IsModuleBuilderInvocation(IInvocationOperation operation) =>
        operation.TargetMethod.ContainingType.ToDisplayString() == ModuleBuilderMetadataName;

    private static ResourceModel? CreateResourceFromOperation(
        InvocationExpressionSyntax invocation,
        IInvocationOperation operation,
        INamedTypeSymbol moduleSymbol,
        Compilation compilation,
        INamedTypeSymbol? containerType,
        INamedTypeSymbol? resourceWithEndpointsType,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var resourceType = GetResourceType(operation, containerType, resourceWithEndpointsType);
        if (resourceType is null)
        {
            return null;
        }

        return CreateAccessibleResource(
            invocation,
            operation,
            resourceType,
            moduleSymbol,
            compilation,
            diagnostics);
    }

    private static ITypeSymbol? GetResourceType(
        IInvocationOperation operation,
        INamedTypeSymbol? containerType,
        INamedTypeSymbol? resourceWithEndpointsType)
    {
        if (operation.TargetMethod.Name == "AddProject")
        {
            return resourceWithEndpointsType;
        }

        return GetNonProjectResourceType(operation, containerType);
    }

    private static ITypeSymbol? GetNonProjectResourceType(
        IInvocationOperation operation,
        INamedTypeSymbol? containerType)
    {
        if (operation.TargetMethod.Name == "AddContainer")
        {
            return containerType;
        }

        return GetCustomResourceType(operation.TargetMethod);
    }

    private static ITypeSymbol? GetCustomResourceType(IMethodSymbol method)
    {
        if (method.Name != "AddResource")
        {
            return null;
        }

        return GetGenericResourceType(method);
    }

    private static ITypeSymbol? GetGenericResourceType(IMethodSymbol method) =>
        method.TypeArguments.Length == 1 ? method.TypeArguments[0] : null;

    private static ResourceModel? CreateAccessibleResource(
        InvocationExpressionSyntax invocation,
        IInvocationOperation operation,
        ITypeSymbol resourceType,
        INamedTypeSymbol moduleSymbol,
        Compilation compilation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        if (!IsAccessibleForGeneratedApi(resourceType, moduleSymbol, compilation))
        {
            diagnostics.Add(new DiagnosticInfo(
                InaccessibleResourceType,
                invocation.GetLocation(),
                resourceType.ToDisplayString(),
                moduleSymbol.ToDisplayString()));
            return null;
        }

        return CreateNamedResource(invocation, operation, resourceType, diagnostics);
    }

    private static ResourceModel? CreateNamedResource(
        InvocationExpressionSyntax invocation,
        IInvocationOperation operation,
        ITypeSymbol resourceType,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var nameArgument = operation.Arguments.FirstOrDefault(
            argument => argument.Parameter?.Name == "name");
        var resourceName = ReadResourceName(nameArgument);
        if (resourceName is null)
        {
            diagnostics.Add(new DiagnosticInfo(
                InvalidResourceName,
                GetResourceNameLocation(nameArgument, invocation),
                operation.TargetMethod.Name));
            return null;
        }

        return new ResourceModel(
            resourceName,
            GetPropertyName(nameArgument!.Value, resourceName),
            resourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            GetExperimentalDiagnosticIds(resourceType),
            GetFilePath(invocation.SyntaxTree),
            invocation.SpanStart,
            nameArgument.Syntax.GetLocation());
    }

    private static string GetFilePath(SyntaxTree syntaxTree) =>
        syntaxTree.FilePath ?? string.Empty;

    private static string? ReadResourceName(IArgumentOperation? nameArgument)
    {
        if (nameArgument is null)
        {
            return null;
        }

        return ReadResourceNameConstant(nameArgument.Value.ConstantValue);
    }

    private static string? ReadResourceNameConstant(Optional<object?> constantValue)
    {
        if (!constantValue.HasValue)
        {
            return null;
        }

        return NormalizeResourceName(constantValue.Value as string);
    }

    private static string? NormalizeResourceName(string? resourceName) =>
        string.IsNullOrWhiteSpace(resourceName) ? null : resourceName;

    private static Location GetResourceNameLocation(
        IArgumentOperation? nameArgument,
        InvocationExpressionSyntax invocation) =>
        nameArgument?.Syntax.GetLocation() ?? invocation.GetLocation();

    private static bool IsAccessibleForGeneratedApi(
        ITypeSymbol resourceType,
        INamedTypeSymbol moduleSymbol,
        Compilation compilation)
    {
        if (!compilation.IsSymbolAccessibleWithin(resourceType, moduleSymbol))
        {
            return false;
        }

        var requiresPublicAccessibility = moduleSymbol.DeclaredAccessibility == Accessibility.Public;
        return HasSufficientAccessibility(resourceType, requiresPublicAccessibility);
    }

    private static bool HasSufficientAccessibility(ITypeSymbol type, bool requiresPublicAccessibility)
    {
        if (type is IArrayTypeSymbol arrayType)
        {
            return HasSufficientAccessibility(arrayType.ElementType, requiresPublicAccessibility);
        }

        return HasSufficientNonArrayAccessibility(type, requiresPublicAccessibility);
    }

    private static bool HasSufficientNonArrayAccessibility(
        ITypeSymbol type,
        bool requiresPublicAccessibility)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return true;
        }

        return HasSufficientNamedTypeAccessibility(namedType, requiresPublicAccessibility);
    }

    private static bool HasSufficientNamedTypeAccessibility(
        INamedTypeSymbol namedType,
        bool requiresPublicAccessibility)
    {
        for (var current = namedType; current is not null; current = current.ContainingType)
        {
            if (!HasRequiredAccessibility(current, requiresPublicAccessibility))
            {
                return false;
            }
        }

        return HaveSufficientTypeArgumentAccessibility(namedType, requiresPublicAccessibility);
    }

    private static bool HasRequiredAccessibility(
        INamedTypeSymbol type,
        bool requiresPublicAccessibility)
    {
        if (requiresPublicAccessibility)
        {
            return type.DeclaredAccessibility == Accessibility.Public;
        }

        return IsAccessibleFromInternalApi(type.DeclaredAccessibility);
    }

    private static bool IsAccessibleFromInternalApi(Accessibility accessibility) =>
        accessibility is Accessibility.Public or
            Accessibility.Internal or
            Accessibility.ProtectedOrInternal;

    private static bool HaveSufficientTypeArgumentAccessibility(
        INamedTypeSymbol namedType,
        bool requiresPublicAccessibility) =>
        namedType.TypeArguments.All(argument =>
            HasSufficientAccessibility(argument, requiresPublicAccessibility));

    private static bool IsInsideModuleDefinitionLambda(
        InvocationExpressionSyntax resourceInvocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var lambda in resourceInvocation.Ancestors().OfType<AnonymousFunctionExpressionSyntax>())
        {
            if (IsModuleDefinitionLambda(lambda, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsModuleDefinitionLambda(
        AnonymousFunctionExpressionSyntax lambda,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var operation = FindArgumentOperation(semanticModel.GetOperation(lambda, cancellationToken));
        return IsModuleBuilderArgument(operation);
    }

    private static IOperation? FindArgumentOperation(IOperation? operation)
    {
        while (operation is not null && operation is not IArgumentOperation)
        {
            operation = operation.Parent;
        }

        return operation;
    }

    private static bool IsModuleBuilderArgument(IOperation? operation)
    {
        if (operation is not IArgumentOperation argument)
        {
            return false;
        }

        return IsNamedModuleBuilderArgument(argument);
    }

    private static bool IsNamedModuleBuilderArgument(IArgumentOperation argument)
    {
        if (argument.Parameter?.Name != "moduleBuilder")
        {
            return false;
        }

        return IsOwnedByModuleDefinition(argument);
    }

    private static bool IsOwnedByModuleDefinition(IArgumentOperation argument)
    {
        if (argument.Parent is not IInvocationOperation owner)
        {
            return false;
        }

        return IsModuleDefinitionOwner(owner);
    }

    private static bool IsModuleDefinitionOwner(IInvocationOperation owner)
    {
        if (!IsModuleDefinitionMethodName(owner.TargetMethod.Name))
        {
            return false;
        }

        return owner.TargetMethod.ContainingType.ToDisplayString() ==
            "Aspire.Hosting.DistributedApplicationModuleExtensions";
    }

    private static bool IsModuleDefinitionMethodName(string methodName) =>
        methodName is "DefineModule" or "ExportModule";

    private static ImmutableArray<string> GetExperimentalDiagnosticIds(ITypeSymbol resourceType)
    {
        var diagnosticIds = ImmutableArray.CreateBuilder<string>();
        for (var currentType = resourceType as INamedTypeSymbol;
             currentType is not null;
             currentType = currentType.BaseType)
        {
            AddExperimentalDiagnosticIds(diagnosticIds, currentType);
        }

        return diagnosticIds.Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    private static void AddExperimentalDiagnosticIds(
        ImmutableArray<string>.Builder diagnosticIds,
        INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            AddExperimentalDiagnosticId(diagnosticIds, attribute);
        }
    }

    private static void AddExperimentalDiagnosticId(
        ImmutableArray<string>.Builder diagnosticIds,
        AttributeData attribute)
    {
        if (!IsExperimentalAttribute(attribute))
        {
            return;
        }

        AddExperimentalConstructorArgument(diagnosticIds, attribute.ConstructorArguments);
    }

    private static bool IsExperimentalAttribute(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() ==
            "System.Diagnostics.CodeAnalysis.ExperimentalAttribute";

    private static void AddExperimentalConstructorArgument(
        ImmutableArray<string>.Builder diagnosticIds,
        ImmutableArray<TypedConstant> constructorArguments)
    {
        if (constructorArguments.Length != 1)
        {
            return;
        }

        AddExperimentalDiagnosticId(diagnosticIds, constructorArguments[0]);
    }

    private static void AddExperimentalDiagnosticId(
        ImmutableArray<string>.Builder diagnosticIds,
        TypedConstant constructorArgument)
    {
        if (constructorArgument.Value is string diagnosticId)
        {
            diagnosticIds.Add(diagnosticId);
        }
    }

    private static string GetPropertyName(IOperation nameOperation, string resourceName)
    {
        nameOperation = UnwrapConversions(nameOperation);

        if (nameOperation is IFieldReferenceOperation fieldReference)
        {
            return GetFieldPropertyName(fieldReference.Field.Name);
        }

        return ToPascalIdentifier(resourceName);
    }

    private static IOperation UnwrapConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static string GetFieldPropertyName(string fieldName) =>
        ToPascalIdentifier(RemoveResourceNameSuffix(fieldName));

    private static string RemoveResourceNameSuffix(string fieldName)
    {
        const string suffix = "ResourceName";
        if (!fieldName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return fieldName;
        }

        return RemovePresentResourceNameSuffix(fieldName, suffix);
    }

    private static string RemovePresentResourceNameSuffix(string fieldName, string suffix)
    {
        if (fieldName.Length <= suffix.Length)
        {
            return fieldName;
        }

        return fieldName.Substring(0, fieldName.Length - suffix.Length);
    }

    private static string ToPascalIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        var upperNext = true;

        foreach (var character in value)
        {
            AppendIdentifierCharacter(builder, character, ref upperNext);
        }

        return GetPascalIdentifier(builder);
    }

    private static void AppendIdentifierCharacter(StringBuilder builder, char character, ref bool upperNext)
    {
        if (IsIdentifierSeparator(character))
        {
            upperNext = true;
            return;
        }

        AppendIdentifierContent(builder, character, ref upperNext);
    }

    private static bool IsIdentifierSeparator(char character) =>
        !char.IsLetterOrDigit(character) && character != '_';

    private static void AppendIdentifierContent(StringBuilder builder, char character, ref bool upperNext)
    {
        AppendLeadingDigitPrefix(builder, character);

        if (character == '_')
        {
            upperNext = true;
            return;
        }

        builder.Append(GetIdentifierCharacter(character, upperNext));
        upperNext = false;
    }

    private static void AppendLeadingDigitPrefix(StringBuilder builder, char character)
    {
        if (builder.Length != 0)
        {
            return;
        }

        if (char.IsDigit(character))
        {
            builder.Append('_');
        }
    }

    private static char GetIdentifierCharacter(char character, bool upperNext) =>
        upperNext ? char.ToUpperInvariant(character) : character;

    private static string GetPascalIdentifier(StringBuilder builder) =>
        builder.Length == 0 ? "Resource" : EscapeIdentifier(builder.ToString());

    private static string EscapeIdentifier(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None &&
            SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.None
                ? identifier
                : "@" + identifier;
    }

    private static void Generate(SourceProductionContext context, ModuleModel module)
    {
        ReportDiagnostics(context, module.Diagnostics);

        if (!module.CanGenerate)
        {
            return;
        }

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");

        var experimentalDiagnosticIds = module.Resources
            .SelectMany(resource => resource.ExperimentalDiagnosticIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(diagnosticId => diagnosticId, StringComparer.Ordinal)
            .ToArray();
        AppendWarningDisables(source, experimentalDiagnosticIds);

        source.AppendLine();
        AppendNamespace(source, module.Namespace);

        source.Append("[global::System.CodeDom.Compiler.GeneratedCode(\"Shirubasoft.Aspire.Extensions.Multirepo\", ")
            .Append(SymbolDisplay.FormatLiteral(GeneratorVersion, quote: true))
            .AppendLine(")]");
        source.Append(module.Accessibility)
            .Append(" static partial class ")
            .Append(module.TypeName)
            .AppendLine();
        source.AppendLine("{");
        AppendSummary(
            source,
            "    ",
            $"Gets a strongly typed reference to module '{module.ModuleName}' version '{module.ModuleVersion}' from another module definition.");
        AppendParameter(source, "    ", "moduleBuilder", "The module definition that requires this contract.");
        AppendReturns(source, "    ", $"A typed reference to module '{module.ModuleName}'.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentNullException",
            "moduleBuilder is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            $"Module '{module.ModuleName}' is missing, has an incompatible version, or is not materialized yet.");
        source.AppendLine("    public static Module Reference(");
        source.AppendLine("        global::Aspire.Hosting.IDistributedApplicationModuleBuilder moduleBuilder)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(moduleBuilder);");
        source.Append("        return new Module(moduleBuilder.GetRequiredModule(")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .Append(", ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
            .AppendLine("));");
        source.AppendLine("    }");
        source.AppendLine();
        AppendConventionalAddMethods(source, module);

        AppendSummary(
            source,
            "    ",
            $"Adds an exported '{module.ModuleName}' module definition to the AppHost and returns its typed resources.");
        AppendParameter(source, "    ", "builder", "The receiving Aspire application builder.");
        AppendParameter(source, "    ", "module", $"The exported '{module.ModuleName}' module definition.");
        AppendReturns(source, "    ", $"The materialized '{module.ModuleName}' module.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentNullException",
            "builder or module is null.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentException",
            $"module does not identify '{module.ModuleName}' version '{module.ModuleVersion}' with the expected package ID.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "Synchronous resource materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.AddExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationBuilder builder,");
        source.AppendLine("        global::Aspire.Hosting.IDistributedApplicationModule module)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(module);");
        source.Append("        if (!global::System.String.Equals(module.Name, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .AppendLine(", global::System.StringComparison.Ordinal) ||");
        source.Append("            !global::System.String.Equals(module.Version, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
            .AppendLine(", global::System.StringComparison.Ordinal) ||");
        source.Append("            !global::System.String.Equals(module.PackageId, ")
            .Append(FormatPackageId(module.PackageId))
            .AppendLine(", global::System.StringComparison.Ordinal))");
        source.AppendLine("        {");
        source.Append("            throw new global::System.ArgumentException(")
            .Append(SymbolDisplay.FormatLiteral(
                $"Expected module '{module.ModuleName}' with contract version '{module.ModuleVersion}' " +
                $"and package ID '{GetPackageDisplayName(module.PackageId)}'.",
                quote: true))
            .AppendLine(", nameof(module));");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        global::Aspire.Hosting.DistributedApplicationModuleExtensions.AddModule(builder, module);");
        source.AppendLine("        return new Module(module);");
        source.AppendLine("    }");
        source.AppendLine();
        AppendConventionalImportMethods(source, module);

        AppendSummary(
            source,
            "    ",
            $"Imports module '{module.ModuleName}' version '{module.ModuleVersion}' with default resource names.");
        AppendInitializationRemarks(source, "    ", module.ModuleName);
        AppendParameter(source, "    ", "builder", "The receiving Aspire application builder.");
        AppendReturns(source, "    ", $"The imported '{module.ModuleName}' module.");
        AppendException(source, "    ", "global::System.ArgumentNullException", "builder is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "The definition, repository preflight requirements, or synchronous materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationBuilder builder)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.Append("        return ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(builder, new global::Aspire.Hosting.ModuleImportOptions());");
        source.AppendLine("    }");
        source.AppendLine();
        AppendSummary(
            source,
            "    ",
            $"Imports module '{module.ModuleName}' version '{module.ModuleVersion}' with resource naming options.");
        AppendInitializationRemarks(source, "    ", module.ModuleName);
        AppendParameter(source, "    ", "builder", "The receiving Aspire application builder.");
        AppendParameter(source, "    ", "options", "Resource prefixes and aliases for this import.");
        AppendReturns(source, "    ", $"The imported '{module.ModuleName}' module.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentNullException",
            "builder or options is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "The definition, import naming, repository preflight requirements, or synchronous materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationBuilder builder,");
        source.AppendLine("        global::Aspire.Hosting.ModuleImportOptions options)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(options);");
        AppendApplicationBuilderDefinition(source, module);

        source.Append("        var module = global::Aspire.Hosting.DistributedApplicationModuleExtensions.ImportModule(builder, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .AppendLine(", options);");
        source.AppendLine("        return new Module(module);");
        source.AppendLine("    }");
        source.AppendLine();
        AppendSummary(
            source,
            "    ",
            $"A materialized '{module.ModuleName}' module with strongly typed access to every declared resource.");
        source.AppendLine("    public sealed class Module : global::Aspire.Hosting.DistributedApplicationModuleReference");
        source.AppendLine("    {");
        source.AppendLine("        internal Module(global::Aspire.Hosting.IDistributedApplicationModule module)");
        source.AppendLine("            : base(module)");
        source.AppendLine("        {");
        source.AppendLine("        }");

        AppendResourceProperties(source, module);

        source.AppendLine("    }");
        source.AppendLine("}");

        AppendWarningRestores(source, experimentalDiagnosticIds);

        context.AddSource(
            GetHintName(module),
            SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void AppendConventionalAddMethods(StringBuilder source, ModuleModel module)
    {
        if (!module.HasConventionalDefineMethod)
        {
            return;
        }

        AppendApplicationBuilderAddMethod(source, module);
        AppendModuleBuilderAddMethod(source, module);
    }

    private static void AppendApplicationBuilderAddMethod(StringBuilder source, ModuleModel module)
    {
        AppendSummary(
            source,
            "    ",
            $"Defines and adds module '{module.ModuleName}' version '{module.ModuleVersion}' and returns its typed resources.");
        AppendParameter(source, "    ", "builder", "The receiving Aspire application builder.");
        AppendReturns(source, "    ", $"The materialized '{module.ModuleName}' module.");
        AppendException(source, "    ", "global::System.ArgumentNullException", "builder is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "The definition or synchronous resource materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.AddExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationBuilder builder)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(builder);");
        source.Append("        var module = global::Aspire.Hosting.DistributedApplicationModuleExtensions.DefineModule(builder, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .Append(", ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
            .Append(", ")
            .Append(FormatPackageId(module.PackageId))
            .AppendLine(", Define);");
        source.Append("        return ")
            .Append(module.AddExtensionMethodName)
            .AppendLine("(builder, module);");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void AppendModuleBuilderAddMethod(StringBuilder source, ModuleModel module)
    {
        AppendSummary(
            source,
            "    ",
            $"Defines and adds module '{module.ModuleName}' version '{module.ModuleVersion}' as part of another module.");
        AppendParameter(source, "    ", "moduleBuilder", "The module definition that composes this module.");
        AppendReturns(source, "    ", $"The composed '{module.ModuleName}' module.");
        AppendException(source, "    ", "global::System.ArgumentNullException", "moduleBuilder is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "The definition or synchronous resource materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.AddExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationModuleBuilder moduleBuilder)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(moduleBuilder);");
        source.Append("        var module = moduleBuilder.AddModule(")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .Append(", ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
            .Append(", ")
            .Append(FormatPackageId(module.PackageId))
            .AppendLine(", Define);");
        source.AppendLine("        return new Module(module);");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void AppendConventionalImportMethods(StringBuilder source, ModuleModel module)
    {
        if (!module.HasConventionalDefineMethod)
        {
            return;
        }

        AppendModuleBuilderDefaultImportMethod(source, module);
        AppendModuleBuilderConfiguredImportMethod(source, module);
    }

    private static void AppendModuleBuilderDefaultImportMethod(StringBuilder source, ModuleModel module)
    {
        AppendSummary(
            source,
            "    ",
            $"Defines and imports module '{module.ModuleName}' version '{module.ModuleVersion}' as part of another module with default resource names.");
        AppendParameter(source, "    ", "moduleBuilder", "The module definition that composes this module.");
        AppendReturns(source, "    ", $"The composed '{module.ModuleName}' module.");
        AppendException(source, "    ", "global::System.ArgumentNullException", "moduleBuilder is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "The definition, repository preflight requirements, or synchronous resource materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationModuleBuilder moduleBuilder)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(moduleBuilder);");
        source.Append("        return ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(moduleBuilder, new global::Aspire.Hosting.ModuleImportOptions());");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void AppendModuleBuilderConfiguredImportMethod(StringBuilder source, ModuleModel module)
    {
        AppendSummary(
            source,
            "    ",
            $"Defines and imports module '{module.ModuleName}' version '{module.ModuleVersion}' as part of another module with resource naming options.");
        AppendParameter(source, "    ", "moduleBuilder", "The module definition that composes this module.");
        AppendParameter(source, "    ", "options", "Resource prefixes and aliases for this import.");
        AppendReturns(source, "    ", $"The composed '{module.ModuleName}' module.");
        AppendException(
            source,
            "    ",
            "global::System.ArgumentNullException",
            "moduleBuilder or options is null.");
        AppendException(
            source,
            "    ",
            "global::System.InvalidOperationException",
            "The definition, import naming, repository preflight requirements, or synchronous resource materialization is invalid.");
        source.Append("    public static Module ")
            .Append(module.ImportExtensionMethodName)
            .AppendLine("(");
        source.AppendLine("        this global::Aspire.Hosting.IDistributedApplicationModuleBuilder moduleBuilder,");
        source.AppendLine("        global::Aspire.Hosting.ModuleImportOptions options)");
        source.AppendLine("    {");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(moduleBuilder);");
        source.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(options);");
        source.Append("        var module = moduleBuilder.ImportModule(")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .Append(", ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
            .Append(", ")
            .Append(FormatPackageId(module.PackageId))
            .AppendLine(", Define, options);");
        source.AppendLine("        return new Module(module);");
        source.AppendLine("    }");
        source.AppendLine();
    }

    private static void AppendApplicationBuilderDefinition(StringBuilder source, ModuleModel module)
    {
        if (!module.HasConventionalDefineMethod)
        {
            return;
        }

        source.Append("        global::Aspire.Hosting.DistributedApplicationModuleExtensions.DefineModule(builder, ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleName, quote: true))
            .Append(", ")
            .Append(SymbolDisplay.FormatLiteral(module.ModuleVersion, quote: true))
            .Append(", ")
            .Append(FormatPackageId(module.PackageId))
            .AppendLine(", Define);");
    }

    private static void AppendResourceProperties(StringBuilder source, ModuleModel module)
    {
        foreach (var resource in module.Resources)
        {
            AppendResourceProperty(source, module.ModuleName, resource);
        }
    }

    private static void AppendResourceProperty(
        StringBuilder source,
        string moduleName,
        ResourceModel resource)
    {
        source.AppendLine();
        AppendSummary(
            source,
            "        ",
            $"Gets the '{resource.ResourceName}' resource declared by module '{moduleName}'.");
        AppendException(
            source,
            "        ",
            "global::System.InvalidOperationException",
            $"Resource '{resource.ResourceName}' is unavailable or its materialized type is incompatible with the contract.");
        source.Append("        public global::Aspire.Hosting.ApplicationModel.IResourceBuilder<")
            .Append(resource.TypeName)
            .Append("> ")
            .Append(resource.PropertyName)
            .Append(" => GetResource<")
            .Append(resource.TypeName)
            .Append(">(")
            .Append(SymbolDisplay.FormatLiteral(resource.ResourceName, quote: true))
            .AppendLine(");");
    }

    private static void ReportDiagnostics(
        SourceProductionContext context,
        ImmutableArray<DiagnosticInfo> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                diagnostic.Descriptor,
                diagnostic.Location,
                diagnostic.MessageArguments));
        }
    }

    private static void AppendWarningDisables(StringBuilder source, string[] diagnosticIds)
    {
        foreach (var diagnosticId in diagnosticIds)
        {
            source.Append("#pragma warning disable ").AppendLine(diagnosticId);
        }
    }

    private static void AppendNamespace(StringBuilder source, string? @namespace)
    {
        if (@namespace is null)
        {
            return;
        }

        source.Append("namespace ").Append(@namespace).AppendLine(";");
        source.AppendLine();
    }

    private static void AppendWarningRestores(StringBuilder source, string[] diagnosticIds)
    {
        if (diagnosticIds.Length == 0)
        {
            return;
        }

        source.AppendLine();
        AppendWarningRestoreDirectives(source, diagnosticIds);
    }

    private static void AppendWarningRestoreDirectives(
        StringBuilder source,
        string[] diagnosticIds)
    {
        foreach (var diagnosticId in diagnosticIds)
        {
            source.Append("#pragma warning restore ").AppendLine(diagnosticId);
        }
    }

    private static string FormatPackageId(string? packageId) =>
        packageId is null ? "null" : SymbolDisplay.FormatLiteral(packageId, quote: true);

    private static string GetPackageDisplayName(string? packageId) =>
        packageId ?? "none";

    private static void AppendSummary(
        StringBuilder source,
        string indentation,
        string summary) =>
        source.Append(indentation)
            .Append("/// <summary>")
            .Append(EscapeXmlDocumentation(summary))
            .AppendLine("</summary>");

    private static void AppendInitializationRemarks(
        StringBuilder source,
        string indentation,
        string moduleName) =>
        source.Append(indentation)
            .Append("/// <remarks>Repository-backed imports of '")
            .Append(EscapeXmlDocumentation(moduleName))
            .Append("' require aspire do initialize --apphost &lt;path&gt; --non-interactive when normal-run preflight requests initialization.</remarks>")
            .AppendLine();

    private static void AppendParameter(
        StringBuilder source,
        string indentation,
        string name,
        string description) =>
        source.Append(indentation)
            .Append("/// <param name=\"")
            .Append(name)
            .Append("\">")
            .Append(EscapeXmlDocumentation(description))
            .AppendLine("</param>");

    private static void AppendReturns(
        StringBuilder source,
        string indentation,
        string description) =>
        source.Append(indentation)
            .Append("/// <returns>")
            .Append(EscapeXmlDocumentation(description))
            .AppendLine("</returns>");

    private static void AppendException(
        StringBuilder source,
        string indentation,
        string exceptionType,
        string description) =>
        source.Append(indentation)
            .Append("/// <exception cref=\"")
            .Append(exceptionType)
            .Append("\">")
            .Append(EscapeXmlDocumentation(description))
            .AppendLine("</exception>");

    private static string EscapeXmlDocumentation(string value) =>
        value.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;")
            .Replace("\r", "&#xD;")
            .Replace("\n", "&#xA;");

    private static string GetHintName(ModuleModel module)
    {
        var fullName = GetFullTypeName(module);
        var builder = new StringBuilder(fullName.Length + 12);

        foreach (var character in fullName)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.Append(".Module.g.cs").ToString();
    }

    private static string GetFullTypeName(ModuleModel module) =>
        module.Namespace is null
            ? module.TypeName
            : module.Namespace + "." + module.TypeName;

    private sealed class ModuleModel : IEquatable<ModuleModel>
    {
        public ModuleModel(
            string? @namespace,
            string typeName,
            string addExtensionMethodName,
            string importExtensionMethodName,
            string accessibility,
            string moduleName,
            string moduleVersion,
            string? packageId,
            bool hasConventionalDefineMethod,
            ImmutableArray<ResourceModel> resources,
            ImmutableArray<DiagnosticInfo> diagnostics,
            bool canGenerate)
        {
            Namespace = @namespace;
            TypeName = typeName;
            AddExtensionMethodName = addExtensionMethodName;
            ImportExtensionMethodName = importExtensionMethodName;
            Accessibility = accessibility;
            ModuleName = moduleName;
            ModuleVersion = moduleVersion;
            PackageId = packageId;
            HasConventionalDefineMethod = hasConventionalDefineMethod;
            Resources = resources;
            Diagnostics = diagnostics;
            CanGenerate = canGenerate;
        }

        public string? Namespace { get; }

        public string TypeName { get; }

        public string AddExtensionMethodName { get; }

        public string ImportExtensionMethodName { get; }

        public string Accessibility { get; }

        public string ModuleName { get; }

        public string ModuleVersion { get; }

        public string? PackageId { get; }

        public bool HasConventionalDefineMethod { get; }

        public ImmutableArray<ResourceModel> Resources { get; }

        public ImmutableArray<DiagnosticInfo> Diagnostics { get; }

        public bool CanGenerate { get; }

        public bool Equals(ModuleModel? other) =>
            other is not null && HasSameNamespace(other);

        private bool HasSameNamespace(ModuleModel other) =>
            string.Equals(Namespace, other.Namespace, StringComparison.Ordinal) && HasSameTypeName(other);

        private bool HasSameTypeName(ModuleModel other) =>
            string.Equals(TypeName, other.TypeName, StringComparison.Ordinal) && HasSameAddMethodName(other);

        private bool HasSameAddMethodName(ModuleModel other) =>
            string.Equals(AddExtensionMethodName, other.AddExtensionMethodName, StringComparison.Ordinal) &&
            HasSameImportMethodName(other);

        private bool HasSameImportMethodName(ModuleModel other) =>
            string.Equals(ImportExtensionMethodName, other.ImportExtensionMethodName, StringComparison.Ordinal) &&
            HasSameAccessibility(other);

        private bool HasSameAccessibility(ModuleModel other) =>
            string.Equals(Accessibility, other.Accessibility, StringComparison.Ordinal) && HasSameModuleName(other);

        private bool HasSameModuleName(ModuleModel other) =>
            string.Equals(ModuleName, other.ModuleName, StringComparison.Ordinal) && HasSameModuleVersion(other);

        private bool HasSameModuleVersion(ModuleModel other) =>
            string.Equals(ModuleVersion, other.ModuleVersion, StringComparison.Ordinal) && HasSamePackageId(other);

        private bool HasSamePackageId(ModuleModel other) =>
            string.Equals(PackageId, other.PackageId, StringComparison.Ordinal) && HasSameDefineMode(other);

        private bool HasSameDefineMode(ModuleModel other) =>
            HasConventionalDefineMethod == other.HasConventionalDefineMethod && HasSameResources(other);

        private bool HasSameResources(ModuleModel other) =>
            Resources.SequenceEqual(other.Resources) && HasSameDiagnostics(other);

        private bool HasSameDiagnostics(ModuleModel other) =>
            Diagnostics.SequenceEqual(other.Diagnostics) && CanGenerate == other.CanGenerate;

        public override bool Equals(object? obj) => Equals(obj as ModuleModel);

        public override int GetHashCode()
        {
            var hashCode = 17;
            hashCode = CombineHashCode(hashCode, Namespace);
            hashCode = CombineHashCode(hashCode, TypeName);
            hashCode = CombineHashCode(hashCode, AddExtensionMethodName);
            hashCode = CombineHashCode(hashCode, ImportExtensionMethodName);
            hashCode = CombineHashCode(hashCode, Accessibility);
            hashCode = CombineHashCode(hashCode, ModuleName);
            hashCode = CombineHashCode(hashCode, ModuleVersion);
            hashCode = CombineHashCode(hashCode, PackageId);
            hashCode = CombineHashCode(hashCode, HasConventionalDefineMethod);
            hashCode = Resources.Aggregate(
                hashCode,
                static (current, resource) => CombineHashCode(current, resource));
            hashCode = Diagnostics.Aggregate(
                hashCode,
                static (current, diagnostic) => CombineHashCode(current, diagnostic));

            return CombineHashCode(hashCode, CanGenerate);
        }
    }

    private sealed class ResourceModel : IEquatable<ResourceModel>
    {
        public ResourceModel(
            string resourceName,
            string propertyName,
            string typeName,
            ImmutableArray<string> experimentalDiagnosticIds,
            string filePath,
            int spanStart,
            Location location)
        {
            ResourceName = resourceName;
            PropertyName = propertyName;
            TypeName = typeName;
            ExperimentalDiagnosticIds = experimentalDiagnosticIds;
            FilePath = filePath;
            SpanStart = spanStart;
            Location = location;
        }

        public string ResourceName { get; }

        public string PropertyName { get; }

        public string TypeName { get; }

        public ImmutableArray<string> ExperimentalDiagnosticIds { get; }

        public string FilePath { get; }

        public int SpanStart { get; }

        public Location Location { get; }

        public bool Equals(ResourceModel? other) =>
            other is not null && HasSameResourceName(other);

        private bool HasSameResourceName(ResourceModel other) =>
            string.Equals(ResourceName, other.ResourceName, StringComparison.Ordinal) && HasSamePropertyName(other);

        private bool HasSamePropertyName(ResourceModel other) =>
            string.Equals(PropertyName, other.PropertyName, StringComparison.Ordinal) && HasSameTypeName(other);

        private bool HasSameTypeName(ResourceModel other) =>
            string.Equals(TypeName, other.TypeName, StringComparison.Ordinal) && HasSameDiagnosticIds(other);

        private bool HasSameDiagnosticIds(ResourceModel other) =>
            ExperimentalDiagnosticIds.SequenceEqual(other.ExperimentalDiagnosticIds, StringComparer.Ordinal) &&
            HasSameFilePath(other);

        private bool HasSameFilePath(ResourceModel other) =>
            string.Equals(FilePath, other.FilePath, StringComparison.Ordinal) && HasSameSpanStart(other);

        private bool HasSameSpanStart(ResourceModel other) =>
            SpanStart == other.SpanStart && LocationsEqual(Location, other.Location);

        public override bool Equals(object? obj) => Equals(obj as ResourceModel);

        public override int GetHashCode()
        {
            var hashCode = 17;
            hashCode = CombineHashCode(hashCode, ResourceName);
            hashCode = CombineHashCode(hashCode, PropertyName);
            hashCode = CombineHashCode(hashCode, TypeName);
            hashCode = ExperimentalDiagnosticIds.Aggregate(
                hashCode,
                static (current, diagnosticId) => CombineHashCode(current, diagnosticId));

            hashCode = CombineHashCode(hashCode, FilePath);
            hashCode = CombineHashCode(hashCode, SpanStart);
            return CombineHashCode(hashCode, GetLocationHashCode(Location));
        }
    }

    private sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
    {
        public DiagnosticInfo(DiagnosticDescriptor descriptor, Location location, params object[] messageArguments)
        {
            Descriptor = descriptor;
            Location = location;
            MessageArguments = messageArguments;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Location Location { get; }

        public object[] MessageArguments { get; }

        public bool Equals(DiagnosticInfo? other) =>
            other is not null && HasSameDescriptor(other);

        private bool HasSameDescriptor(DiagnosticInfo other) =>
            ReferenceEquals(Descriptor, other.Descriptor) && HasSameLocation(other);

        private bool HasSameLocation(DiagnosticInfo other) =>
            LocationsEqual(Location, other.Location) && HasSameMessageArguments(other);

        private bool HasSameMessageArguments(DiagnosticInfo other) =>
            MessageArguments.SequenceEqual(other.MessageArguments);

        public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

        public override int GetHashCode()
        {
            var hashCode = CombineHashCode(17, Descriptor.Id);
            hashCode = CombineHashCode(hashCode, GetLocationHashCode(Location));
            hashCode = MessageArguments.Aggregate(
                hashCode,
                static (current, messageArgument) => CombineHashCode(current, messageArgument));

            return hashCode;
        }
    }

    private static bool LocationsEqual(Location left, Location right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return HaveSameLocationKind(left, right);
    }

    private static bool HaveSameLocationKind(Location left, Location right) =>
        left.Kind == right.Kind && HaveSameSourceSpan(left, right);

    private static bool HaveSameSourceSpan(Location left, Location right) =>
        left.SourceSpan.Equals(right.SourceSpan) && HaveSameSourcePath(left, right);

    private static bool HaveSameSourcePath(Location left, Location right) =>
        string.Equals(
            left.GetLineSpan().Path,
            right.GetLineSpan().Path,
            StringComparison.Ordinal) && HaveSameMappedLineSpan(left, right);

    private static bool HaveSameMappedLineSpan(Location left, Location right) =>
        left.GetMappedLineSpan().Equals(right.GetMappedLineSpan());

    private static int GetLocationHashCode(Location location)
    {
        var hashCode = CombineHashCode(17, location.Kind);
        hashCode = CombineHashCode(hashCode, location.SourceSpan);
        hashCode = CombineHashCode(hashCode, location.GetLineSpan().Path);
        return CombineHashCode(hashCode, location.GetMappedLineSpan());
    }

    private static int CombineHashCode(int hashCode, object? value) =>
        unchecked((hashCode * 31) + EqualityComparer<object?>.Default.GetHashCode(value));
}
