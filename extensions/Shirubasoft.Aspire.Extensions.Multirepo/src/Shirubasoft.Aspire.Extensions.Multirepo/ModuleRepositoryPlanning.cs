using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable CA1308 // Remote identities and filesystem lookup slugs use lowercase normalized forms.

namespace Aspire.Hosting;

internal sealed class ModuleRepositoryRequirement
{
    private readonly HashSet<string> _moduleNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkoutDirectoryNameConfigurationKeys = new(StringComparer.Ordinal);

    internal ModuleRepositoryRequirement(
        string moduleName,
        string repository,
        string normalizedRepository,
        string repositoryPath,
        string? revision,
        bool updateOnInitialize,
        string stepKey,
        bool requiredOnRun,
        bool usesCanonicalCheckout,
        bool usesDefaultCanonicalDirectoryName,
        string checkoutDirectoryNameConfigurationKey)
    {
        AddConsumer(moduleName, checkoutDirectoryNameConfigurationKey);
        Repository = repository;
        NormalizedRepository = normalizedRepository;
        RepositoryPath = Path.GetFullPath(repositoryPath);
        Revision = NormalizeRevision(revision);
        UpdateOnInitialize = Revision is null && updateOnInitialize;
        StepKey = stepKey;
        RequiredOnRun = requiredOnRun;
        UsesCanonicalCheckout = usesCanonicalCheckout;
        UsesDefaultCanonicalDirectoryName = usesDefaultCanonicalDirectoryName;
        ConfigurationFingerprint = CreateConfigurationFingerprint(
            NormalizedRepository,
            RepositoryPath,
            Revision,
            UpdateOnInitialize);
    }

    public IReadOnlyCollection<string> ModuleNames => _moduleNames;

    public IReadOnlyCollection<string> CheckoutDirectoryNameConfigurationKeys =>
        _checkoutDirectoryNameConfigurationKeys;

    public string Repository { get; }

    public string NormalizedRepository { get; }

    public string RepositoryPath { get; }

    public string? Revision { get; }

    public bool UpdateOnInitialize { get; }

    public string StepKey { get; }

    public string ConfigurationFingerprint { get; }

    public bool RequiredOnRun { get; private set; }

    public bool UsesCanonicalCheckout { get; }

    public bool UsesDefaultCanonicalDirectoryName { get; }

    internal void AddModule(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        _moduleNames.Add(moduleName.Trim());
    }

    internal void AddConsumer(string moduleName, string checkoutDirectoryNameConfigurationKey)
    {
        AddModule(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutDirectoryNameConfigurationKey);
        _checkoutDirectoryNameConfigurationKeys.Add(checkoutDirectoryNameConfigurationKey.Trim());
    }

    internal void RequireOnRun() => RequiredOnRun = true;

    internal void EnsureCompatible(
        string normalizedRepository,
        string? revision,
        bool updateOnInitialize)
    {
        var normalizedRevision = NormalizeRevision(revision);
        var normalizedUpdateOnInitialize = normalizedRevision is null && updateOnInitialize;
        ValidateCompatibleRepository(normalizedRepository);
        ValidateCompatibleRevision(normalizedRevision);
        ValidateCompatibleUpdatePolicy(normalizedUpdateOnInitialize);
    }

    private void ValidateCompatibleRepository(string normalizedRepository)
    {
        if (!string.Equals(NormalizedRepository, normalizedRepository, StringComparison.Ordinal))
        {
            throw CreateIncompatibleRepositoryException();
        }
    }

    private void ValidateCompatibleRevision(string? normalizedRevision)
    {
        if (!string.Equals(Revision, normalizedRevision, StringComparison.Ordinal))
        {
            throw CreateIncompatibleRepositoryException();
        }
    }

    private void ValidateCompatibleUpdatePolicy(bool normalizedUpdateOnInitialize)
    {
        if (UpdateOnInitialize != normalizedUpdateOnInitialize)
        {
            throw CreateIncompatibleRepositoryException();
        }
    }

    private InvalidOperationException CreateIncompatibleRepositoryException() =>
        new(
            $"Modules sharing repository checkout '{RepositoryPath}' configure conflicting repositories, " +
            "revisions, or initialization update policies. Each checkout must have one initialization policy.");

    private static string? NormalizeRevision(string? revision) =>
        string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();

    private static string CreateConfigurationFingerprint(
        string normalizedRepository,
        string repositoryPath,
        string? revision,
        bool updateOnInitialize)
    {
        var configuration = string.Join(
            '\n',
            "1",
            normalizedRepository,
            Path.GetFullPath(repositoryPath),
            revision ?? string.Empty,
            updateOnInitialize ? "update" : "preserve");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)))
            .ToLowerInvariant();
    }
}

internal readonly record struct ModuleRepositoryPlanRegistration(
    ModuleRepositoryRequirement Requirement,
    bool IsNew);

internal sealed class ModuleRepositoryPlanRegistry
{
    private readonly Dictionary<string, ModuleRepositoryRequirement> _requirements =
        new(PathSafety.Comparer);
    private readonly Dictionary<string, ModuleRepositoryRequirement> _requirementsByIdentity =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleRepositoryRequirement> _requirementsByCanonicalSlug =
        new(StringComparer.Ordinal);

