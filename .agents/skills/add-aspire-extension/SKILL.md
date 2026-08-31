---
name: add-aspire-extension
description: Add or scaffold a new independently released Shirubasoft Aspire extension in this aspire-extensions repository. Use when creating a package under extensions/, adding another Aspire integration, or setting up its source, tests, sample, packaging, CI, and release workflows. Copy and adapt the closest existing local extension instead of importing another repository or inventing a parallel structure.
---

# Add an Aspire extension

Create the extension as one independently owned folder while reusing the repository build, quality, packaging, and release infrastructure.

## Establish the local precedent

1. Read the root `AGENTS.md` and the target extension's `AGENTS.md` if it already exists.
2. Inspect `extensions/` and choose the existing extension closest in API and resource lifecycle behavior.
3. Read that extension's solution, source project, tests, package tests, sample, README, release config, and three workflow wrappers before editing.
4. Inspect shared files only when the new extension needs a capability that the existing extension does not use.

Do not copy `bin/`, `obj/`, `artifacts/`, coverage reports, secrets, user settings, or another repository's generated state.

## Create the package folder

Use `extensions/Shirubasoft.Aspire.Extensions.<Name>/` and preserve this shape:

```text
AGENTS.md
README.md
release.config.mjs
Shirubasoft.Aspire.Extensions.<Name>.slnx
samples/<Name>.AppHost/
src/Directory.Build.props
src/Shirubasoft.Aspire.Extensions.<Name>/
tests/Shirubasoft.Aspire.Extensions.<Name>.Tests/
tests/Shirubasoft.Aspire.Extensions.<Name>.PackageTests/
```

Create `AGENTS.md` as an empty file. Add package-specific instructions only after the package develops a real local convention.

Copy the closest extension, then replace every package ID, namespace, sample name, description, artifact name, tag prefix, and workflow display name. Search the new folder and wrappers for stale names before continuing.

## Reuse shared infrastructure

- Put dependency versions in `Directory.Packages.props`; package projects use versionless `PackageReference` items.
- Inherit root compilation, symbols, Source Link, warnings, and repository metadata settings.
- Keep `build.sh`, `build.ps1`, `eng/`, and `tools/CrapScore` generic. Add parameters or conventions when reuse needs improvement; do not add package-name branches.
- Add the extension solution to `Aspire.Extensions.slnx` and its package to the root README catalog.
- Extend the shared workflow-lint file list with all new wrapper workflows.

## Implement and prove the extension

Keep AppHost composition declarative. Model resources and relationships in public types and builder methods. Put lifecycle or external-client work behind small internal interfaces. Make registration and provisioning safe to retry when the external system permits it.

Add tests for:

- resource annotations, endpoints, health checks, parent or dependency relationships, references, and wait behavior;
- success, retry-safe existing state, invalid input, and external failure paths;
- the packed `.nupkg` and `.snupkg`, README, XML documentation, repository metadata, dependencies, and a consumer build against the local package.

Add a runnable sample that demonstrates the package's main resource graph without embedding operational setup in the AppHost.

## Wire independent releases

Add `<slug>-ci.yml`, `<slug>-publish.yml`, and `<slug>-prerelease.yml` as thin callers of the reusable workflows. Use a unique artifact name and tag prefix. CI `push` and `pull_request` path filters must include:

- the complete extension folder;
- shared build and package inputs;
- shared CRAP tooling;
- shared release tooling and the relevant reusable workflow.

Create `release.config.mjs` with `createExtensionReleaseConfig`. The extension folder is the primary semantic-release scope. Shared package inputs may also trigger a release. Do not let changes to unrelated extension folders change this package's version.

## Validate

Run all of these from the repository root:

```bash
./build.sh --extension extensions/Shirubasoft.Aspire.Extensions.<Name>
npm ci --prefix .github/release
npm test --prefix .github/release
```

Collect coverage for the extension's unit-test project and run `tools/CrapScore` with `--maximum-exclusive 5`. The gate is strict: a score of 5 fails.

Validate every workflow with the pinned actionlint version. Inspect the final `.nupkg` and `.snupkg` rather than treating a successful `dotnet pack` as sufficient proof.
