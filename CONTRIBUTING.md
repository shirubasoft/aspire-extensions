# Contributing

## Repository contract

Keep every extension self-contained under `extensions/Shirubasoft.Aspire.Extensions.<Name>/`. Do not add extension-specific branches to root build scripts or shared workflow implementations. Supply package differences through paths, manifests, release configuration, and workflow inputs.

Use the repo-scoped `add-aspire-extension` skill when creating a package. Start from the nearest existing local extension, copy only source-controlled inputs, and adapt all package names, tags, documentation, tests, and workflow filters.

## Development

Use Conventional Commits. Add `BREAKING CHANGE:` to the commit body when a public API change is incompatible. Do not preserve obsolete APIs unless a compatibility period is an explicit requirement.

Keep AppHost APIs declarative. Resource builders should describe resources, references, dependencies, and lifecycle behavior. Put operational logic behind a resource-owned implementation and cover it with unit tests.

Before submitting a change, run:

```bash
./build.sh --extension extensions/Shirubasoft.Aspire.Extensions.<Name>
```

CI also collects OpenCover data and runs the shared CRAP tool. Every measured method must score below 5.

## Package requirements

Every extension must include:

- a NuGet library with XML documentation, embedded sources, Source Link, repository metadata, a package README, and `.snupkg` symbols;
- unit tests and package contract tests that build a consumer against the packed artifact;
- a runnable Aspire AppHost sample;
- a package README and an initially empty `AGENTS.md`;
- an independent semantic-release configuration and path-filtered CI, publish, and prerelease workflow wrappers.

An extension that publishes related packages lists their project paths in `pack-projects.txt`. Its release configuration lists every package ID and whether the package has symbols. When production code is covered by more than one test project, `coverage-projects.txt` lists each project and its Coverlet include filter. The shared build and CI scripts read these manifests; a single-package extension does not need them.