    public ModuleRepositoryPlanRegistry(string appHostDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        AppHostDirectory = Path.GetFullPath(appHostDirectory);
        AppHostRepositoryRoot = RepositoryIdentity.FindGitRoot(AppHostDirectory);
        SiblingParent = Path.GetDirectoryName(AppHostRepositoryRoot)
            ?? throw new InvalidOperationException(
                $"Unable to determine the parent of AppHost repository '{AppHostRepositoryRoot}'.");
    }

    public string AppHostDirectory { get; }

    public string AppHostRepositoryRoot { get; }

    public string SiblingParent { get; }

    public IReadOnlyCollection<ModuleRepositoryRequirement> Requirements =>
        _requirements.Values.ToArray();

    public ModuleRepositoryPlanRegistration Register(
        string moduleName,
        string repository,
        string? revision,
        bool updateOnInitialize,
        bool requiredOnRun = true,
        string? checkoutDirectoryName = null,
        string? checkoutDirectoryNameConfigurationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var configurationKey = GetConfigurationKey(
            moduleName,
            checkoutDirectoryNameConfigurationKey);
        var normalizedRevision = NormalizeRevision(revision);
        var equivalentRegistration = TryRegisterEquivalentDefaultCheckout(
            moduleName,
            repository,
            revision,
            normalizedRevision,
            updateOnInitialize,
            requiredOnRun,
            checkoutDirectoryName,
            configurationKey);
        if (equivalentRegistration is not null)
        {
            return equivalentRegistration.Value;
        }

        var repositoryPath = RepositoryIdentity.GetSiblingPath(
            SiblingParent,
            repository,
            revision,
            AppHostDirectory,
            checkoutDirectoryName,
            configurationKey);
        return Register(
            moduleName,
            repository,
            repositoryPath,
            revision,
            updateOnInitialize,
            requiredOnRun,
            checkoutDirectoryName,
            configurationKey);
    }

    private ModuleRepositoryPlanRegistration? TryRegisterEquivalentDefaultCheckout(
        string moduleName,
        string repository,
        string? revision,
        string? normalizedRevision,
        bool updateOnInitialize,
        bool requiredOnRun,
        string? checkoutDirectoryName,
        string configurationKey)
    {
        if (!UsesDefaultCanonicalDirectoryName(repository, normalizedRevision, checkoutDirectoryName))
        {
            return null;
        }

        return TryRegisterEquivalentIdentity(
            moduleName,
            repository,
            revision,
            normalizedRevision,
            updateOnInitialize,
            requiredOnRun,
            checkoutDirectoryName,
            configurationKey);
    }

    private bool UsesDefaultCanonicalDirectoryName(
        string repository,
        string? normalizedRevision,
        string? checkoutDirectoryName)
    {
        if (normalizedRevision is not null || checkoutDirectoryName is not null)
        {
            return false;
        }

        return RepositoryIdentity.IsRemoteRepository(repository, AppHostDirectory);
    }

    private ModuleRepositoryPlanRegistration? TryRegisterEquivalentIdentity(
        string moduleName,
        string repository,
        string? revision,
        string? normalizedRevision,
        bool updateOnInitialize,
        bool requiredOnRun,
        string? checkoutDirectoryName,
        string configurationKey)
    {
        var normalizedRepository = RepositoryIdentity.NormalizeRepositoryIdentity(repository, AppHostDirectory);
        var identityKey = CreateIdentityKey(normalizedRepository, normalizedRevision);
        if (!_requirementsByIdentity.TryGetValue(identityKey, out var equivalent))
        {
            return null;
        }

        return RegisterEquivalentDefaultCheckout(
            moduleName,
            repository,
            revision,
            updateOnInitialize,
            requiredOnRun,
            checkoutDirectoryName,
            configurationKey,
            equivalent);
    }

    private ModuleRepositoryPlanRegistration? RegisterEquivalentDefaultCheckout(
        string moduleName,
        string repository,
        string? revision,
        bool updateOnInitialize,
        bool requiredOnRun,
        string? checkoutDirectoryName,
        string configurationKey,
        ModuleRepositoryRequirement equivalent)
    {
        if (!equivalent.UsesDefaultCanonicalDirectoryName)
        {
            return null;
        }

        return Register(
            moduleName,
            repository,
            equivalent.RepositoryPath,
            revision,
            updateOnInitialize,
            requiredOnRun,
            checkoutDirectoryName,
            configurationKey);
    }

