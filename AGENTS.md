Use `.agents/skills/add-aspire-extension/SKILL.md` whenever you add an Aspire extension.

Keep every extension under `extensions/Shirubasoft.Aspire.Extensions.<Name>`. The extension folder owns its source, unit tests, package contract tests, runnable sample, README, release configuration, solution, and an initially empty `AGENTS.md` for extension-specific instructions.

Place every extension's public API in namespaces already defined by the Aspire assemblies that the extension builds on. Add an architectural test that rejects exported package types from other namespaces.

Reuse the root build entry points, CRAP score tool, release tooling, and reusable GitHub workflows. Do not copy their implementations into extension folders.

Public packages target .NET 10 and the Aspire versions pinned in `Directory.Packages.props`. Pin the AppHost SDK in `global.json`. Enable nullable reference types, XML documentation, package validation, Source Link, symbols, and the public API analyzer.

Never synchronously block on asynchronous work. Propagate `Task`, `ValueTask`, and cancellation tokens to callers.

Backward compatibility is not a constraint before the first stable release. Mark later breaking changes with Conventional Commits syntax and a `BREAKING CHANGE:` footer.

Use `fix:` or `perf:` for patch releases, `feat:` for minor releases, and `!` or `BREAKING CHANGE:` for major releases. Release tooling filters commits by extension path, so keep each commit focused.

Every public feature needs tests and a runnable sample. CI must build the sample, inspect the packed `.nupkg` and `.snupkg`, and keep every production method's CRAP score below 5.

Run container-backed tests with Docker or Podman. Check which runtime is available before choosing one.
