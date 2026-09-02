using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CA1308 // Aspire hashes normalized AppHost paths using lowercase text.

namespace Aspire.Hosting;

[JsonConverter(typeof(JsonStringEnumConverter<ModuleRepositoryCheckoutOwnership>))]
internal enum ModuleRepositoryCheckoutOwnership
{
    Created,
    Adopted
}

internal sealed record ModuleRepositoryInitializationState(
    int SchemaVersion,
    string Repository,
    string Destination,
    string? Revision,
    string ConfigurationFingerprint,
    string Origin,
    string ResolvedCommit,
    ModuleRepositoryCheckoutOwnership Ownership,
    DateTimeOffset InitializedAtUtc)
{
    public const int CurrentSchemaVersion = 2;

    public bool Matches(ModuleRepositoryRequirement requirement)
    {
        if (!HasMatchingIdentity(requirement))
        {
            return false;
        }

        return string.Equals(
            ConfigurationFingerprint,
            requirement.ConfigurationFingerprint,
            StringComparison.Ordinal);
    }

    public bool RefersTo(ModuleRepositoryRequirement requirement)
    {
        if (!Enum.IsDefined(Ownership))
        {
            return false;
        }

        return HasMatchingIdentity(requirement);
    }

    private bool HasMatchingIdentity(ModuleRepositoryRequirement requirement)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            return false;
        }

        return HasMatchingRepository(requirement) && HasMatchingRevision(requirement);
    }

    private bool HasMatchingRepository(ModuleRepositoryRequirement requirement) =>
        string.Equals(Repository, requirement.NormalizedRepository, StringComparison.Ordinal) &&
        PathSafety.AreEqual(Destination, requirement.RepositoryPath);

    private bool HasMatchingRevision(ModuleRepositoryRequirement requirement) =>
        string.Equals(Revision, requirement.Revision, StringComparison.Ordinal);
}

internal interface IModuleRepositoryStateStore
{
    string? StateFilePath { get; }

    Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken);

    Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken);
}

internal sealed class FileModuleRepositoryStateStore(string stateFilePath)
    : IModuleRepositoryStateStore, IDisposable
{
    private const string StateFileName = "modular-apphosts.json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _stateFilePath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(stateFilePath)
            ? throw new ArgumentException("The repository state file path is required.", nameof(stateFilePath))
            : stateFilePath);

    public string? StateFilePath => _stateFilePath;

    public void Dispose() => _gate.Dispose();

    public static string ResolveStateFilePath(
        string? appHostPathSha256,
        string appHostDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostDirectory);
        var stateDirectoryName = ResolveStateDirectoryName(appHostPathSha256, appHostDirectory);
        ValidateStateDirectoryName(stateDirectoryName);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments",
            stateDirectoryName,
            StateFileName);
    }

    private static string ResolveStateDirectoryName(string? appHostPathSha256, string appHostDirectory)
    {
        if (!string.IsNullOrWhiteSpace(appHostPathSha256))
        {
            return appHostPathSha256.Trim();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(appHostDirectory).ToLowerInvariant())));
    }

    private static void ValidateStateDirectoryName(string stateDirectoryName)
    {
        ValidateStateDirectoryLeafName(stateDirectoryName);
        ValidateStateDirectoryCharacters(stateDirectoryName);
    }

    private static void ValidateStateDirectoryLeafName(string stateDirectoryName)
    {
        if (!string.Equals(
                Path.GetFileName(stateDirectoryName),
                stateDirectoryName,
                StringComparison.Ordinal))
        {
            throw CreateInvalidStateDirectoryException(stateDirectoryName);
        }
    }

    private static void ValidateStateDirectoryCharacters(string stateDirectoryName)
    {
        if (stateDirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw CreateInvalidStateDirectoryException(stateDirectoryName);
        }
    }

    private static InvalidOperationException CreateInvalidStateDirectoryException(string stateDirectoryName) =>
        new($"AppHost path SHA '{stateDirectoryName}' is not a valid state-directory name.");

    public async Task<ModuleRepositoryInitializationState?> ReadAsync(
        ModuleRepositoryRequirement requirement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(repairMalformed: false, cancellationToken).ConfigureAwait(false);
            return document?.Repositories.GetValueOrDefault(requirement.StepKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(
        ModuleRepositoryRequirement requirement,
        ModuleRepositoryInitializationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(repairMalformed: true, cancellationToken).ConfigureAwait(false)
                ?? new ModuleRepositoryStateDocument();
            document.Repositories[requirement.StepKey] = state;
            await SaveAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ModuleRepositoryStateDocument?> LoadAsync(
        bool repairMalformed,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        var document = await TryDeserializeAsync(cancellationToken).ConfigureAwait(false);
        if (!HasRepositories(document))
        {
            return null;
        }

        return ValidateSchemaVersion(document!, repairMalformed);
    }

    private async Task<ModuleRepositoryStateDocument?> TryDeserializeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = new FileStream(
                _stateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<ModuleRepositoryStateDocument>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool HasRepositories(ModuleRepositoryStateDocument? document)
    {
        if (document is null)
        {
            return false;
        }

        return document.Repositories is not null;
    }

    private ModuleRepositoryStateDocument? ValidateSchemaVersion(
        ModuleRepositoryStateDocument document,
        bool repairMalformed)
    {
        if (document.SchemaVersion == ModuleRepositoryStateDocument.CurrentSchemaVersion)
        {
            return document;
        }

        ThrowForUnsupportedSchemaWhenRepairing(document, repairMalformed);
        return null;
    }

    private void ThrowForUnsupportedSchemaWhenRepairing(
        ModuleRepositoryStateDocument document,
        bool repairMalformed)
    {
        Action[] handlers =
        [
            static () => { },
            () => throw new InvalidOperationException(
                $"Repository state file '{_stateFilePath}' uses unsupported schema version " +
                $"'{document.SchemaVersion}'.")
        ];
        handlers[Convert.ToInt32(repairMalformed)]();
    }

    private async Task SaveAsync(
        ModuleRepositoryStateDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_stateFilePath)
            ?? throw new InvalidOperationException(
                $"Repository state path '{_stateFilePath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_stateFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _stateFilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ModuleRepositoryStateDocument
    {
        // Repository records have their own schema version, so the envelope stays
        // compatible with state files written by the repository-state preflight feature.
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public Dictionary<string, ModuleRepositoryInitializationState> Repositories { get; init; } =
            new(StringComparer.Ordinal);
    }
}