    public ModuleRepositoryPlanRegistration Register(
        string moduleName,
        string repository,
        string repositoryPath,
        string? revision,
        bool updateOnInitialize,
        bool requiredOnRun = true,
        string? checkoutDirectoryName = null,
        string? checkoutDirectoryNameConfigurationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var configurationKey = GetConfigurationKey(
            moduleName,
            checkoutDirectoryNameConfigurationKey);
        var normalizedRevision = NormalizeRevision(revision);
        var usesCanonicalCheckout = UsesCanonicalCheckout(repository, normalizedRevision);
        var usesDefaultCanonicalDirectoryName =
            usesCanonicalCheckout && checkoutDirectoryName is null;
        ValidateCheckoutDirectoryName(
            checkoutDirectoryName,
            configurationKey,
            normalizedRevision);

        var fullRepositoryPath = Path.GetFullPath(repositoryPath);
        RepositoryIdentity.EnsureDirectSibling(
            AppHostRepositoryRoot,
            SiblingParent,
            fullRepositoryPath);

        var normalizedRepository = RepositoryIdentity.NormalizeRepositoryIdentity(
            repository,
            AppHostDirectory);
        var identityKey = CreateIdentityKey(normalizedRepository, normalizedRevision);
        var existingRegistration = TryRegisterExistingRequirement(
            moduleName,
            normalizedRepository,
            normalizedRevision,
            revision,
            updateOnInitialize,
            requiredOnRun,
            fullRepositoryPath,
            configurationKey,
            identityKey);
        if (existingRegistration is not null)
        {
            return existingRegistration.Value;
        }

        var canonicalSlug = GetCanonicalSlug(
            repository,
            normalizedRepository,
            fullRepositoryPath,
            usesDefaultCanonicalDirectoryName,
            configurationKey);
        return AddRequirement(
            moduleName,
            repository,
            normalizedRepository,
            fullRepositoryPath,
            revision,
            updateOnInitialize,
            requiredOnRun,
            usesCanonicalCheckout,
            usesDefaultCanonicalDirectoryName,
            configurationKey,
            identityKey,
            canonicalSlug);
    }

    private bool UsesCanonicalCheckout(string repository, string? normalizedRevision)
    {
        if (normalizedRevision is not null)
        {
            return false;
        }

        return RepositoryIdentity.IsRemoteRepository(repository, AppHostDirectory);
    }

    private void ValidateCheckoutDirectoryName(
        string? checkoutDirectoryName,
        string configurationKey,
        string? normalizedRevision)
    {
        if (checkoutDirectoryName is null)
        {
            return;
        }

        _ = RepositoryIdentity.ValidateCheckoutDirectoryName(
            checkoutDirectoryName,
            configurationKey,
            normalizedRevision,
            SiblingParent);
    }

    private ModuleRepositoryPlanRegistration? TryRegisterExistingRequirement(
        string moduleName,
        string normalizedRepository,
        string? normalizedRevision,
        string? revision,
        bool updateOnInitialize,
        bool requiredOnRun,
        string fullRepositoryPath,
        string configurationKey,
        string identityKey)
    {
        var identityRegistration = TryRegisterExistingIdentity(
            moduleName,
            normalizedRepository,
            normalizedRevision,
            updateOnInitialize,
            requiredOnRun,
            fullRepositoryPath,
            configurationKey,
            identityKey);
        if (identityRegistration is not null)
        {
            return identityRegistration;
        }

        return TryRegisterExistingPath(
            moduleName,
            normalizedRepository,
            revision,
            updateOnInitialize,
            requiredOnRun,
            fullRepositoryPath,
            configurationKey);
    }

    private ModuleRepositoryPlanRegistration? TryRegisterExistingIdentity(
        string moduleName,
        string normalizedRepository,
        string? normalizedRevision,
        bool updateOnInitialize,
        bool requiredOnRun,
        string fullRepositoryPath,
        string configurationKey,
        string identityKey)
    {
        if (!_requirementsByIdentity.TryGetValue(identityKey, out var equivalent))
        {
            return null;
        }

        ValidateEquivalentIdentityPath(
            equivalent,
            normalizedRepository,
            fullRepositoryPath,
            configurationKey);
        equivalent.EnsureCompatible(normalizedRepository, normalizedRevision, updateOnInitialize);
        RegisterConsumer(equivalent, moduleName, configurationKey, requiredOnRun);
        return new ModuleRepositoryPlanRegistration(equivalent, IsNew: false);
    }

    private static void ValidateEquivalentIdentityPath(
        ModuleRepositoryRequirement equivalent,
        string normalizedRepository,
        string fullRepositoryPath,
        string configurationKey)
    {
        if (!PathSafety.AreEqual(equivalent.RepositoryPath, fullRepositoryPath))
        {
            throw CreateEquivalentIdentityPathException(
                equivalent,
                normalizedRepository,
                fullRepositoryPath,
                configurationKey);
        }
    }

    private ModuleRepositoryPlanRegistration? TryRegisterExistingPath(
        string moduleName,
        string normalizedRepository,
        string? revision,
        bool updateOnInitialize,
        bool requiredOnRun,
        string fullRepositoryPath,
        string configurationKey)
    {
        if (!_requirements.TryGetValue(fullRepositoryPath, out var existing))
        {
            return null;
        }

        if (!string.Equals(
            existing.NormalizedRepository,
            normalizedRepository,
            StringComparison.Ordinal))
        {
            throw CreateCanonicalPathCollisionException(
                existing,
                normalizedRepository,
                fullRepositoryPath,
                configurationKey);
        }

        existing.EnsureCompatible(normalizedRepository, revision, updateOnInitialize);
        RegisterConsumer(existing, moduleName, configurationKey, requiredOnRun);
        return new ModuleRepositoryPlanRegistration(existing, IsNew: false);
    }

    private static void RegisterConsumer(
        ModuleRepositoryRequirement requirement,
        string moduleName,
        string configurationKey,
        bool requiredOnRun)
    {
        requirement.AddConsumer(moduleName, configurationKey);
        if (requiredOnRun)
        {
            requirement.RequireOnRun();
        }
    }

