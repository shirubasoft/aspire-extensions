# Remote repository initialization

This sample imports `notification-service` from the unpinned [`shirubasoft/spire-external-repo-sample`](https://github.com/shirubasoft/spire-external-repo-sample) repository.

Requirements: .NET 10 SDK, Aspire CLI 13.4.6 or later, and GitHub CLI. Git may already be installed or can be installed by the initialization pipeline.

From this directory, start the AppHost before initializing it:

```bash
aspire run
```

Startup reports the missing checkout and prints an exact recovery command. Run it, then start the AppHost again:

```bash
aspire do initialize --apphost . --non-interactive
aspire run
```

Initialization verifies Git, clones the remote to the sibling `<workspace>/spire-external-repo-sample`, and records it as initializer-managed. A matching checkout that already exists is adopted and never updated by initialization. Later initialization runs can fast-forward a clean initializer-managed checkout.

The `notification-service` resource becomes healthy and exposes `/notifications` through the dashboard.

## What the sample demonstrates

Git is a required-tool resource with a live health check. The service waits for it, while the dashboard exposes its download page and an **Install** command. The same platform-specific installer runs before repository initialization when Git is missing.

The imported project also supports developer-local project/container switching. Its checked-in project mode works after a fresh clone; see [developer-local mode switching](../../docs/modules.md#developer-local-mode-switching) to try its generated `notification-service` steps.
