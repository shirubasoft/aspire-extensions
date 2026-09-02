# Multi-repository E2E tests

This xUnit project owns the MultiRepo sample's lifecycle and isolation validation. Both the checked-in
AppHosts and the dynamically built isolated consumer run through `Aspire.Hosting.Testing`, which
supplies resource health notifications, endpoint discovery, HTTP clients, random host ports, and
async cleanup.

The adjacent `Spire.MultiRepo.E2E.Support` executable handles the contracts that still require the
real Aspire CLI: explicit initialization, fail-fast remediation commands, process isolation, and
container-runtime selection. Normal isolated runs use the non-generic testing builder against the
built fixture assembly. The support components are separated by responsibility:

- `MultiRepositoryScenario` coordinates the behavior under test.
- `AspireTestingAppHost` owns isolated AppHost lifecycle, health waits, endpoint discovery, clients,
  and environment restoration.
- `TrackedRepositoryFixture` copies the root build inputs and this extension's tracked or unignored
  untracked files returned by `git ls-files`; it never recursively copies a developer worktree.
- `GitProxy` records each invocation and rejects every command shape outside the exact read-only
  allowlist during normal run.
- `RuntimeProxy` proves the executable selected through Aspire's container runtime resolver.
- `ProcessExecutor` uses CliWrap with bounded cancellation and process-tree termination.
- `FailureBundle` and cleanup/assertion components retain complete diagnostics.

The suite packs the module contract, creates isolated consumer and producer repositories, and then
verifies managed checkout ownership, pinned-revision isolation, fail-fast recovery commands,
read-only normal-run Git access, opt-in clean refresh, dirty-worktree protection, selected container
runtime behavior, and tagged-image fallback without a build checkout. A separate CI job validates
the producer-to-consumer `images publish` / `images apply` handoff against a local registry.

Run the full Docker-backed suite from the repository root:

```bash
dotnet tool restore
MULTI_REPO_E2E=true ASPIRE_CONTAINER_RUNTIME=docker \
  dotnet test extensions/Shirubasoft.Aspire.Extensions.Multirepo/tests/Spire.MultiRepo.E2E.Tests/Spire.MultiRepo.E2E.Tests.csproj \
  --configuration Release
```

Use `ASPIRE_CONTAINER_RUNTIME=podman` for local Podman coverage. CI validates Docker end to end. On
failure, the support executable writes a bundle under `artifacts/e2e/multi-repo-failure`;
CI uploads that directory without retaining the temporary repositories.