    private string? GetCanonicalSlug(
        string repository,
        string normalizedRepository,
        string fullRepositoryPath,
        bool usesDefaultCanonicalDirectoryName,
        string configurationKey)
    {
        if (!usesDefaultCanonicalDirectoryName)
        {
            return null;
        }

        var canonicalSlug = RepositoryIdentity.GetCanonicalCheckoutSlug(repository, AppHostDirectory);
        ValidateCanonicalSlug(
            normalizedRepository,
            fullRepositoryPath,
            canonicalSlug,
            configurationKey);
        return canonicalSlug;
    }

    private void ValidateCanonicalSlug(
        string normalizedRepository,
        string fullRepositoryPath,
        string canonicalSlug,
        string configurationKey)
    {
        if (_requirementsByCanonicalSlug.TryGetValue(canonicalSlug, out var slugCollision))
        {
            throw CreateCanonicalSlugCollisionException(
                slugCollision,
                normalizedRepository,
                fullRepositoryPath,
                canonicalSlug,
                configurationKey);
        }
    }

    private ModuleRepositoryPlanRegistration AddRequirement(
        string moduleName,
        string repository,
        string normalizedRepository,
        string fullRepositoryPath,
        string? revision,
        bool updateOnInitialize,
        bool requiredOnRun,
        bool usesCanonicalCheckout,
        bool usesDefaultCanonicalDirectoryName,
        string configurationKey,
        string identityKey,
        string? canonicalSlug)
    {
        var stepKey = RepositoryIdentity.GetStepKey(
            normalizedRepository,
            revision,
            Path.GetFileName(fullRepositoryPath));
        var requirement = new ModuleRepositoryRequirement(
            moduleName,
            repository.Trim(),
            normalizedRepository,
            fullRepositoryPath,
            revision,
            updateOnInitialize,
            stepKey,
            requiredOnRun,
            usesCanonicalCheckout,
            usesDefaultCanonicalDirectoryName,
            configurationKey);
        _requirements.Add(fullRepositoryPath, requirement);
        _requirementsByIdentity.Add(identityKey, requirement);
        if (canonicalSlug is not null)
        {
            _requirementsByCanonicalSlug.Add(canonicalSlug, requirement);
        }

        return new ModuleRepositoryPlanRegistration(requirement, IsNew: true);
    }

    private static string? NormalizeRevision(string? revision) =>
        string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();

    private static string GetConfigurationKey(string moduleName, string? configurationKey) =>
        string.IsNullOrWhiteSpace(configurationKey)
            ? $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:{moduleName}:CheckoutDirectoryName"
            : configurationKey.Trim();

    private static string CreateIdentityKey(string normalizedRepository, string? revision) =>
        $"{normalizedRepository}\n{revision ?? string.Empty}";

    private static InvalidOperationException CreateCanonicalPathCollisionException(
        ModuleRepositoryRequirement existing,
        string normalizedRepository,
        string repositoryPath,
        string configurationKey)
    {
        var existingKeys = FormatConfigurationKeys(existing.CheckoutDirectoryNameConfigurationKeys);
        return new InvalidOperationException(
            $"Repository identities '{existing.NormalizedRepository}' ({existingKeys}) and " +
            $"'{normalizedRepository}' (configuration key '{configurationKey}') resolve to the same canonical " +
            $"checkout path '{repositoryPath}'. Configure an explicit distinct CheckoutDirectoryName using " +
            $"{existingKeys} or configuration key '{configurationKey}'.");
    }

    private static InvalidOperationException CreateEquivalentIdentityPathException(
        ModuleRepositoryRequirement existing,
        string normalizedRepository,
        string repositoryPath,
        string configurationKey)
    {
        var existingKeys = FormatConfigurationKeys(existing.CheckoutDirectoryNameConfigurationKeys);
        return new InvalidOperationException(
            $"Equivalent repository identity '{normalizedRepository}' resolves to both " +
            $"'{existing.RepositoryPath}' ({existingKeys}) and '{repositoryPath}' " +
            $"(configuration key '{configurationKey}'). Equivalent repositories must share one repository plan; " +
            "configure the same CheckoutDirectoryName for every use.");
    }

    private static InvalidOperationException CreateCanonicalSlugCollisionException(
        ModuleRepositoryRequirement existing,
        string normalizedRepository,
        string repositoryPath,
        string canonicalSlug,
        string configurationKey)
    {
        var existingKeys = FormatConfigurationKeys(existing.CheckoutDirectoryNameConfigurationKeys);
        return new InvalidOperationException(
            $"Repository identities '{existing.NormalizedRepository}' ({existingKeys}) and " +
            $"'{normalizedRepository}' (configuration key '{configurationKey}') use directory names " +
            $"'{Path.GetFileName(existing.RepositoryPath)}' and '{Path.GetFileName(repositoryPath)}' that " +
            $"resolve to the same canonical checkout slug '{canonicalSlug}'. Configure an explicit distinct " +
            $"CheckoutDirectoryName using {existingKeys} or configuration key '{configurationKey}'.");
    }

    private static string FormatConfigurationKeys(IEnumerable<string> configurationKeys) =>
        string.Join(
            ", ",
            configurationKeys
                .Order(StringComparer.Ordinal)
                .Select(key => $"configuration key '{key}'"));
}

