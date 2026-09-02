# Shirubasoft.Aspire.Extensions.Multirepo.Tool

`modular-apphosts` publishes producer-owned module images, records their exact registry identities in a versioned workflow document, applies that document to a consumer command, and can dispatch and wait for the consumer's GitHub Actions workflow.

Requirements:

- .NET 10 SDK to install the tool.
- Aspire CLI 13.4.6 or later for `images publish`.
- GitHub CLI 2.87.0 or later for `workflow dispatch`.
- Registry authentication available to Aspire's selected Docker or Podman runtime.
- The same tool and `Shirubasoft.Aspire.Extensions.Multirepo` release in both repositories.

Pin the tool in each repository:

```bash
dotnet new tool-manifest
dotnet tool install Shirubasoft.Aspire.Extensions.Multirepo.Tool --version <VERSION>
git add .config/dotnet-tools.json
```

After `dotnet tool restore`, invoke it with `dotnet tool run modular-apphosts -- ...`. Run any command with `--help` for its complete option list.

## Producer: publish images

Publish an explicit producer selection and write its workflow document:

```bash
dotnet tool run modular-apphosts -- images publish \
  --apphost src/Producer.AppHost \
  --module orders \
  --resource api \
  --tag "$GITHUB_SHA" \
  --output module-image-workflow.json
```

Repeat `--module` and `--resource`, or use `--all`. `--tag` overrides each AppHost-resolved tag; optional entries in `--resource-tags '{"orders/worker":"worker-candidate"}'` win last. The command runs the selected `workflow-images` graph once, pushes its images, and writes the resolved push targets. Use `--aspire-path` only when the Aspire executable is not named `aspire`.

The output path defaults to `module-image-workflow.json`. In GitHub Actions the command also emits:

| Output | Value |
| --- | --- |
| `workflow-document` | Compact JSON for a reusable workflow input. |
| `workflow-document-path` | Absolute saved path for dispatch. |

## Consumer: apply images

Run the consumer AppHost or E2E command through `apply`:

```bash
dotnet tool run modular-apphosts -- images apply \
  --json "$IMAGE_WORKFLOW" \
  -- \
  dotnet test tests/Consumer.E2E.Tests/Consumer.E2E.Tests.csproj --configuration Release
```

Specify exactly one of `--json` or `--file`; place the child command after `--`. `apply` launches it directly with normal `Aspire:ModularAppHosts:Modules:<module>:<collection>:<resource>` configuration. The working directory, standard streams, and child exit code are preserved.

Listed projects use container mode. Listed resources disable local publishing, clear conflicting tag/digest values, and use `ImagePullPolicy.Always`. Optional consumer `--tag <tag>` overrides every received identity and `--resource-tags <json>` wins last; both select existing registry content and do not create tags.

## Producer: dispatch and wait

Create and follow one specific consumer workflow run:

```bash
dotnet tool run modular-apphosts -- workflow dispatch \
  --repository your-org/consumer \
  --workflow external-e2e.yml \
  --ref main \
  --workflow-document module-image-workflow.json \
  --input repo-a-ref=main
```

| Option | Meaning |
| --- | --- |
| `--repository` | Required `[HOST/]OWNER/REPO` target. |
| `--workflow` | Required workflow file, ID, or name. |
| `--workflow-document` | Required workflow document file. |
| `--ref` | Ref containing the workflow; defaults to the target's default branch. |
| `--workflow-document-input` | Input receiving the document; defaults to `image-workflow`. |
| `--input` | Additional `<name>=<value>` input; repeat as needed. |
| `--gh-path` | GitHub CLI executable; defaults to configured `GitHubCliPath`, then `gh`. |

The command sends the complete payload to `gh workflow run --json`, reads the returned run URL, and streams `gh run watch --compact --exit-status`. It emits `run-id` and `run-url` in GitHub Actions. Authentication belongs to GitHub CLI; use `GH_TOKEN` or an existing authenticated session.

## Module image workflow document contract

The document is strict JSON. Unknown fields are rejected, identities are case-insensitively unique, and each image contains exactly one tag or supported SHA-256 digest:

```json
{
  "schemaVersion": 1,
  "images": [
    {
      "module": "orders",
      "resource": "api",
      "resourceKind": "project",
      "registry": "ghcr.io",
      "repository": "your-org/orders-api",
      "tag": "0123456789abcdef",
      "digest": null
    }
  ]
}
```

The compact document and complete dispatch payload are each limited to 65,535 characters. Module and resource names cannot contain identity or configuration separators.

## Configuration and exit codes

The tool reads .NET configuration. Set `Aspire:ModularAppHosts:GitHubCliPath` (environment form `Aspire__ModularAppHosts__GitHubCliPath`) to choose the default GitHub CLI executable.

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Operational or watched-workflow failure. |
| `2` | Invalid command input, document, selection, tag map, or payload. |
| `130` | Interrupted. |

The child command launched by `images apply` and GitHub CLI can return additional exit statuses unchanged.

## Security notes

- Do not publish images from untrusted forks with a privileged registry token.
- The producer needs write access and the consumer needs read access to every selected image.
- A repository's built-in `GITHUB_TOKEN` normally cannot dispatch another repository; provide suitable `GH_TOKEN` credentials without placing them in arguments.
- `gh run watch` cannot use a fine-grained PAT because it cannot grant the required Checks read permission.
- Treat workflow documents as deployment inputs. The tool validates their shape, not the caller's authorization to run an image.

See the [cross-repository guide](https://github.com/Shirubasoft/aspire-extensions/blob/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/docs/external-e2e-workflows.md) and [checked-in workflows](https://github.com/Shirubasoft/aspire-extensions/tree/main/extensions/Shirubasoft.Aspire.Extensions.Multirepo/docs/workflows).
