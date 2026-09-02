#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREUSERSECRETS001

using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using CliWrap;
using HealthChecks.Uris;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Deploys or imports a Docker Compose environment and represents its services as an Aspire testing application.
/// </summary>
public sealed class DockerComposeDeploymentTestingBuilder : IDistributedApplicationTestingBuilder
{
    /// <summary>The environment variable that identifies the deployment environment file to load.</summary>
    public const string FilePathEnvironmentVariableName = "ASPIRE_TEST_CONFIGURATION_FILE";

    /// <summary>The environment variable that selects the Aspire deployment environment used by <see cref="DeployAsync{TEntryPoint}(CancellationToken)"/>.</summary>
    public const string DeploymentEnvironmentVariableName = "ASPIRE_TEST_DEPLOYMENT_ENVIRONMENT";

    /// <summary>The environment variable that selects the Aspire deployment output path used by <see cref="DeployAsync{TEntryPoint}(CancellationToken)"/>.</summary>
    public const string DeploymentOutputPathEnvironmentVariableName = "ASPIRE_TEST_DEPLOYMENT_OUTPUT_PATH";

    /// <summary>The prefix used for generated Aspire test deployment environment names.</summary>
    public const string DefaultDeploymentEnvironmentName = "Tests";

    private const string EndpointPrefix = "ASPIRE_TEST_ENDPOINT__";
    private const string EndpointHealthPathPrefix = "ASPIRE_TEST_ENDPOINT_HEALTH_PATH__";
    private const string ValuePrefix = "ASPIRE_TEST_VALUE__";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] PortConflictMessages =
    [
        "address already in use",
        "port is already allocated",
        "port is already in use"
    ];
    private static readonly HashSet<string> ReservedEnvironmentNames =
        new(StringComparer.Ordinal) { ".", ".." };
    private static readonly IReadOnlyList<string>[] OptionalDestroyArguments = [[], ["--yes"]];
    private readonly IDistributedApplicationBuilder _innerBuilder;
    private readonly OwnedDeployment? _ownedDeployment;
    private readonly object _lifecycleLock = new();

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The shared disposal task disposes the application asynchronously for both disposal APIs.")]
    private DistributedApplication? _application;
    private Task? _disposeTask;

    private DockerComposeDeploymentTestingBuilder(
        IDistributedApplicationBuilder innerBuilder,
        OwnedDeployment? ownedDeployment)
    {
        _innerBuilder = innerBuilder;
        _ownedDeployment = ownedDeployment;
    }

    /// <summary>
    /// Creates a testing builder from an Aspire-generated Docker Compose environment file.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly used by the deployment.</typeparam>
    /// <param name="filePath">The path to the environment-specific deployment file.</param>
    public static DockerComposeDeploymentTestingBuilder Create<TEntryPoint>(string filePath)
        where TEntryPoint : class
        => Create<TEntryPoint>(filePath, ownedDeployment: null);

    /// <summary>
    /// Deploys the AppHost to Docker Compose and creates a testing builder that owns the deployment.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly to deploy.</typeparam>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// The deployment environment defaults to a unique name prefixed by <see cref="DefaultDeploymentEnvironmentName"/> and
    /// can be overridden with <see cref="DeploymentEnvironmentVariableName"/>. The output path defaults to a temporary
    /// directory and can be overridden with <see cref="DeploymentOutputPathEnvironmentVariableName"/>. Asynchronously
    /// disposing the builder destroys the deployment.
    /// </remarks>
    public static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        var environmentName = System.Environment.GetEnvironmentVariable(DeploymentEnvironmentVariableName);
        var outputPath = System.Environment.GetEnvironmentVariable(DeploymentOutputPathEnvironmentVariableName);
        var options = new DockerComposeDeploymentOptions { OutputPath = outputPath };
        var environmentNames = new[] { options.EnvironmentName, environmentName! };
        options.EnvironmentName = environmentNames[Convert.ToInt32(!string.IsNullOrWhiteSpace(environmentName))];

        return DeployAsync<TEntryPoint>(options, cancellationToken);
    }

    /// <summary>
    /// Deploys the AppHost to Docker Compose with explicit deployment options and creates a testing builder that owns it.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly to deploy.</typeparam>
    /// <param name="options">The deployment options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>Asynchronously disposing the returned builder runs <c>aspire destroy</c>.</remarks>
    public static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        DockerComposeDeploymentOptions options,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        var appHostPath = ResolveAppHostPath(typeof(TEntryPoint).Assembly);
        return DeployCoreAsync<TEntryPoint>(
            Snapshot(options),
            appHostPath,
            CliWrapAspireCommandRunner.Instance,
            cancellationToken);
    }

    /// <summary>
    /// Deploys the AppHost to Docker Compose and creates a testing builder that owns the deployment.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly to deploy.</typeparam>
    /// <param name="environmentName">The Aspire deployment environment name.</param>
    /// <param name="outputPath">
    /// The deployment output path. When omitted, a temporary directory is created and removed with the deployment.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>Asynchronously disposing the returned builder runs <c>aspire destroy</c>.</remarks>
    public static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        string environmentName,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        return DeployAsync<TEntryPoint>(new DockerComposeDeploymentOptions
        {
            EnvironmentName = environmentName,
            OutputPath = outputPath
        }, cancellationToken);
    }

    internal static Task<DockerComposeDeploymentTestingBuilder> DeployAsync<TEntryPoint>(
        DockerComposeDeploymentOptions options,
        string appHostPath,
        IAspireCommandRunner commandRunner,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);
        ArgumentNullException.ThrowIfNull(commandRunner);
        return DeployCoreAsync<TEntryPoint>(
            Snapshot(options),
            Path.GetFullPath(appHostPath),
            commandRunner,
            cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "All deployment failures require best-effort cleanup before the original exception is rethrown.")]
    private static async Task<DockerComposeDeploymentTestingBuilder> DeployCoreAsync<TEntryPoint>(
        DockerComposeDeploymentOptions options,
        string appHostPath,
        IAspireCommandRunner commandRunner,
        CancellationToken cancellationToken)
        where TEntryPoint : class
    {
        var deployment = CreateOwnedDeployment(options, appHostPath, commandRunner);
        var outcome = await TryDeployWithRetriesAsync<TEntryPoint>(
            deployment,
            cancellationToken).ConfigureAwait(false);
        return await CompleteDeploymentAsync(deployment, outcome).ConfigureAwait(false);
    }

    private static OwnedDeployment CreateOwnedDeployment(
        DockerComposeDeploymentOptions options,
        string appHostPath,
        IAspireCommandRunner commandRunner)
    {
        var deleteOutputDirectory = string.IsNullOrWhiteSpace(options.OutputPath);
        var absoluteOutputPath = deleteOutputDirectory
            ? CreateTemporaryOutputPath(appHostPath)
            : Path.GetFullPath(options.OutputPath!);
        Directory.CreateDirectory(absoluteOutputPath);
        return new OwnedDeployment(
            appHostPath,
            absoluteOutputPath,
            options,
            deleteOutputDirectory,
            commandRunner);
    }

    private static async Task<DockerComposeDeploymentTestingBuilder> CompleteDeploymentAsync<TEntryPoint>(
        OwnedDeployment deployment,
        DeploymentAttempt<TEntryPoint> outcome)
        where TEntryPoint : class
    {
        if (outcome.Builder is not null)
        {
            return outcome.Builder;
        }

        var deploymentFailure = outcome.Failure
            ?? new InvalidOperationException("The Compose deployment failed without an exception.");
        await ThrowDeploymentFailureAsync(deployment, deploymentFailure).ConfigureAwait(false);
        throw new InvalidOperationException("Unreachable code.");
    }

    private static async Task<DeploymentAttempt<TEntryPoint>> TryDeployWithRetriesAsync<TEntryPoint>(
        OwnedDeployment deployment,
        CancellationToken cancellationToken)
        where TEntryPoint : class
    {
        for (var attempt = 0; attempt <= deployment.Options.PortConflictRetryCount; attempt++)
        {
            var outcome = await TryDeployOnceAsync<TEntryPoint>(
                deployment,
                attempt,
                cancellationToken).ConfigureAwait(false);
            if (ShouldStopDeploying(outcome))
            {
                return outcome;
            }
        }

        return new DeploymentAttempt<TEntryPoint>(null, null, ShouldRetry: false);
    }

    private static bool ShouldStopDeploying<TEntryPoint>(DeploymentAttempt<TEntryPoint> outcome)
        where TEntryPoint : class =>
        outcome.Builder is not null || !outcome.ShouldRetry;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Deployment failures are classified for retry and cleanup.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The successful builder is returned through the attempt result and transfers ownership to the caller.")]
    private static async Task<DeploymentAttempt<TEntryPoint>> TryDeployOnceAsync<TEntryPoint>(
        OwnedDeployment deployment,
        int attempt,
        CancellationToken cancellationToken)
        where TEntryPoint : class
    {
        try
        {
            await RunAspireCommandAsync("deploy", deployment, cancellationToken).ConfigureAwait(false);
            var configurationFilePath = Path.Combine(
                deployment.OutputPath,
                $".env.{deployment.Options.EnvironmentName}");
            return new DeploymentAttempt<TEntryPoint>(
                Create<TEntryPoint>(configurationFilePath, deployment),
                null,
                ShouldRetry: false);
        }
        catch (Exception exception)
        {
            return await HandleDeploymentAttemptFailureAsync<TEntryPoint>(
                deployment,
                attempt,
                exception).ConfigureAwait(false);
        }
    }

    private static async Task<DeploymentAttempt<TEntryPoint>> HandleDeploymentAttemptFailureAsync<TEntryPoint>(
        OwnedDeployment deployment,
        int attempt,
        Exception exception)
        where TEntryPoint : class
    {
        if (!CanRetryDeployment(deployment, attempt, exception))
        {
            return new DeploymentAttempt<TEntryPoint>(null, exception, ShouldRetry: false);
        }

        LogDeploymentRetry(deployment.Options.PortConflictRetryCount, attempt);
        await EnsureRetryCleanupSucceededAsync(deployment, exception).ConfigureAwait(false);
        return new DeploymentAttempt<TEntryPoint>(null, exception, ShouldRetry: true);
    }

    private static bool CanRetryDeployment(
        OwnedDeployment deployment,
        int attempt,
        Exception exception)
    {
        if (attempt >= deployment.Options.PortConflictRetryCount)
        {
            return false;
        }

        return IsPortConflict(exception);
    }

    private static void LogDeploymentRetry(int retryCount, int attempt) =>
        Console.WriteLine(
            $"[aspire deploy] Host-port conflict detected; cleaning the partial deployment before retry " +
            $"{attempt + 1} of {retryCount}.");

    private static async Task EnsureRetryCleanupSucceededAsync(
        OwnedDeployment deployment,
        Exception deploymentFailure)
    {
        var cleanupFailure = await DestroyFailedDeploymentAsync(deployment).ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            throw new AggregateException(
                $"The Compose deployment hit a host-port conflict and cleanup before retry also failed. " +
                $"Deployment state was retained at '{deployment.OutputPath}' for recovery.",
                deploymentFailure,
                cleanupFailure);
        }
    }

    private static async Task ThrowDeploymentFailureAsync(
        OwnedDeployment deployment,
        Exception deploymentFailure)
    {
        var cleanupFailure = await CleanupFailedDeploymentAsync(deployment).ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            throw new AggregateException(
                $"The Compose deployment failed and cleanup also failed. Deployment state was retained at " +
                $"'{deployment.OutputPath}' for recovery.",
                deploymentFailure,
                cleanupFailure);
        }

        ExceptionDispatchInfo.Capture(deploymentFailure).Throw();
    }

    private static DockerComposeDeploymentTestingBuilder Create<TEntryPoint>(
        string filePath,
        OwnedDeployment? ownedDeployment)
        where TEntryPoint : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var absolutePath = Path.GetFullPath(filePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException(
                $"The Aspire deployment test configuration file '{absolutePath}' does not exist.",
                absolutePath);
        }

        var appHostAssembly = typeof(TEntryPoint).Assembly;
        var innerBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = appHostAssembly.GetName().Name,
            DisableDashboard = true,
            ProjectDirectory = ResolveProjectDirectory(appHostAssembly)
        });
        innerBuilder.Services.AddHttpClient();

        var values = DotEnvFile.Load(absolutePath);
        ImportConfiguration(innerBuilder, values);
        ImportEndpoints(innerBuilder, values);

        return new DockerComposeDeploymentTestingBuilder(innerBuilder, ownedDeployment);
    }

    /// <summary>
    /// Creates a testing builder from the file identified by <see cref="FilePathEnvironmentVariableName"/>.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type in the AppHost assembly used by the deployment.</typeparam>
    public static DockerComposeDeploymentTestingBuilder CreateFromEnvironment<TEntryPoint>()
        where TEntryPoint : class
    {
        var filePath = System.Environment.GetEnvironmentVariable(FilePathEnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException(
                $"Set {FilePathEnvironmentVariableName} to the Aspire deployment environment file before running external tests.");
        }

        return Create<TEntryPoint>(filePath);
    }

    /// <inheritdoc />
    public ConfigurationManager Configuration => _innerBuilder.Configuration;

    /// <inheritdoc />
    public string AppHostDirectory => _innerBuilder.AppHostDirectory;

    /// <inheritdoc />
    public Assembly? AppHostAssembly => _innerBuilder.AppHostAssembly;

    /// <inheritdoc />
    public IHostEnvironment Environment => _innerBuilder.Environment;

    /// <inheritdoc />
    public IServiceCollection Services => _innerBuilder.Services;

    /// <inheritdoc />
    public DistributedApplicationExecutionContext ExecutionContext => _innerBuilder.ExecutionContext;

    /// <inheritdoc />
    public IDistributedApplicationEventing Eventing => _innerBuilder.Eventing;

    /// <inheritdoc />
    public IDistributedApplicationPipeline Pipeline => _innerBuilder.Pipeline;

    /// <inheritdoc />
    public IResourceCollection Resources => _innerBuilder.Resources;

    /// <inheritdoc />
    public IFileSystemService FileSystemService => _innerBuilder.FileSystemService;

    /// <inheritdoc />
    public IUserSecretsManager UserSecretsManager => _innerBuilder.UserSecretsManager;

    /// <inheritdoc />
    public IResourceBuilder<T> AddResource<T>(T resource)
        where T : IResource => _innerBuilder.AddResource(resource);

    /// <inheritdoc />
    public IResourceBuilder<T> CreateResourceBuilder<T>(T resource)
        where T : IResource => _innerBuilder.CreateResourceBuilder(resource);

    /// <inheritdoc />
    public DistributedApplication Build()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_application is not null)
            {
                throw new InvalidOperationException("The distributed application has already been built.");
            }

            return _application = _innerBuilder.Build();
        }
    }

    /// <inheritdoc />
    public Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Build());
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Design",
        "CA1065:Do not raise exceptions in unexpected locations",
        Justification = "Synchronous disposal cannot preserve the builder's required asynchronous deployment cleanup.")]
    [SuppressMessage(
        "Design",
        "CA1063:Implement IDisposable correctly",
        Justification = "The synchronous interface member is explicit so callers use the public asynchronous disposal contract.")]
    void IDisposable.Dispose() => throw new InvalidOperationException(
        $"{nameof(DockerComposeDeploymentTestingBuilder)} performs asynchronous cleanup. Use await using or await DisposeAsync().");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(GetOrCreateDisposalTask());

    private Task GetOrCreateDisposalTask()
    {
        TaskCompletionSource? completion = null;
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null)
            {
                return _disposeTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        _ = CompleteDisposalAsync(completion);
        return completion.Task;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every disposal failure must be transferred to all callers of the shared disposal task.")]
    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "All cleanup paths must run before disposal propagates failures.")]
    private async Task DisposeCoreAsync()
    {
        var applicationFailure = await TryDisposeApplicationAsync().ConfigureAwait(false);
        var deploymentFailure = await TryDisposeOwnedDeploymentAsync().ConfigureAwait(false);
        ThrowDisposalFailure(CombineFailures(applicationFailure, deploymentFailure));
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Application disposal failures are combined with deployment cleanup failures.")]
    private async Task<Exception?> TryDisposeApplicationAsync()
    {
        try
        {
            await DisposeApplicationIfPresentAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private Task DisposeApplicationIfPresentAsync() =>
        _application is null ? Task.CompletedTask : _application.DisposeAsync().AsTask();

    private async Task<Exception?> TryDisposeOwnedDeploymentAsync()
    {
        if (_ownedDeployment is null)
        {
            return null;
        }

        var destroyFailure = await DestroyFailedDeploymentAsync(_ownedDeployment).ConfigureAwait(false);
        if (destroyFailure is not null)
        {
            return destroyFailure;
        }

        return TryDeleteOwnedOutputDirectory(_ownedDeployment);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Output cleanup failures are combined with other disposal failures.")]
    private static Exception? TryDeleteOwnedOutputDirectory(OwnedDeployment deployment)
    {
        try
        {
            DeleteOwnedOutputDirectory(deployment);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? CombineFailures(Exception? first, Exception? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : new AggregateException(first, second);
    }

    private static void ThrowDisposalFailure(Exception? failure)
    {
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    internal static string GetEndpointVariableName(string resourceName) => EndpointPrefix + EncodeName(resourceName);

    internal static string GetEndpointVariableName(string resourceName, string endpointName) =>
        EndpointPrefix + EncodeName(resourceName) + "__" + EncodeName(endpointName);

    internal static string GetEndpointHealthPathVariableName(string resourceName) =>
        EndpointHealthPathPrefix + EncodeName(resourceName);

    internal static string GetEndpointHealthPathVariableName(string resourceName, string endpointName) =>
        EndpointHealthPathPrefix + EncodeName(resourceName) + "__" + EncodeName(endpointName);

    internal static string GetValueVariableName(string configurationKey) =>
        ValuePrefix + EncodeName(configurationKey);

    internal static string CreateDefaultDeploymentEnvironmentName() =>
        $"{DefaultDeploymentEnvironmentName}-{System.Environment.ProcessId}-{Guid.NewGuid():N}";

    private static string ResolveProjectDirectory(Assembly appHostAssembly)
    {
        var projectPath = GetMetadataValue(
            appHostAssembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            "AppHostProjectPath");
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return Path.GetDirectoryName(appHostAssembly.Location)!;
        }

        return GetProjectDirectory(projectPath);
    }

    private static string GetProjectDirectory(string projectPath)
    {
        var absolutePath = Path.GetFullPath(projectPath);
        var candidates = new[] { absolutePath, Path.GetDirectoryName(absolutePath)! };
        return candidates[Convert.ToInt32(
            string.Equals(Path.GetExtension(absolutePath), ".csproj", StringComparison.OrdinalIgnoreCase))];
    }

    internal static string ResolveAppHostPath(Assembly appHostAssembly)
    {
        var metadata = appHostAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        var projectPath = GetRequiredAppHostProjectPath(appHostAssembly, metadata);
        var absoluteProjectPath = Path.GetFullPath(projectPath);
        if (string.Equals(Path.GetExtension(absoluteProjectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return absoluteProjectPath;
        }

        var projectName = GetRequiredAppHostProjectName(appHostAssembly, metadata);
        var projectFileName = string.Equals(Path.GetExtension(projectName), ".csproj", StringComparison.OrdinalIgnoreCase)
            ? projectName
            : $"{projectName}.csproj";
        return Path.Combine(absoluteProjectPath, projectFileName);
    }

    private static string GetRequiredAppHostProjectPath(
        Assembly appHostAssembly,
        IReadOnlyList<AssemblyMetadataAttribute> metadata)
    {
        var projectPath = GetMetadataValue(metadata, "AppHostProjectPath");
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException(
                $"Assembly '{appHostAssembly.GetName().Name}' does not identify an Aspire AppHost project path.");
        }

        return projectPath;
    }

    private static string GetRequiredAppHostProjectName(
        Assembly appHostAssembly,
        IReadOnlyList<AssemblyMetadataAttribute> metadata)
    {
        var projectName = GetMetadataValue(metadata, "AppHostProjectName");
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new InvalidOperationException(
                $"Assembly '{appHostAssembly.GetName().Name}' does not identify an Aspire AppHost project name.");
        }

        return projectName;
    }

    private static string? GetMetadataValue(
        IEnumerable<AssemblyMetadataAttribute> metadata,
        string key) =>
        metadata.FirstOrDefault(attribute => string.Equals(
            attribute.Key,
            key,
            StringComparison.OrdinalIgnoreCase))?.Value;

    private static string CreateTemporaryOutputPath(string appHostPath)
    {
        var appHostName = Path.GetFileNameWithoutExtension(appHostPath);
        return Path.Combine(
            Path.GetTempPath(),
            "aspire-compose-tests",
            appHostName,
            Guid.NewGuid().ToString("N"));
    }

    private static void ValidateEnvironmentName(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        if (ReservedEnvironmentNames.Contains(environmentName))
        {
            throw CreateInvalidEnvironmentNameException(environmentName);
        }

        ValidateEnvironmentNameCharacters(environmentName);
    }

    private static void ValidateEnvironmentNameCharacters(string environmentName)
    {
        if (environmentName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw CreateInvalidEnvironmentNameException(environmentName);
        }
    }

    private static ArgumentException CreateInvalidEnvironmentNameException(string environmentName) =>
        new(
            $"The deployment environment name '{environmentName}' is not valid as an environment file suffix.",
            nameof(environmentName));

    private static void ValidateOptions(DockerComposeDeploymentOptions options)
    {
        ValidateEnvironmentName(options.EnvironmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AspireCliPath);
        ValidateDeploymentTimeout(options);
        ValidateCleanupTimeout(options);
        ValidatePortConflictRetryCount(options);
    }

    private static void ValidateDeploymentTimeout(DockerComposeDeploymentOptions options)
    {
        if (options.DeploymentTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.DeploymentTimeout,
                "The deployment timeout must be positive.");
        }
    }

    private static void ValidateCleanupTimeout(DockerComposeDeploymentOptions options)
    {
        if (options.CleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.CleanupTimeout,
                "The cleanup timeout must be positive.");
        }
    }

    private static void ValidatePortConflictRetryCount(DockerComposeDeploymentOptions options)
    {
        if (options.PortConflictRetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PortConflictRetryCount,
                "The port-conflict retry count cannot be negative.");
        }
    }

    private static DockerComposeDeploymentOptions Snapshot(DockerComposeDeploymentOptions options) => new()
    {
        EnvironmentName = options.EnvironmentName,
        OutputPath = options.OutputPath,
        AspireCliPath = options.AspireCliPath,
        PortConflictRetryCount = options.PortConflictRetryCount,
        DeploymentTimeout = options.DeploymentTimeout,
        CleanupTimeout = options.CleanupTimeout
    };

    private static async Task RunAspireCommandAsync(
        string command,
        OwnedDeployment deployment,
        CancellationToken cancellationToken)
    {
        var timeout = string.Equals(command, "deploy", StringComparison.Ordinal)
            ? deployment.Options.DeploymentTimeout
            : deployment.Options.CleanupTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await deployment.CommandRunner.RunAsync(
                deployment.Options.AspireCliPath,
                command,
                deployment.AppHostPath,
                deployment.OutputPath,
                deployment.Options.EnvironmentName,
                linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"'aspire {command}' exceeded the configured timeout of {timeout}.",
                exception);
        }
    }

    private static async Task RunAspireCliAsync(
        string aspireCliPath,
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        CancellationToken cancellationToken)
    {
        var invocation = ResolveAspireCliInvocation(aspireCliPath, appHostPath);
        var output = new AspireCommandOutput(command);
        var result = await ExecuteAspireCliAsync(
            invocation,
            command,
            appHostPath,
            outputPath,
            environmentName,
            output,
            cancellationToken).ConfigureAwait(false);
        result = await TryFallbackToAspireOnPathAsync(
            result,
            invocation,
            aspireCliPath,
            command,
            appHostPath,
            outputPath,
            environmentName,
            output,
            cancellationToken).ConfigureAwait(false);
        EnsureAspireCommandSucceeded(result, command, appHostPath, output.Text);
    }

    private static async Task<CommandResult> ExecuteAspireCliAsync(
        AspireCliInvocation invocation,
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        AspireCommandOutput output,
        CancellationToken cancellationToken)
    {
        var arguments = CreateAspireArguments(
            invocation,
            command,
            appHostPath,
            outputPath,
            environmentName);
        return await CliCommand.Wrap(invocation.Executable)
            .WithArguments(arguments)
            .WithWorkingDirectory(Path.GetDirectoryName(appHostPath)!)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(output.Report))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(output.Report))
            .ExecuteAsync(cancellationToken);
    }

    private static List<string> CreateAspireArguments(
        AspireCliInvocation invocation,
        string command,
        string appHostPath,
        string outputPath,
        string environmentName)
    {
        var arguments = new List<string>(invocation.PrefixArguments)
        {
            command,
            "--apphost",
            appHostPath,
            "--output-path",
            outputPath,
            "--environment",
            environmentName
        };
        arguments.AddRange(OptionalDestroyArguments[Convert.ToInt32(
            string.Equals(command, "destroy", StringComparison.Ordinal))]);
        arguments.Add("--non-interactive");
        return arguments;
    }

    private static async Task<CommandResult> TryFallbackToAspireOnPathAsync(
        CommandResult result,
        AspireCliInvocation invocation,
        string aspireCliPath,
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        AspireCommandOutput output,
        CancellationToken cancellationToken)
    {
        var shouldFallback = !result.IsSuccess & ShouldFallBackToAspireOnPath(invocation, output.Text);
        Func<Task<CommandResult>>[] operations =
        [
            () => Task.FromResult(result),
            () => ExecuteAspireFallbackAsync(
                aspireCliPath,
                command,
                appHostPath,
                outputPath,
                environmentName,
                output,
                cancellationToken)
        ];
        return await operations[Convert.ToInt32(shouldFallback)]().ConfigureAwait(false);
    }

    private static Task<CommandResult> ExecuteAspireFallbackAsync(
        string aspireCliPath,
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        AspireCommandOutput output,
        CancellationToken cancellationToken)
    {
        output.Report("The manifest tool is not restored; falling back to 'aspire' on PATH.");
        return ExecuteAspireCliAsync(
            new AspireCliInvocation(aspireCliPath, []),
            command,
            appHostPath,
            outputPath,
            environmentName,
            output,
            cancellationToken);
    }

    private static void EnsureAspireCommandSucceeded(
        CommandResult result,
        string command,
        string appHostPath,
        string output)
    {
        Action[] handlers =
        [
            () => throw new InvalidOperationException(
                CreateAspireCommandFailureMessage(command, result.ExitCode, appHostPath, output)),
            static () => { }
        ];
        handlers[Convert.ToInt32(result.IsSuccess)]();
    }

    private static string CreateAspireCommandFailureMessage(
        string command,
        int exitCode,
        string appHostPath,
        string output)
    {
        var diagnostic = TruncateDiagnostic(output.Trim());
        var message = $"'aspire {command}' exited with code {exitCode} for AppHost '{appHostPath}'.";
        return AppendDiagnostic(message, diagnostic);
    }

    private static string TruncateDiagnostic(string diagnostic) =>
        diagnostic[^Math.Min(diagnostic.Length, 4000)..];

    private static string AppendDiagnostic(string message, string diagnostic)
    {
        var messages = new[] { $"{message}{System.Environment.NewLine}{diagnostic}", message };
        return messages[Convert.ToInt32(diagnostic.Length == 0)];
    }

    internal static AspireCliInvocation ResolveAspireCliInvocation(string aspireCliPath, string appHostPath)
        => AspireCliInvocationResolver.Resolve(
            aspireCliPath,
            appHostPath);

    internal static bool ShouldFallBackToAspireOnPath(AspireCliInvocation invocation, string output)
        => AspireCliInvocationResolver.ShouldFallBackToAspireOnPath(invocation, output);

    private static bool IsPortConflict(Exception exception)
    {
        if (HasPortConflictMessage(exception.Message))
        {
            return true;
        }

        return HasNestedPortConflict(exception.InnerException);
    }

    private static bool HasPortConflictMessage(string message) =>
        PortConflictMessages
            .Any(candidate => message.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool HasNestedPortConflict(Exception? innerException)
    {
        if (innerException is null)
        {
            return false;
        }

        return IsPortConflict(innerException);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup failures are returned so they can be reported with the deployment failure.")]
    private static async Task<Exception?> CleanupFailedDeploymentAsync(OwnedDeployment deployment)
    {
        var destroyFailure = await DestroyFailedDeploymentAsync(deployment).ConfigureAwait(false);
        if (destroyFailure is not null)
        {
            return destroyFailure;
        }

        try
        {
            DeleteOwnedOutputDirectory(deployment);
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup failures are returned so callers can preserve deployment diagnostics and recovery state.")]
    private static async Task<Exception?> DestroyFailedDeploymentAsync(OwnedDeployment deployment)
    {
        try
        {
            await RunAspireCommandAsync("destroy", deployment, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void DeleteOwnedOutputDirectory(OwnedDeployment deployment)
    {
        if (deployment.DeleteOutputDirectory && Directory.Exists(deployment.OutputPath))
        {
            Directory.Delete(deployment.OutputPath, recursive: true);
        }
    }

    private static void ImportConfiguration(
        IDistributedApplicationBuilder builder,
        Dictionary<string, string> values)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values.Where(pair => pair.Key.StartsWith(ValuePrefix, StringComparison.Ordinal)))
        {
            var configurationKey = DecodeName(pair.Key[ValuePrefix.Length..]);
            if (!configuration.TryAdd(configurationKey, pair.Value))
            {
                throw new InvalidOperationException(
                    $"The deployment test configuration exports configuration key '{configurationKey}' more than once.");
            }
        }

        builder.Configuration.AddInMemoryCollection(configuration);
    }

    private static void ImportEndpoints(
        IDistributedApplicationBuilder builder,
        Dictionary<string, string> values)
    {
        var resources = new Dictionary<string, ImportedEndpointResource>(
            StringComparer.OrdinalIgnoreCase);
        var endpointVariableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in values.Where(pair => pair.Key.StartsWith(EndpointPrefix, StringComparison.Ordinal)))
        {
            endpointVariableNames.Add(pair.Key);
            var endpoint = ParseImportedEndpoint(pair);
            var imported = GetOrAddImportedResource(builder, resources, endpoint.ResourceName);
            AddImportedEndpoint(imported, endpoint);
            AddImportedHealthCheck(builder, imported, endpoint, values);
        }

        ValidateHealthCheckEndpoints(values, endpointVariableNames);
    }

    private static ImportedEndpoint ParseImportedEndpoint(KeyValuePair<string, string> pair)
    {
        var encodedEndpoint = pair.Key[EndpointPrefix.Length..];
        var (resourceName, endpointName) = DecodeEndpointName(encodedEndpoint);
        var endpoint = ParseHttpOrigin(resourceName, pair.Value);
        return new ImportedEndpoint(
            encodedEndpoint,
            resourceName,
            endpointName ?? endpoint.Scheme,
            endpoint);
    }

    private static (string ResourceName, string? EndpointName) DecodeEndpointName(string encodedEndpoint)
    {
        var separator = encodedEndpoint.IndexOf("__", StringComparison.Ordinal);
        if (separator < 0)
        {
            return (DecodeName(encodedEndpoint), null);
        }

        return (
            DecodeName(encodedEndpoint[..separator]),
            DecodeName(encodedEndpoint[(separator + 2)..]));
    }

    private static Uri ParseHttpOrigin(string resourceName, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
        {
            throw CreateInvalidEndpointException(resourceName, value);
        }

        ValidateHttpScheme(resourceName, value, endpoint);
        ValidateOriginPath(resourceName, value, endpoint);
        ValidateOriginQuery(resourceName, value, endpoint);
        ValidateOriginFragment(resourceName, value, endpoint);
        ValidateOriginUserInfo(resourceName, value, endpoint);
        return endpoint;
    }

    private static void ValidateHttpScheme(string resourceName, string value, Uri endpoint)
    {
        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateInvalidEndpointException(resourceName, value);
        }
    }

    private static void ValidateOriginPath(string resourceName, string value, Uri endpoint)
    {
        if (endpoint.AbsolutePath != "/")
        {
            throw CreateInvalidEndpointException(resourceName, value);
        }
    }

    private static void ValidateOriginQuery(string resourceName, string value, Uri endpoint)
    {
        if (endpoint.Query.Length > 0)
        {
            throw CreateInvalidEndpointException(resourceName, value);
        }
    }

    private static void ValidateOriginFragment(string resourceName, string value, Uri endpoint)
    {
        if (endpoint.Fragment.Length > 0)
        {
            throw CreateInvalidEndpointException(resourceName, value);
        }
    }

    private static void ValidateOriginUserInfo(string resourceName, string value, Uri endpoint)
    {
        if (endpoint.UserInfo.Length > 0)
        {
            throw CreateInvalidEndpointException(resourceName, value);
        }
    }

    private static InvalidOperationException CreateInvalidEndpointException(
        string resourceName,
        string value) =>
        new(
            $"The exported test endpoint '{resourceName}' must be an absolute HTTP(S) origin without " +
            $"credentials, path, query, or fragment; found '{value}'.");

    private static ImportedEndpointResource GetOrAddImportedResource(
        IDistributedApplicationBuilder builder,
        Dictionary<string, ImportedEndpointResource> resources,
        string resourceName)
    {
        if (resources.TryGetValue(resourceName, out var imported))
        {
            return imported;
        }

        var resource = new DeployedEndpointResource(resourceName);
        var resourceBuilder = builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "ExternalService",
                State = KnownResourceStates.Running,
                Properties = []
            })
            .ExcludeFromManifest();
        imported = new ImportedEndpointResource(resource, resourceBuilder);
        resources.Add(resourceName, imported);
        return imported;
    }

    private static void AddImportedEndpoint(
        ImportedEndpointResource imported,
        ImportedEndpoint endpoint)
    {
        if (HasEndpoint(imported.Resource, endpoint.EndpointName))
        {
            throw new InvalidOperationException(
                $"The exported test endpoint '{endpoint.ResourceName}/{endpoint.EndpointName}' is defined more than once.");
        }

        var annotation = CreateEndpointAnnotation(endpoint);
        imported.Resource.Annotations.Add(annotation);
        imported.Builder.WithUrl(endpoint.Uri.AbsoluteUri);
    }

    private static bool HasEndpoint(DeployedEndpointResource resource, string endpointName) =>
        resource.Annotations.OfType<EndpointAnnotation>().Any(annotation =>
            string.Equals(annotation.Name, endpointName, StringComparison.OrdinalIgnoreCase));

    private static EndpointAnnotation CreateEndpointAnnotation(ImportedEndpoint endpoint)
    {
        var annotation = new EndpointAnnotation(
            ProtocolType.Tcp,
            uriScheme: endpoint.Uri.Scheme,
            transport: endpoint.Uri.Scheme,
            name: endpoint.EndpointName,
            port: endpoint.Uri.Port,
            targetPort: endpoint.Uri.Port,
            isExternal: true,
            isProxied: false)
        {
            TargetHost = endpoint.Uri.Host
        };
        annotation.AllocatedEndpoint = new AllocatedEndpoint(
            annotation,
            endpoint.Uri.Host,
            endpoint.Uri.Port);
        return annotation;
    }

    private static void AddImportedHealthCheck(
        IDistributedApplicationBuilder builder,
        ImportedEndpointResource imported,
        ImportedEndpoint endpoint,
        Dictionary<string, string> values)
    {
        var healthKey = EndpointHealthPathPrefix + endpoint.EncodedName;
        if (!values.TryGetValue(healthKey, out var healthPath))
        {
            return;
        }

        ValidateHealthPath(endpoint, healthPath);
        var healthCheckKey = $"{endpoint.ResourceName}_{endpoint.EndpointName}_deployment_check";
        var healthCheckUri = new Uri(endpoint.Uri, healthPath);
        builder.Services.AddHealthChecks().AddUrlGroup(
            options => options.AddUri(healthCheckUri),
            healthCheckKey);
        imported.Builder.WithHealthCheck(healthCheckKey);
    }

    private static void ValidateHealthPath(ImportedEndpoint endpoint, string healthPath)
    {
        if (!TestEndpointHealthPath.IsRootRelative(healthPath))
        {
            throw new InvalidOperationException(
                $"The exported health check for '{endpoint.ResourceName}/{endpoint.EndpointName}' must be a " +
                $"root-relative URI path; found '{healthPath}'.");
        }
    }

    private static void ValidateHealthCheckEndpoints(
        Dictionary<string, string> values,
        HashSet<string> endpointVariableNames)
    {
        foreach (var healthPair in values.Where(pair =>
                     pair.Key.StartsWith(EndpointHealthPathPrefix, StringComparison.Ordinal)))
        {
            var endpointVariableName = EndpointPrefix + healthPair.Key[EndpointHealthPathPrefix.Length..];
            if (!endpointVariableNames.Contains(endpointVariableName))
            {
                throw new InvalidOperationException(
                    $"The deployment test configuration exports health check '{healthPair.Key}' without its endpoint.");
            }
        }
    }

    private static string EncodeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Convert.ToHexString(Encoding.UTF8.GetBytes(name));
    }

    private static string DecodeName(string encodedName)
    {
        try
        {
            var decodedName = StrictUtf8.GetString(Convert.FromHexString(encodedName));
            if (string.IsNullOrWhiteSpace(decodedName))
            {
                throw new FormatException("The decoded name is empty.");
            }

            return decodedName;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidOperationException(
                $"The Aspire deployment test configuration contains invalid encoded name '{encodedName}'.",
                exception);
        }
    }

    private sealed class DeployedEndpointResource(string name) : Resource(name), IResourceWithEndpoints;

    private sealed record ImportedEndpoint(
        string EncodedName,
        string ResourceName,
        string EndpointName,
        Uri Uri);

    private sealed record ImportedEndpointResource(
        DeployedEndpointResource Resource,
        IResourceBuilder<DeployedEndpointResource> Builder);

    private sealed class AspireCommandOutput(string command)
    {
        private readonly StringBuilder _output = new();
        private readonly object _lock = new();

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return _output.ToString();
                }
            }
        }

        public void Report(string line)
        {
            lock (_lock)
            {
                _output.AppendLine(line);
                Console.WriteLine($"[aspire {command}] {line}");
            }
        }
    }

    private sealed class CliWrapAspireCommandRunner : IAspireCommandRunner
    {
        public static CliWrapAspireCommandRunner Instance { get; } = new();

        public Task RunAsync(
            string aspireCliPath,
            string command,
            string appHostPath,
            string outputPath,
            string environmentName,
            CancellationToken cancellationToken) =>
            RunAspireCliAsync(
                aspireCliPath,
                command,
                appHostPath,
                outputPath,
                environmentName,
                cancellationToken);
    }

    private sealed record OwnedDeployment(
        string AppHostPath,
        string OutputPath,
        DockerComposeDeploymentOptions Options,
        bool DeleteOutputDirectory,
        IAspireCommandRunner CommandRunner);

    private sealed record DeploymentAttempt<TEntryPoint>(
        DockerComposeDeploymentTestingBuilder? Builder,
        Exception? Failure,
        bool ShouldRetry)
        where TEntryPoint : class;
}

internal interface IAspireCommandRunner
{
    Task RunAsync(
        string aspireCliPath,
        string command,
        string appHostPath,
        string outputPath,
        string environmentName,
        CancellationToken cancellationToken);
}