internal static class RepositoryIdentity
{
    private const int HashLength = 10;
    private const int MaximumDirectoryNameLength = 72;
    private static readonly HashSet<string> ReservedCheckoutDirectoryNames = new(
        ["CON", "PRN", "AUX", "NUL"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NumberedDeviceNames = new(
        [
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<char> InvalidCheckoutFileNameCharacters =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '|', '?', '*'];
    private static readonly Dictionary<string, int> DefaultRepositoryPorts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        [Uri.UriSchemeHttp] = 80,
        [Uri.UriSchemeHttps] = 443,
        ["ssh"] = 22,
        ["git"] = 9418
    };
    private static readonly (Func<string, bool> IsInvalid, string Reason)[] CheckoutDirectoryNameRules =
    [
        (string.IsNullOrWhiteSpace, "it must contain exactly one non-empty filename segment"),
        (HasSurroundingWhitespace, "leading or trailing whitespace is not allowed"),
        (IsTooLong, "filename segments longer than 255 characters are not allowed"),
        (IsTraversalSegment, "'.' and '..' are traversal segments"),
        (IsRootedCheckoutPath, "rooted paths are not allowed"),
        (HasDirectorySeparator, "directory separators and multi-segment paths are not allowed"),
        (HasInvalidFileNameCharacter, "invalid filename characters are not allowed"),
        (EndsWithPeriod, "filename segments ending in a period are not portable"),
        (IsReservedCheckoutDirectoryName, "reserved filename segments are not allowed")
    ];

    public static string FindRepositoryRoot(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return TryFindRepositoryRoot(projectPath) ?? GetWorkingDirectory(projectPath);
    }

    public static string? TryFindRepositoryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var current = new DirectoryInfo(GetWorkingDirectory(path));
        while (current is not null)
        {
            if (HasGitMetadata(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    public static string FindGitRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return TryFindRepositoryRoot(path) ?? throw new InvalidOperationException(
            $"AppHost directory '{path}' is not inside a Git repository. " +
            "Repository initialization requires an AppHost Git root.");
    }

    public static bool HasGitMetadata(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var gitMetadata = Path.Combine(Path.GetFullPath(repositoryPath), ".git");
        return Directory.Exists(gitMetadata) || File.Exists(gitMetadata);
    }

    public static string GetSiblingPath(
        string siblingParent,
        string repository,
        string? revision,
        string baseDirectory,
        string? checkoutDirectoryName = null,
        string? checkoutDirectoryNameConfigurationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siblingParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var normalizedRepository = NormalizeRepositoryIdentity(repository, baseDirectory);
        var normalizedRevision = NormalizeRevision(revision);
        var configurationKey = ResolveCheckoutDirectoryConfigurationKey(
            checkoutDirectoryNameConfigurationKey);
        checkoutDirectoryName = ValidateOptionalCheckoutDirectoryName(
            checkoutDirectoryName,
            configurationKey,
            normalizedRevision,
            siblingParent);

        if (normalizedRevision is null && IsRemoteRepository(repository, baseDirectory))
        {
            return GetCanonicalSiblingPath(
                siblingParent,
                repository,
                normalizedRepository,
                baseDirectory,
                checkoutDirectoryName,
                configurationKey);
        }

        return GetHashedSiblingPath(siblingParent, normalizedRepository, normalizedRevision);
    }

    private static string? NormalizeRevision(string? revision) =>
        string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();

    private static string ResolveCheckoutDirectoryConfigurationKey(string? configurationKey) =>
        string.IsNullOrWhiteSpace(configurationKey)
            ? $"{ModularAppHostsOptions.ConfigurationSectionName}:Modules:<module>:CheckoutDirectoryName"
            : configurationKey.Trim();

    private static string? ValidateOptionalCheckoutDirectoryName(
        string? checkoutDirectoryName,
        string configurationKey,
        string? normalizedRevision,
        string siblingParent)
    {
        if (checkoutDirectoryName is null)
        {
            return null;
        }

        return ValidateCheckoutDirectoryName(
            checkoutDirectoryName,
            configurationKey,
            normalizedRevision,
            siblingParent);
    }

    private static string GetCanonicalSiblingPath(
        string siblingParent,
        string repository,
        string normalizedRepository,
        string baseDirectory,
        string? checkoutDirectoryName,
        string configurationKey)
    {
        var siblingParentPath = Path.GetFullPath(siblingParent);
        if (checkoutDirectoryName is not null)
        {
            return Path.Combine(siblingParentPath, checkoutDirectoryName);
        }

        return ResolveCanonicalSiblingPath(
            siblingParentPath,
            repository,
            normalizedRepository,
            baseDirectory,
            configurationKey);
    }

    private static string ResolveCanonicalSiblingPath(
        string siblingParentPath,
        string repository,
        string normalizedRepository,
        string baseDirectory,
        string configurationKey)
    {
        var canonicalDirectoryName = GetValidatedRemoteRepositoryName(
            repository,
            normalizedRepository,
            configurationKey);
        var canonicalPath = Path.Combine(siblingParentPath, canonicalDirectoryName);
        if (!Directory.Exists(siblingParentPath))
        {
            return canonicalPath;
        }

        var canonicalSlug = CreateSlug(canonicalDirectoryName, MaximumDirectoryNameLength);
        var matchingSiblings = FindMatchingSiblingPaths(
            siblingParentPath,
            baseDirectory,
            canonicalSlug);
        return SelectCanonicalSiblingPath(
            matchingSiblings,
            canonicalPath,
            canonicalSlug,
            normalizedRepository,
            configurationKey);
    }

    private static string GetValidatedRemoteRepositoryName(
        string repository,
        string normalizedRepository,
        string configurationKey)
    {
        var canonicalDirectoryName = GetRemoteRepositoryName(repository);
        var invalidDirectoryNameReason = GetInvalidCheckoutDirectoryNameReason(
            canonicalDirectoryName,
            revision: null);
        if (invalidDirectoryNameReason is not null)
        {
            throw new InvalidOperationException(
                $"Remote repository '{normalizedRepository}' has repository name " +
                $"'{FormatDiagnosticValue(canonicalDirectoryName)}', which cannot be used as a checkout " +
                $"directory name: {invalidDirectoryNameReason}. Configure CheckoutDirectoryName at " +
                $"configuration key '{configurationKey}'.");
        }

        return canonicalDirectoryName;
    }

    private static string[] FindMatchingSiblingPaths(
        string siblingParentPath,
        string baseDirectory,
        string canonicalSlug)
    {
        var appHostRepositoryRoot = TryFindRepositoryRoot(baseDirectory);
        return Directory
            .EnumerateDirectories(siblingParentPath)
            .Where(path => !PathSafety.AreEqual(path, appHostRepositoryRoot))
            .Select(path => new
            {
                Path = path,
                Name = Path.GetFileName(path)
            })
            .Where(candidate => string.Equals(
                CreateSlug(candidate.Name, MaximumDirectoryNameLength),
                canonicalSlug,
                StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.Path)
            .ToArray();
    }

    private static string SelectCanonicalSiblingPath(
        string[] matchingSiblings,
        string canonicalPath,
        string canonicalSlug,
        string normalizedRepository,
        string configurationKey)
    {
        if (matchingSiblings.Length > 1)
        {
            var matches = string.Join(", ", matchingSiblings.Select(path => $"'{path}'"));
            throw new InvalidOperationException(
                $"Multiple sibling directories {matches} normalize to the same canonical checkout slug " +
                $"'{canonicalSlug}' for repository '{normalizedRepository}'. Configure CheckoutDirectoryName " +
                $"at configuration key '{configurationKey}' to select the intended checkout explicitly.");
        }

        return matchingSiblings.FirstOrDefault() ?? canonicalPath;
    }

    private static string GetHashedSiblingPath(
        string siblingParent,
        string normalizedRepository,
        string? normalizedRevision)
    {
        var repositorySlug = CreateSlug(GetRepositoryName(normalizedRepository), 30);
        var repositoryHash = GetStableHash(normalizedRepository);
        var directoryName = $"{repositorySlug}-{repositoryHash}";
        if (normalizedRevision is not null)
        {
            var revisionSlug = CreateSlug(normalizedRevision, 18);
            var revisionHash = GetStableHash(normalizedRevision);
            directoryName = $"{directoryName}-rev-{revisionSlug}-{revisionHash}";
        }

        if (directoryName.Length > MaximumDirectoryNameLength)
        {
            var stableSuffix = GetStableHash($"{normalizedRepository}\n{normalizedRevision}");
            directoryName =
                $"{directoryName[..(MaximumDirectoryNameLength - stableSuffix.Length - 1)].TrimEnd('-')}-{stableSuffix}";
        }

        return Path.Combine(Path.GetFullPath(siblingParent), directoryName);
    }

    public static string ValidateCheckoutDirectoryName(
        string value,
        string configurationKey,
        string? revision,
        string siblingParent)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(siblingParent);
        var reason = GetInvalidCheckoutDirectoryNameReason(value, revision);
        if (reason is not null)
        {
            throw new InvalidOperationException(
                $"Checkout directory name '{FormatDiagnosticValue(value)}' from configuration key " +
                $"'{configurationKey}' is invalid: {reason}.");
        }

        var siblingParentPath = Path.GetFullPath(siblingParent);
        var destination = Path.GetFullPath(Path.Combine(siblingParentPath, value));
        if (!PathSafety.AreEqual(Path.GetDirectoryName(destination), siblingParentPath))
        {
            throw new InvalidOperationException(
                $"Checkout directory name '{FormatDiagnosticValue(value)}' from configuration key " +
                $"'{configurationKey}' is invalid: it resolves outside sibling parent '{siblingParentPath}'.");
        }

        return value;
    }

    public static string GetCanonicalCheckoutSlug(string repository, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (!IsRemoteRepository(repository, baseDirectory))
        {
            throw new InvalidOperationException(
                $"Repository '{repository}' is not a recognizable remote repository identity.");
        }

        return CreateSlug(GetRemoteRepositoryName(repository), MaximumDirectoryNameLength);
    }

    public static string NormalizeRepositoryIdentity(string repository, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var value = repository.Trim().TrimEnd('/', '\\');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return NormalizeUriIdentity(uri, baseDirectory);
        }

        if (IsLocalRepositoryIdentity(value, baseDirectory))
        {
            return NormalizeLocalIdentity(value, baseDirectory);
        }

        return NormalizeShorthandRemoteIdentity(value, repository);
    }

    private static string NormalizeUriIdentity(Uri uri, string baseDirectory)
    {
        if (uri.IsFile)
        {
            return NormalizeLocalIdentity(uri.LocalPath, baseDirectory);
        }

        var host = uri.IdnHost.ToLowerInvariant();
        var port = GetNormalizedPort(uri);
        return NormalizeHostAndPath(host, port, uri.AbsolutePath);
    }

    private static string GetNormalizedPort(Uri uri)
    {
        if (IsDefaultPort(uri.Scheme, uri.Port))
        {
            return string.Empty;
        }

        return $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool IsLocalRepositoryIdentity(string value, string baseDirectory)
    {
        if (Path.IsPathRooted(value) || value.StartsWith('.'))
        {
            return true;
        }

        return Directory.Exists(Path.GetFullPath(value, baseDirectory));
    }

    private static string NormalizeShorthandRemoteIdentity(string value, string repository)
    {
        value = RemoveQueryOrFragment(value);
        if (TryNormalizeScpIdentity(value, out var normalized))
        {
            return normalized;
        }

        return NormalizeComponentIdentity(value, repository);
    }

    private static string RemoveQueryOrFragment(string value)
    {
        var queryOrFragment = value.IndexOfAny(['?', '#']);
        if (queryOrFragment >= 0)
        {
            return value[..queryOrFragment];
        }

        return value;
    }

    private static bool TryNormalizeScpIdentity(string value, out string normalized)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || value[..colon].IndexOfAny(['/', '\\']) >= 0)
        {
            normalized = string.Empty;
            return false;
        }

        var authority = value[..colon];
        var userSeparator = authority.LastIndexOf('@');
        var host = authority[(userSeparator + 1)..].ToLowerInvariant();
        normalized = NormalizeHostAndPath(host, string.Empty, value[(colon + 1)..]);
        return true;
    }

    private static string NormalizeComponentIdentity(string value, string repository)
    {
        var components = value
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length < 2)
        {
            throw new InvalidOperationException(
                $"Repository '{repository}' is not a recognizable remote repository identity.");
        }

        var firstComponentIsHost = components[0].Contains('.', StringComparison.Ordinal);
        var hostName = ResolveComponentHost(components[0], firstComponentIsHost);
        var pathStart = firstComponentIsHost ? 1 : 0;
        return NormalizeHostAndPath(
            hostName,
            string.Empty,
            string.Join('/', components[pathStart..]));
    }

    private static string ResolveComponentHost(string firstComponent, bool firstComponentIsHost)
    {
        if (firstComponentIsHost)
        {
            return firstComponent.ToLowerInvariant();
        }

        return "github.com";
    }

    public static string NormalizeRemoteIdentity(string repository) =>
        NormalizeRepositoryIdentity(repository, Directory.GetCurrentDirectory());

    public static bool IsRemoteRepository(string repository, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (Uri.TryCreate(repository, UriKind.Absolute, out var uri))
        {
            return !uri.IsFile;
        }

        if (IsLocalRepositoryIdentity(repository, baseDirectory))
        {
            return false;
        }

        return HasRemoteRepositorySeparator(repository);
    }

    private static bool HasRemoteRepositorySeparator(string repository) =>
        repository.Contains('/', StringComparison.Ordinal) ||
        repository.Contains(':', StringComparison.Ordinal);

    public static bool RefersToSameRepository(string first, string second, string baseDirectory)
    {
        if (!IsRemoteRepository(first, baseDirectory) || !IsRemoteRepository(second, baseDirectory))
        {
            return false;
        }

        return string.Equals(
            NormalizeRepositoryIdentity(first, baseDirectory),
            NormalizeRepositoryIdentity(second, baseDirectory),
            StringComparison.Ordinal);
    }

    public static bool AreEquivalent(string first, string second, string baseDirectory)
    {
        var firstIsRemote = IsRemoteRepository(first, baseDirectory);
        var secondIsRemote = IsRemoteRepository(second, baseDirectory);
        if (firstIsRemote != secondIsRemote)
        {
            return false;
        }

        return firstIsRemote
            ? RefersToSameRepository(first, second, baseDirectory)
            : PathSafety.AreEqual(
                Path.GetFullPath(first, baseDirectory),
                Path.GetFullPath(second, baseDirectory));
    }

    public static string GetStepKey(
        string normalizedRepository,
        string? revision,
        string repositoryDirectoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryDirectoryName);
        var slug = CreateSlug(repositoryDirectoryName, 38);
        var suffix = GetStableHash(
            $"{normalizedRepository}\n{(string.IsNullOrWhiteSpace(revision) ? string.Empty : revision.Trim())}");
        return $"{slug}-{suffix}";
    }

