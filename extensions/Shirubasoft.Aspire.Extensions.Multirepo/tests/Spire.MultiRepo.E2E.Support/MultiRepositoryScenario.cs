#pragma warning disable CA1308 // Aspire hashes the lowercase AppHost identity.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    private const string ModuleName = "multi-repo-resource-build";
    private const string ResourceName = "multi-repo-api";
    private const string PinnedMarker = "multi-repo-resource-pinned-revision";
    private const string LatestMarker = "multi-repo-resource-unpinned-latest";
    private const string AdoptedUpstreamMarker = "multi-repo-resource-adopted-upstream";
    private const string InitializedMarker = "multi-repo-resource-initialized-update";
    private const string RefreshMarker = "multi-repo-resource-runtime-refresh";
    private const string DirtyMarker = "multi-repo-resource-dirty-rebuild";
    private const string DirtyUpstreamMarker = "multi-repo-resource-dirty-upstream";
    private const string PackageVersion = "0.0.0-multi-repo-e2e";
    private const string ExtensionDirectoryName = "Shirubasoft.Aspire.Extensions.Multirepo";
    private static readonly string ExtensionRelativePath = Path.Combine(
        "extensions",
        ExtensionDirectoryName);
    private static readonly string[] FixturePathSpecs =
    [
        ".config/dotnet-tools.json",
        ".gitignore",
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        ExtensionRelativePath
    ];

    private static string FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Aspire.Extensions.slnx")) &&
                File.Exists(Path.Combine(
                    current.FullName,
                    ExtensionRelativePath,
                    $"{ExtensionDirectoryName}.slnx")) &&
                (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                 File.Exists(Path.Combine(current.FullName, ".git"))))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Unable to find the Multirepo extension root above '{startDirectory}'. " +
            "Pass --repository-root explicitly.");
    }

    private static async Task WaitForExitAndKillAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class MultiRepositoryScenario(
        string repositoryRoot,
        string temporaryRoot,
        E2EOptions options)
    {
        private readonly ProcessExecutor _process = new();
        private readonly AspireCommand _aspire = AspireCommand.Create(options.AspirePath);
        private readonly string _consumerRepository = Path.Combine(temporaryRoot, "consumer");
        private readonly string _contractRepository = Path.Combine(temporaryRoot, "producer-contract-source");
        private readonly string _resourceRepository = Path.Combine(temporaryRoot, "resource-build-source");
        private readonly string _packageFeed = Path.Combine(temporaryRoot, "packages");
        private readonly string _nugetPackages = Path.Combine(temporaryRoot, "nuget-packages");
        private readonly string _gitProxyLog = Path.Combine(temporaryRoot, "git-proxy.jsonl");
        private readonly string _runtimeProxyLogDirectory = Path.Combine(temporaryRoot, "runtime-proxy-log");
        private readonly string _runtimeShimDirectory = Path.Combine(temporaryRoot, "runtime-shim");
        private readonly string _driverExecutable = GetDriverExecutable();
        private string _appHost = string.Empty;
        private string _pinnedRevision = string.Empty;
        private string _latestRevision = string.Empty;
        private string _remoteRepository = string.Empty;
        private string? _realContainerRuntime;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await WritePhaseAsync("Create isolated package consumer and producer repositories").ConfigureAwait(false);
            await CreateFixtureAsync(cancellationToken).ConfigureAwait(false);

            await WritePhaseAsync("Fail fast before pinned initialization").ConfigureAwait(false);
            var pinnedEnvironment = CreateAppHostEnvironment(
                _resourceRepository,
                _pinnedRevision,
                GitProxyPolicy.Initialize);
            await AssertStartRequiresInitializationAsync(pinnedEnvironment, cancellationToken).ConfigureAwait(false);

            await WritePhaseAsync("Initialize a local pinned source without moving its developer checkout")
                .ConfigureAwait(false);
            var pinnedDirectoriesBefore = GetSiblingGitDirectories();
            var pinnedInitialization = await RunInitializeAsync(
                pinnedEnvironment,
                cancellationToken).ConfigureAwait(false);
            var pinnedCheckout = GetNewSiblingGitDirectory(pinnedDirectoriesBefore);
            AssertEqual(
                _pinnedRevision,
                await GetRevisionAsync(pinnedCheckout, cancellationToken).ConfigureAwait(false),
                "The initializer-owned pinned checkout did not resolve the requested revision.");
            AssertEqual(
                _latestRevision,
                await GetRevisionAsync(_resourceRepository, cancellationToken).ConfigureAwait(false),
                "Pinned initialization moved the developer checkout.");
            AssertEqual(
                string.Empty,
                await RunGitOutputAsync(pinnedCheckout, ["branch", "--show-current"], cancellationToken)
                    .ConfigureAwait(false),
                "The pinned checkout is not detached.");
            AssertEqual(
                Path.GetFullPath(_resourceRepository),
                Path.GetFullPath(await RunGitOutputAsync(
                    pinnedCheckout,
                    ["remote", "get-url", "origin"],
                    cancellationToken).ConfigureAwait(false)),
                "The pinned checkout has the wrong producer origin.");
            AssertContains(
                Path.GetFileName(pinnedCheckout),
                "-rev-",
                "The pinned checkout did not use an isolated hashed revision sibling.");
            AssertRepositoryState(
                pinnedCheckout,
                "Created",
                normalizedRepository: null);
            AssertInitializationLifecycle(
                pinnedInitialization.CombinedOutput,
                pinnedCheckout,
                "clone",
                "fetch",
                "checkout",
                "submodule-update");

            var siblingCount = GetSiblingGitDirectories().Count;
            var pinnedRevisionBeforeRepeat = await GetRevisionAsync(pinnedCheckout, cancellationToken)
                .ConfigureAwait(false);
            await RunInitializeAsync(pinnedEnvironment, cancellationToken).ConfigureAwait(false);
            AssertEqual(
                siblingCount,
                GetSiblingGitDirectories().Count,
                "Repeated initialization created another sibling checkout.");
            AssertEqual(
                pinnedRevisionBeforeRepeat,
                await GetRevisionAsync(pinnedCheckout, cancellationToken).ConfigureAwait(false),
                "Repeated initialization moved the pinned checkout.");

            await WritePhaseAsync("Run the pinned image with read-only Git inspection").ConfigureAwait(false);
            DeleteGitProxyLog();
            DeleteRuntimeProxyLog();
            var pinnedRunEnvironment = CreateAppHostEnvironment(
                _resourceRepository,
                _pinnedRevision,
                GitProxyPolicy.ReadOnly);
            await AssertResourceMarkerAsync(pinnedRunEnvironment, PinnedMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertNoRepositoryMutation(ReadGitProxyOperations());
            AssertConfiguredContainerRuntimeUsed(ReadRuntimeProxyOperations());

            await WritePhaseAsync("Adopt an existing matching canonical sibling without moving it")
                .ConfigureAwait(false);
            var adoptedCheckout = Path.Combine(temporaryRoot, "resource-build-adopted");
            await RunGitAsync(
                temporaryRoot,
                ["clone", _resourceRepository, adoptedCheckout],
                cancellationToken).ConfigureAwait(false);
            await RunGitAsync(
                adoptedCheckout,
                ["remote", "set-url", "origin", _remoteRepository],
                cancellationToken).ConfigureAwait(false);
            var adoptedRevision = await GetRevisionAsync(adoptedCheckout, cancellationToken)
                .ConfigureAwait(false);
            _latestRevision = await CommitMarkerAsync(
                AdoptedUpstreamMarker,
                "Advance upstream before adopting the canonical checkout",
                cancellationToken).ConfigureAwait(false);
            var adoptedEnvironment = CreateAppHostEnvironment(
                _remoteRepository,
                revision: null,
                GitProxyPolicy.Initialize,
                checkoutDirectoryName: Path.GetFileName(adoptedCheckout));
            DeleteGitProxyLog();
            await RunInitializeAsync(adoptedEnvironment, cancellationToken).ConfigureAwait(false);
            AssertEqual(
                adoptedRevision,
                await GetRevisionAsync(adoptedCheckout, cancellationToken).ConfigureAwait(false),
                "Initialization moved an adopted developer checkout after upstream advanced.");
            AssertNoRepositoryMutation(ReadGitProxyOperations());
            AssertRepositoryState(adoptedCheckout, "Adopted");

            var adoptedDeveloperFile = Path.Combine(adoptedCheckout, "developer-note.txt");
            await File.WriteAllTextAsync(
                adoptedDeveloperFile,
                "developer-owned",
                cancellationToken).ConfigureAwait(false);
            DeleteGitProxyLog();
            await RunInitializeAsync(adoptedEnvironment, cancellationToken).ConfigureAwait(false);
            AssertEqual(
                adoptedRevision,
                await GetRevisionAsync(adoptedCheckout, cancellationToken).ConfigureAwait(false),
                "Repeated initialization moved an adopted dirty checkout.");
            if (!File.Exists(adoptedDeveloperFile))
            {
                throw new InvalidOperationException(
                    "Repeated initialization removed a developer-owned file from the adopted checkout.");
            }

            AssertNoRepositoryMutation(ReadGitProxyOperations());
            AssertRepositoryState(adoptedCheckout, "Adopted");

            await WritePhaseAsync("Fail fast and initialize a remote repository")
                .ConfigureAwait(false);
            DeleteGitProxyLog();
            var remoteEnvironment = CreateAppHostEnvironment(
                _remoteRepository,
                revision: null,
                GitProxyPolicy.Initialize);
            await AssertStartRequiresInitializationAsync(remoteEnvironment, cancellationToken).ConfigureAwait(false);
            var remoteDirectoriesBefore = GetSiblingGitDirectories();
            var remoteInitialization = await RunInitializeAsync(remoteEnvironment, cancellationToken)
                .ConfigureAwait(false);
            var remoteCheckout = GetNewSiblingGitDirectory(remoteDirectoriesBefore);
            AssertEqual(
                Path.Combine(temporaryRoot, "resource-build"),
                remoteCheckout,
                "The missing unpinned remote was not cloned into its canonical human-readable sibling.");
            AssertInitializationLifecycle(remoteInitialization.CombinedOutput, remoteCheckout, "clone");
            AssertRepositoryState(remoteCheckout, "Created");
            AssertEqual(
                _latestRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "The unpinned checkout did not start at the producer's latest revision.");
            AssertEqual(
                _remoteRepository,
                await RunGitOutputAsync(
                    remoteCheckout,
                    ["remote", "get-url", "origin"],
                    cancellationToken).ConfigureAwait(false),
                "The unpinned checkout has the wrong normalized origin.");

            await WritePhaseAsync("Pick up a clean upstream change through initialize").ConfigureAwait(false);
            var initializedRevision = await CommitMarkerAsync(
                InitializedMarker,
                "Change marker for another initialization",
                cancellationToken).ConfigureAwait(false);
            var updateInitialization = await RunInitializeAsync(remoteEnvironment, cancellationToken)
                .ConfigureAwait(false);
            AssertInitializationLifecycle(
                updateInitialization.CombinedOutput,
                remoteCheckout,
                "fast-forward");
            AssertEqual(
                initializedRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "Another initialization did not fast-forward the clean checkout.");

            await WritePhaseAsync("Build the initialized image and keep normal run read-only").ConfigureAwait(false);
            DeleteGitProxyLog();
            var readOnlyEnvironment = CreateAppHostEnvironment(
                _remoteRepository,
                revision: null,
                GitProxyPolicy.ReadOnly);
            await AssertResourceMarkerAsync(readOnlyEnvironment, InitializedMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertNoRepositoryMutation(ReadGitProxyOperations());

            await WritePhaseAsync("Leave a clean checkout unchanged until runtime refresh is enabled")
                .ConfigureAwait(false);
            var refreshRevision = await CommitMarkerAsync(
                RefreshMarker,
                "Change marker for runtime refresh",
                cancellationToken).ConfigureAwait(false);
            DeleteGitProxyLog();
            await AssertResourceMarkerAsync(readOnlyEnvironment, InitializedMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                initializedRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "Default run moved a clean checkout without refresh being enabled.");
            AssertNoRepositoryMutation(ReadGitProxyOperations());

            await WritePhaseAsync("Fast-forward a clean checkout with opt-in runtime refresh")
                .ConfigureAwait(false);
            DeleteGitProxyLog();
            var refreshEnvironment = CreateAppHostEnvironment(
                _remoteRepository,
                revision: null,
                GitProxyPolicy.Refresh,
                refreshBuildRepositories: true);
            await AssertResourceMarkerAsync(refreshEnvironment, RefreshMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                refreshRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "Opt-in runtime refresh did not fast-forward the clean checkout.");
            AssertContainsNetworkUpdate(ReadGitProxyOperations());

            await WritePhaseAsync("Preserve a dirty checkout and rebuild it even with refresh enabled")
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(remoteCheckout, "marker.txt"),
                $"{DirtyMarker}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
            var dirtyBaseRevision = await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false);
            var dirtyUpstreamRevision = await CommitMarkerAsync(
                DirtyUpstreamMarker,
                "Change marker behind a dirty checkout",
                cancellationToken).ConfigureAwait(false);
            DeleteGitProxyLog();
            await AssertResourceMarkerAsync(refreshEnvironment, DirtyMarker, cancellationToken)
                .ConfigureAwait(false);
            AssertEqual(
                dirtyBaseRevision,
                await GetRevisionAsync(remoteCheckout, cancellationToken).ConfigureAwait(false),
                "Runtime refresh moved a dirty checkout.");
            AssertEqual(
                dirtyUpstreamRevision,
                await GetRevisionAsync(_resourceRepository, cancellationToken).ConfigureAwait(false),
                "The producer repository did not retain its new upstream revision.");
            AssertNotEmpty(
                await RunGitOutputAsync(
                    remoteCheckout,
                    ["status", "--porcelain", "--untracked-files=normal"],
                    cancellationToken).ConfigureAwait(false),
                "The dirty checkout was unexpectedly cleaned.");
            AssertNoNetworkUpdate(ReadGitProxyOperations());
        }

        private async Task CreateFixtureAsync(CancellationToken cancellationToken)
        {
            var extensionRoot = Path.Combine(repositoryRoot, ExtensionRelativePath);
            Directory.CreateDirectory(_packageFeed);
            ConfigureContainerRuntimeProxy();
            await RunRequiredAsync(
                new ProcessInvocation(
                    "dotnet",
                    [
                        "pack",
                        Path.Combine(
                            extensionRoot,
                            "src",
                            ExtensionDirectoryName,
                            $"{ExtensionDirectoryName}.csproj"),
                        "--configuration", "Release",
                        "--no-build",
                        "--no-restore",
                        "--disable-build-servers",
                        "--output", _packageFeed,
                        $"-p:PackageVersion={PackageVersion}"
                    ],
                    repositoryRoot),
                "Pack the modular AppHosts library",
                cancellationToken).ConfigureAwait(false);
            await RunRequiredAsync(
                new ProcessInvocation(
                    "dotnet",
                    [
                        "pack",
                        Path.Combine(
                            extensionRoot,
                            "samples",
                            "MultiRepoE2E",
                            "Spire.ModuleContract",
                            "Spire.ModuleContract.csproj"),
                        "--configuration", "Release",
                        "--no-build",
                        "--no-restore",
                        "--disable-build-servers",
                        "--output", _packageFeed,
                        $"-p:PackageVersion={PackageVersion}"
                    ],
                    repositoryRoot),
                "Pack the module contract",
                cancellationToken).ConfigureAwait(false);

            await TrackedRepositoryFixture.CopyAsync(
                _process,
                repositoryRoot,
                _consumerRepository,
                FixturePathSpecs,
                cancellationToken).ConfigureAwait(false);
            var consumerExtensionRoot = Path.Combine(_consumerRepository, ExtensionRelativePath);
            Directory.Move(
                Path.Combine(consumerExtensionRoot, "samples", "MultiRepoE2E", "Spire.ModuleContract"),
                _contractRepository);
            Directory.Move(
                Path.Combine(consumerExtensionRoot, "samples", "MultiRepoE2E", "ResourceBuildRepository"),
                _resourceRepository);

            await InitializeRepositoryAsync(_consumerRepository, cancellationToken).ConfigureAwait(false);
            await InitializeRepositoryAsync(_contractRepository, cancellationToken).ConfigureAwait(false);
            await InitializeRepositoryAsync(_resourceRepository, cancellationToken).ConfigureAwait(false);
            _pinnedRevision = await GetRevisionAsync(_resourceRepository, cancellationToken).ConfigureAwait(false);
            _latestRevision = await CommitMarkerAsync(
                LatestMarker,
                "Change marker after pinned revision",
                cancellationToken).ConfigureAwait(false);
            _remoteRepository = "https://example.invalid/modular/resource-build.git";
            _appHost = Path.Combine(
                consumerExtensionRoot,
                "samples",
                "MultiRepoE2E",
                "Spire.Consumer.AppHost",
                "Spire.Consumer.AppHost.csproj");

            await RunRequiredAsync(
                new ProcessInvocation(
                    "dotnet",
                    ["restore", _appHost],
                    _consumerRepository,
                    CreatePackageEnvironment()),
                "Restore the isolated package consumer",
                cancellationToken).ConfigureAwait(false);
            await RunRequiredAsync(
                new ProcessInvocation(
                    "dotnet",
                    ["build", _appHost, "--configuration", "Release", "--no-restore"],
                    _consumerRepository,
                    CreatePackageEnvironment()),
                "Build the isolated package consumer",
                cancellationToken).ConfigureAwait(false);
        }

        private async Task InitializeRepositoryAsync(string path, CancellationToken cancellationToken)
        {
            await RunGitAsync(path, ["init", "--initial-branch", "main"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(path, ["config", "user.name", "MultiRepo E2E"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(path, ["config", "user.email", "multi-repo@example.invalid"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(path, ["add", "."], cancellationToken).ConfigureAwait(false);
            await RunGitAsync(path, ["commit", "-m", "Create isolated E2E repository"], cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<string> CommitMarkerAsync(
            string marker,
            string message,
            CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_resourceRepository, "marker.txt"),
                $"{marker}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
            await RunGitAsync(_resourceRepository, ["add", "marker.txt"], cancellationToken)
                .ConfigureAwait(false);
            await RunGitAsync(_resourceRepository, ["commit", "-m", message], cancellationToken)
                .ConfigureAwait(false);
            return await GetRevisionAsync(_resourceRepository, cancellationToken).ConfigureAwait(false);
        }

        private async Task AssertStartRequiresInitializationAsync(
            IReadOnlyDictionary<string, string?> environment,
            CancellationToken cancellationToken)
        {
            var started = false;
            try
            {
                var start = await RunAspireAsync(
                    [
                        "start",
                        "--apphost", _appHost,
                        "--isolated",
                        "--format", "Json",
                        "--non-interactive"
                    ],
                    environment,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(start, "aspire start before repository initialization");
                started = true;

                var wait = await RunAspireAsync(
                    [
                        "wait", ResourceName,
                        "--apphost", _appHost,
                        "--timeout", "30",
                        "--non-interactive"
                    ],
                    environment,
                    cancellationToken).ConfigureAwait(false);
                if (wait.IsSuccess)
                {
                    throw new InvalidOperationException(
                        "The repository-backed resource started before repository initialization.");
                }

                var logs = await RunAspireAsync(
                    [
                        "logs", ResourceName,
                        "--apphost", _appHost,
                        "--format", "Json",
                        "--non-interactive"
                    ],
                    environment,
                    cancellationToken).ConfigureAwait(false);
                var failureOutput = $"{wait.CombinedOutput}{Environment.NewLine}{logs.CombinedOutput}";

                AssertContains(
                    failureOutput,
                    "aspire do initialize --apphost",
                    "The resource preflight failure did not contain the AppHost-aware initialization command.");
                AssertContains(
                    RemoveWhitespace(failureOutput),
                    RemoveWhitespace(Path.GetDirectoryName(_appHost)!),
                    "The resource preflight failure did not identify the AppHost to initialize.");
                AssertContains(
                    failureOutput,
                    "--non-interactive",
                    "The resource preflight failure did not provide a non-interactive initialization command.");
            }
            finally
            {
                if (started)
                {
                    await StopAppHostAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task<ProcessResult> RunInitializeAsync(
            IReadOnlyDictionary<string, string?> environment,
            CancellationToken cancellationToken)
        {
            var result = await RunAspireAsync(
                [
                    "do", "initialize",
                    "--apphost", _appHost,
                    "--pipeline-log-level", "trace",
                    "--non-interactive"
                ],
                environment,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "aspire do initialize");
            await Console.Out.WriteLineAsync(result.CombinedOutput.Trim()).ConfigureAwait(false);
            return result;
        }

        private async Task AssertResourceMarkerAsync(
            IReadOnlyDictionary<string, string?> environment,
            string expectedMarker,
            CancellationToken cancellationToken)
        {
            var resource = await AspireTestingAppHost.ReadResourceAsync(
                _appHost,
                ResourceName,
                environment,
                cancellationToken).ConfigureAwait(false);
            AssertEqual(
                "healthy-from-separate-build-repository",
                resource.Health,
                "The running image does not contain the producer-owned health marker.");
            AssertEqual(expectedMarker, resource.Marker, "The running image contains the wrong marker.");
        }

        private async Task StopAppHostAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var result = await RunAspireAsync(
                    ["stop", "--apphost", _appHost, "--non-interactive"],
                    environment: null,
                    cleanup.Token).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    await Console.Error.WriteLineAsync(
                        $"Emergency Aspire stop failed:{Environment.NewLine}{result.CombinedOutput}")
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync(
                    "Emergency Aspire stop exceeded its independent 30-second cleanup timeout.")
                    .ConfigureAwait(false);
            }
        }

        private Task<ProcessResult> RunAspireAsync(
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken)
        {
            return _process.RunAsync(
                new ProcessInvocation(
                    _aspire.FileName,
                    [.. _aspire.PrefixArguments, .. arguments],
                    _consumerRepository,
                    environment),
                cancellationToken);
        }

        private async Task RunRequiredAsync(
            ProcessInvocation invocation,
            string operation,
            CancellationToken cancellationToken)
        {
            var result = await _process.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, operation);
        }

        private async Task RunGitAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var result = await _process.RunAsync(
                new ProcessInvocation("git", arguments, workingDirectory),
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"git {string.Join(' ', arguments)}");
        }

        private async Task<string> RunGitOutputAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var result = await _process.RunAsync(
                new ProcessInvocation("git", arguments, workingDirectory),
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"git {string.Join(' ', arguments)}");
            return result.StandardOutput.Trim();
        }

        private Task<string> GetRevisionAsync(string repository, CancellationToken cancellationToken) =>
            RunGitOutputAsync(repository, ["rev-parse", "HEAD"], cancellationToken);

        private Dictionary<string, string?> CreatePackageEnvironment() => new(StringComparer.Ordinal)
        {
            ["SpireContractPackageVersion"] = PackageVersion,
            ["RestoreAdditionalProjectSources"] = _packageFeed,
            ["NUGET_PACKAGES"] = _nugetPackages,
            ["DOTNET_ENVIRONMENT"] = "MultiRepoE2E"
        };

        private void ConfigureContainerRuntimeProxy()
        {
            if (options.ContainerRuntime is not { } containerRuntime)
            {
                return;
            }

            _realContainerRuntime = FindExecutableOnPath(containerRuntime);
            Directory.CreateDirectory(_runtimeProxyLogDirectory);
            Directory.CreateDirectory(_runtimeShimDirectory);
            var shimExecutable = Path.Combine(
                _runtimeShimDirectory,
                OperatingSystem.IsWindows() ? $"{containerRuntime}.exe" : containerRuntime);
            File.Copy(_driverExecutable, shimExecutable);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(shimExecutable, File.GetUnixFileMode(_driverExecutable));
            }

            var driverName = typeof(Program).Assembly.GetName().Name
                ?? throw new InvalidOperationException("The E2E driver assembly has no name.");
            foreach (var extension in new[] { ".deps.json", ".dll", ".runtimeconfig.json" })
            {
                var source = Path.Combine(AppContext.BaseDirectory, $"{driverName}{extension}");
                File.Copy(source, Path.Combine(_runtimeShimDirectory, Path.GetFileName(source)));
            }
        }

        private Dictionary<string, string?> CreateAppHostEnvironment(
            string buildRepository,
            string? revision,
            GitProxyPolicy proxyPolicy,
            bool refreshBuildRepositories = false,
            string? checkoutDirectoryName = null)
        {
            var environment = CreatePackageEnvironment();
            var section = "Aspire__ModularAppHosts";
            var module = $"{section}__Modules__{ModuleName}";
            var container = $"{module}__Containers__{ResourceName}";
            environment[$"{module}__Repository"] = _contractRepository;
            environment[$"{container}__BuildRepository"] = buildRepository;
            environment[$"{container}__BuildRepositoryRevision"] = revision;
            environment[$"{container}__CheckoutDirectoryName"] = checkoutDirectoryName;
            environment[$"{section}__GitExecutablePath"] = _driverExecutable;
            environment[$"{section}__GitHubCliPath"] = _driverExecutable;
            environment[$"{section}__RefreshBuildRepositoriesOnRun"] = refreshBuildRepositories.ToString();
            environment[GitProxy.LogEnvironmentVariable] = _gitProxyLog;
            environment[GitProxy.PolicyEnvironmentVariable] = proxyPolicy.ToString();
            environment[GitProxy.RealGitEnvironmentVariable] = "git";
            environment[GitProxy.RemoteRepositoryEnvironmentVariable] = _remoteRepository;
            environment[GitProxy.SourceRepositoryEnvironmentVariable] = _resourceRepository;
            if (!string.IsNullOrWhiteSpace(options.ContainerRuntime))
            {
                environment["ASPIRE_CONTAINER_RUNTIME"] = options.ContainerRuntime;
                environment["PATH"] = string.Join(
                    Path.PathSeparator,
                    _runtimeShimDirectory,
                    Environment.GetEnvironmentVariable("PATH"));
                environment[RuntimeProxy.LogDirectoryEnvironmentVariable] = _runtimeProxyLogDirectory;
                environment[RuntimeProxy.RealExecutableEnvironmentVariable] = _realContainerRuntime;
                environment[RuntimeProxy.RuntimeEnvironmentVariable] = options.ContainerRuntime;
            }

            return environment;
        }

        private static string FindExecutableOnPath(string executable)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var extensions = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                : [string.Empty];
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var unquotedDirectory = directory.Trim().Trim('"');
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(unquotedDirectory, executable + extension.ToLowerInvariant());
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }

            throw new InvalidOperationException(
                $"Configured container runtime '{executable}' was not found on PATH.");
        }

        private HashSet<string> GetSiblingGitDirectories()
        {
            return Directory.EnumerateDirectories(temporaryRoot)
                .Where(path => Directory.Exists(Path.Combine(path, ".git")) ||
                    File.Exists(Path.Combine(path, ".git")))
                .Select(Path.GetFullPath)
                .ToHashSet(PathComparer);
        }

        private string GetNewSiblingGitDirectory(IReadOnlySet<string> before)
        {
            var added = GetSiblingGitDirectories()
                .Where(path => !before.Contains(path))
                .ToArray();
            if (added.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one initializer-owned direct sibling, but found {added.Length}: " +
                    string.Join(", ", added));
            }

            return added[0];
        }

        private void AssertRepositoryState(
            string repositoryPath,
            string ownership,
            string? normalizedRepository = "example.invalid/modular/resource-build")
        {
            var appHostIdentity = Path.GetFullPath(_appHost);
            var appHostHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(appHostIdentity.ToLowerInvariant())));
            var stateFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".aspire",
                "deployments",
                appHostHash,
                "modular-apphosts.json");
            if (!File.Exists(stateFile))
            {
                throw new InvalidOperationException("Initialization did not write repository state.");
            }

            var state = File.ReadAllText(stateFile);
            AssertContains(
                state,
                "\"repositories\"",
                "Initialization did not write the repository-state document.");
            if (normalizedRepository is not null)
            {
                AssertContains(
                    state,
                    normalizedRepository,
                    "Initialization state did not retain the normalized repository identity.");
            }
            using var document = JsonDocument.Parse(state);
            var matchingState = document.RootElement
                .GetProperty("repositories")
                .EnumerateObject()
                .Select(property => property.Value)
                .SingleOrDefault(value =>
                    value.TryGetProperty("destination", out var destination) &&
                    PathComparer.Equals(
                        Path.GetFullPath(destination.GetString()!),
                        Path.GetFullPath(repositoryPath)));
            if (matchingState.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException(
                    $"Initialization state did not contain checkout '{repositoryPath}'.");
            }

            AssertEqual(
                ownership,
                matchingState.GetProperty("ownership").GetString(),
                $"Initialization state recorded the wrong ownership for '{repositoryPath}'.");

            var legacyEnvironmentStateFile = Path.Combine(
                Path.GetDirectoryName(stateFile)!,
                "multirepoe2e.json");
            if (File.Exists(legacyEnvironmentStateFile) &&
                File.ReadAllText(legacyEnvironmentStateFile).Contains(
                    "modular-apphosts:repositories:",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Initialization still wrote repository state to the environment deployment-state file.");
            }
        }

        private IReadOnlyList<GitProxyOperation> ReadGitProxyOperations()
        {
            if (!File.Exists(_gitProxyLog))
            {
                return [];
            }

            return File.ReadLines(_gitProxyLog)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<GitProxyOperation>(line)
                    ?? throw new InvalidDataException("The Git proxy wrote an empty operation."))
                .ToArray();
        }

        private void DeleteGitProxyLog()
        {
            File.Delete(_gitProxyLog);
        }

        private IReadOnlyList<RuntimeProxyOperation> ReadRuntimeProxyOperations()
        {
            if (!Directory.Exists(_runtimeProxyLogDirectory))
            {
                return [];
            }

            return Directory.EnumerateFiles(_runtimeProxyLogDirectory, "*.json")
                .Order(StringComparer.Ordinal)
                .Select(path => JsonSerializer.Deserialize<RuntimeProxyOperation>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("The container-runtime proxy wrote an empty operation."))
                .ToArray();
        }

        private void DeleteRuntimeProxyLog()
        {
            if (!Directory.Exists(_runtimeProxyLogDirectory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(_runtimeProxyLogDirectory, "*.json"))
            {
                File.Delete(path);
            }
        }

        private void AssertConfiguredContainerRuntimeUsed(IEnumerable<RuntimeProxyOperation> operations)
        {
            if (options.ContainerRuntime is not { } expectedRuntime || _realContainerRuntime is null)
            {
                return;
            }

            var captured = operations.ToArray();
            if (captured.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Configured container runtime '{expectedRuntime}' was never invoked.");
            }

            foreach (var operation in captured)
            {
                AssertEqual(
                    expectedRuntime,
                    operation.Runtime,
                    "The container-runtime proxy observed the wrong selected executable.");
                AssertEqual(
                    _realContainerRuntime,
                    operation.RealExecutable,
                    "The container-runtime proxy did not forward to the configured runtime executable.");
            }

            if (!captured.Any(operation =>
                    operation.Arguments is ["build", .., "--tag", var imageReference, _] &&
                    imageReference.StartsWith("multi-repo-e2e-resource:", StringComparison.Ordinal)) ||
                !captured.Any(operation =>
                    operation.Arguments is ["tag", var source, "multi-repo-e2e-resource:aspire-run"] &&
                    source.StartsWith("multi-repo-e2e-resource:", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"The image evaluator did not build and tag its canonical image with configured runtime '{expectedRuntime}'.");
            }
        }

        private static void AssertInitializationLifecycle(
            string output,
            string repositoryPath,
            params string[] operations)
        {
            AssertContains(output, "Initializing repository", "Initialization did not log its start.");
            AssertContains(output, "Initialized repository", "Initialization did not log its completion.");
            AssertContains(
                RemoveWhitespace(output),
                RemoveWhitespace(repositoryPath),
                "Initialization lifecycle output did not include its structured repository path context.");
            AssertContains(
                output,
                ModuleName,
                "Initialization lifecycle output did not include its structured module context.");
            foreach (var operation in operations)
            {
                AssertContains(
                    output,
                    $"Repository operation {operation} started",
                    $"Initialization did not log the start of repository operation '{operation}'.");
                AssertContains(
                    output,
                    $"Repository operation {operation} completed",
                    $"Initialization did not log the completion of repository operation '{operation}'.");
            }
        }

        private static void AssertNoRepositoryMutation(IEnumerable<GitProxyOperation> operations)
        {
            var mutation = operations.FirstOrDefault(operation =>
                !ReadOnlyGitCommandPolicy.IsAllowed(operation.Arguments));
            if (mutation is not null)
            {
                throw new InvalidOperationException(
                    $"Default run invoked prohibited Git operation '{mutation.Operation}'.");
            }
        }

        private static void AssertContainsNetworkUpdate(IEnumerable<GitProxyOperation> operations)
        {
            if (!operations.Any(operation => operation.Operation is "fetch" or "pull"))
            {
                throw new InvalidOperationException(
                    "Opt-in runtime refresh did not invoke a Git fetch or pull operation.");
            }
        }

        private static void AssertNoNetworkUpdate(IEnumerable<GitProxyOperation> operations)
        {
            if (operations.Any(operation => operation.Operation is "fetch" or "pull"))
            {
                throw new InvalidOperationException("Runtime refresh fetched or pulled a dirty checkout.");
            }
        }

        private static void EnsureSuccess(ProcessResult result, string operation)
        {
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"{operation} failed with exit code {result.ExitCode}:{Environment.NewLine}" +
                    result.CombinedOutput);
            }
        }

        private static async Task WritePhaseAsync(string phase)
        {
            await Console.Out.WriteLineAsync($"[multi-repo-e2e] {phase}").ConfigureAwait(false);
        }

        private static string GetDriverExecutable()
        {
            var executable = Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows()
                    ? "Spire.MultiRepo.E2E.Support.exe"
                    : "Spire.MultiRepo.E2E.Support");
            if (!File.Exists(executable))
            {
                throw new InvalidOperationException($"The Git proxy executable '{executable}' does not exist.");
            }

            return executable;
        }

    }
}
