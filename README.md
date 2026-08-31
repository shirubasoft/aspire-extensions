# Aspire extensions

This repository contains independently versioned Aspire extensions. Each package owns its source, tests, runnable sample, documentation, semantic-release configuration, and workflow entry points under one folder in `extensions/`.

## Extensions

| Package | Purpose | Documentation |
| --- | --- | --- |
| `Shirubasoft.Aspire.Extensions.Kafka` | Confluent Schema Registry and idempotent Kafka topic resources. | [Kafka extension](extensions/Shirubasoft.Aspire.Extensions.Kafka/README.md) |

## Build

Install the .NET SDK pinned in `global.json`, then run:

```bash
./build.sh
```

Build one extension with:

```bash
./build.sh --extension extensions/Shirubasoft.Aspire.Extensions.Kafka
```

The Windows entry point is `./build.ps1`. Both entry points restore tools, verify formatting, build with warnings as errors, run unit and package contract tests, and create `.nupkg` and `.snupkg` files under `artifacts/<package-id>/`.

## Quality and release model

The repository uses shared reusable workflows for CI, stable releases, and manually confirmed prereleases. Thin package workflows supply the package path, artifact name, release configuration, and tag prefix.

Each package versions independently from Conventional Commits that touch its own folder or shared package inputs. For example, Kafka releases use tags such as `kafka-v1.2.3`. A `feat` commit produces a minor release, a `fix` commit produces a patch release, and a breaking change produces a major release.

CI runs on Linux and Windows. Linux also measures method-level CRAP scores from OpenCover data and fails unless every method scores strictly below 5. After a successful `main` build, semantic-release publishes the package and symbols to NuGet.org with the `NUGET_API_KEY` organization secret, creates the package tag, and attaches both files to a GitHub release.

## Add an extension

Use the repository skill in `.agents/skills/add-aspire-extension/`. It treats an existing local extension as the template, keeps package-specific files together, and reuses the root build, quality, and release tooling.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the repository contract.