    public static void EnsureDirectSibling(
        string appHostRepositoryRoot,
        string siblingParent,
        string repositoryPath)
    {
        var fullRepositoryPath = Path.GetFullPath(repositoryPath);
        var actualParent = Path.GetDirectoryName(fullRepositoryPath);
        if (PathSafety.AreEqual(fullRepositoryPath, appHostRepositoryRoot) ||
            !PathSafety.AreEqual(actualParent, siblingParent))
        {
            throw new InvalidOperationException(
                $"Repository initialization target '{repositoryPath}' must be a direct sibling of " +
                $"the AppHost Git root '{appHostRepositoryRoot}'.");
        }
    }

    private static string NormalizeHostAndPath(string host, string port, string path)
    {
        var normalizedPath = TrimGitSuffix(path.Replace('\\', '/').Trim('/'));
        ValidateRemoteHostAndPath(host, normalizedPath);
        normalizedPath = NormalizeRepositoryPathCase(host, normalizedPath);

        return $"{host}{port}/{normalizedPath}";
    }

    private static string TrimGitSuffix(string value)
    {
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return value[..^4];
        }

        return value;
    }

    private static void ValidateRemoteHostAndPath(string host, string path)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("A remote repository must include a host and repository path.");
        }
    }

    private static string NormalizeRepositoryPathCase(string host, string path)
    {
        if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return path.ToLowerInvariant();
        }

        return path;
    }

    private static string GetWorkingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException(
                    $"Unable to determine the directory containing '{path}'.");
    }

    private static string NormalizeLocalIdentity(string repository, string baseDirectory)
    {
        var fullPath = Path.GetFullPath(repository, baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"file:{fullPath.Replace(Path.DirectorySeparatorChar, '/')}";
    }

    private static string GetRepositoryName(string normalizedRepository)
    {
        var separator = normalizedRepository.LastIndexOf('/');
        return separator < 0 ? normalizedRepository : normalizedRepository[(separator + 1)..];
    }

    private static string GetRemoteRepositoryName(string repository)
    {
        var value = repository.Trim().TrimEnd('/', '\\');
        var path = GetRemoteRepositoryPath(value);
        path = path.Replace('\\', '/').Trim('/');
        var separator = path.LastIndexOf('/');
        var repositoryName = Uri.UnescapeDataString(GetLastPathSegment(path, separator));
        return TrimGitSuffix(repositoryName);
    }

    private static string GetRemoteRepositoryPath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath;
        }

        value = RemoveQueryOrFragment(value);
        return GetScpRepositoryPath(value);
    }

    private static string GetScpRepositoryPath(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && value[..colon].IndexOfAny(['/', '\\']) < 0)
        {
            return value[(colon + 1)..];
        }

        return value;
    }

    private static string GetLastPathSegment(string path, int separator)
    {
        if (separator < 0)
        {
            return path;
        }

        return path[(separator + 1)..];
    }

    private static string? GetInvalidCheckoutDirectoryNameReason(string value, string? revision)
    {
        if (!string.IsNullOrWhiteSpace(revision))
        {
            return $"it cannot be used with pinned repository revision '{FormatDiagnosticValue(revision)}'";
        }

        return CheckoutDirectoryNameRules.FirstOrDefault(rule => rule.IsInvalid(value)).Reason;
    }

    private static bool HasSurroundingWhitespace(string value) =>
        !string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsTooLong(string value) => value.Length > 255;

    private static bool IsTraversalSegment(string value) => value is "." or "..";

    private static bool IsRootedCheckoutPath(string value)
    {
        if (Path.IsPathRooted(value))
        {
            return true;
        }

        return IsWindowsRootedPath(value);
    }

    private static bool IsWindowsRootedPath(string value)
    {
        if (value.Length < 3)
        {
            return false;
        }

        if (!char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        return HasWindowsDrivePrefix(value);
    }

    private static bool HasWindowsDrivePrefix(string value) =>
        value[1] == ':' && IsDirectorySeparator(value[2]);

    private static bool HasDirectorySeparator(string value) => value.Any(IsDirectorySeparator);

    private static bool IsDirectorySeparator(char character) => character is '/' or '\\';

    private static bool HasInvalidFileNameCharacter(string value) => value.Any(IsInvalidFileNameCharacter);

    private static bool IsInvalidFileNameCharacter(char character) =>
        char.IsControl(character) || InvalidCheckoutFileNameCharacters.Contains(character);

    private static bool EndsWithPeriod(string value) => value.EndsWith('.');

    private static bool IsReservedCheckoutDirectoryName(string value)
    {
        var stem = value.Split('.', 2)[0];
        return ReservedCheckoutDirectoryNames.Contains(stem) || NumberedDeviceNames.Contains(stem);
    }

    private static string FormatDiagnosticValue(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string CreateSlug(string value, int maximumLength)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            AppendSlugCharacter(builder, character);
        }

        var slug = EnsureSlug(builder.ToString().Trim('-'));
        return TruncateSlug(slug, maximumLength);
    }

    private static void AppendSlugCharacter(StringBuilder builder, char character)
    {
        if (char.IsAsciiLetterOrDigit(character))
        {
            builder.Append(character);
            return;
        }

        AppendSlugSeparator(builder);
    }

    private static void AppendSlugSeparator(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '-')
        {
            builder.Append('-');
        }
    }

    private static string EnsureSlug(string slug)
    {
        if (slug.Length == 0)
        {
            return "repository";
        }

        return slug;
    }

    private static string TruncateSlug(string slug, int maximumLength)
    {
        if (slug.Length <= maximumLength)
        {
            return slug;
        }

        return slug[..maximumLength].TrimEnd('-');
    }

    private static string GetStableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..HashLength]
            .ToLowerInvariant();

    private static bool IsDefaultPort(string scheme, int port) =>
        port < 0 || DefaultRepositoryPorts.TryGetValue(scheme, out var defaultPort) && port == defaultPort;
}
