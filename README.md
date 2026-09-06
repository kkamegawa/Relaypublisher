# Relaypublisher

Relaypublisher publishes winget-like YAML manifests as Microsoft Intune LOB apps from CI.

Distribution:

- NuGet global tool package id: `relaypublisher`
- Command name: `relaypublisher`
- Package version: injected by CI from Git tag `vX.Y.Z`
- Feeds: nuget.org, GitHub Packages (this repository), and Azure Artifacts
- Self-contained single-file apps for `win-x64`, `win-arm64`, and `osx-arm64` are attached to each
  GitHub release. They are neither code-signed nor notarized, so macOS shows a Gatekeeper warning.

Quick install:

```bash
dotnet tool install --global relaypublisher
```

See [doc/05-operation.md](doc/05-operation.md#0-tool-installation-and-version-control) for installing
from GitHub Packages or Azure Artifacts instead.

The repository now contains the .NET CLI foundation for the normal workflow:

- `validate` checks manifest schema rules and repository-wide identity uniqueness.
- `plan` resolves the target manifest set once and writes `manifest-list.json` for later CI jobs.
- `package` stages app files: Windows Win32 `.intunewin` packages (Windows runner required) or a
  staged, checksum-verified macOS `.pkg` (any OS).
- `publish` creates or updates Intune apps, uploads packaged content, reconciles app categories, and
  reconciles assignments.

The Japanese translation is available in [README_ja.md](README_ja.md).

## Supported Platforms

| | Windows (`win32LobApp`) | macOS `AppType: pkg` (`macOSPkgApp`, default) | macOS `AppType: lob` (`macOSLobApp`) |
|---|---|---|---|
| Graph API version | v1.0 | beta | v1.0 |
| Signing | Not required | Not required | Developer ID Installer required |
| Max package size | - | 8 GB | 2 GB |
| Icon | Optional | Optional | Required |
| `Intent: uninstall` | Supported | Not supported | Supported |
| Detection | PowerShell script or file system rule (`exists` / `version`) | `IncludedApps` (bundleId + version) | `IncludedApps` (bundleId + version) |
| Pre/post install script | Not applicable | Supported (`Scripts`, optional) | Not supported |

See [doc/01-manifest-schema.md](doc/01-manifest-schema.md) §5.3-5.4 for the full macOS manifest shape
and validation rules, and [doc/00-overview.md](doc/00-overview.md) §6.13 for the design rationale
(including why `macOSPkgApp` requires Graph beta).

## What This Repository Provides

- Centralized design decisions for Intune LOB publishing.
- YAML manifest schema and Microsoft Graph mapping.
- .NET 10 / C# CLI implementation.
- GitHub Actions and Azure Pipelines workflow examples.
- Operational and troubleshooting guidance for production use.
- APM configuration for Copilot, Claude, Codex, and Agent Skills.

## Start Here

- [doc/00-overview.md](doc/00-overview.md) - requirements, design principles, and major design decisions.
- [doc/01-manifest-schema.md](doc/01-manifest-schema.md) - YAML schema and examples.
- [doc/02-dotnet-architecture.md](doc/02-dotnet-architecture.md) - .NET solution and interface design.
- [doc/03-ci-github-actions.md](doc/03-ci-github-actions.md) - GitHub Actions workflow.
- [doc/04-ci-azure-pipelines.md](doc/04-ci-azure-pipelines.md) - Azure Pipelines workflow.
- [doc/05-operation.md](doc/05-operation.md) - operational setup and daily commands.
- [doc/06-troubleshooting.md](doc/06-troubleshooting.md) - recovery and failure handling.
- [doc/07-local-e2e.md](doc/07-local-e2e.md) - local terminal E2E testing and package handoff.

Japanese translations are provided with the `_ja` postfix, for example [doc/05-operation_ja.md](doc/05-operation_ja.md).

## Workflows

`.github/workflows/` holds this repository's own CI/CD and is active here:

- `ci.yml` - builds and tests every pull request targeting main on Linux and Windows, and produces the
  NuGet package and the self-contained single-file apps as artifacts. It uses no secrets, so pull
  requests from forks pass.
- `release-draft.yml` - on a `v*` tag pushed onto main, packs the release and creates a **draft** GitHub
  release with the `.nupkg`, the single-file app archives, and `SHA256SUMS.txt`.
- `release-publish.yml` - when that draft release is published by hand, pushes the released `.nupkg` to
  GitHub Packages, Azure Artifacts, and nuget.org.

See [doc/03-ci-github-actions.md](doc/03-ci-github-actions.md) for the design.

## Workflow Samples

The files under `workflows/` are reference samples for *consumer* repositories and are not enabled
automatically here. Copy the sample for the CI platform into the target repository, then complete the
setup checklist in [doc/05-operation.md](doc/05-operation.md#6-workflow-setup-checklist).

- GitHub Actions: copy `workflows/github-actions/publish-intune-apps.yml` into `.github/workflows/`.
- Azure Pipelines: copy `workflows/azure-pipelines/azure-pipelines.yml` into the target repository root.

## Basic CLI Flow

```powershell
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build

relaypublisher plan --base-ref <base-ref> --output manifest-list.json

relaypublisher validate --manifest-list manifest-list.json

relaypublisher package --manifest-list manifest-list.json --output ./out

relaypublisher publish --manifest-list manifest-list.json --package-dir ./out --expected-tenant <tenant-id>
```

For bash, use the same commands.

## Important Invariants

- The authoritative design sources are `doc/00` through `doc/04` and `doc/issues/`.
- `doc/99-full-conversation-summary.md` is historical context and can differ from the current design.
- `doc/relaypublisher-design-and-copilot-issues.md` is only a pointer to split documents.
- App identity is `PackageIdentifier + Platform + Architecture`.
- Management metadata is stored in the Intune app `notes` field.
- Changed manifest detection is resolved once by `plan` and passed through CI as `manifest-list.json`.
- Windows `Detection.Type: file` evaluates an `exists` or `version` rule on the target device; its
  `Path` is not a repository path and must not be passed through repository path validation.
- An app entry's optional `Categories` list is the exact desired set of Intune app categories; omitting it
  preserves the app's current categories, and the tool never creates, renames, or deletes a tenant category
  (see [doc/05-operation.md](doc/05-operation.md#4d-intune-app-categories)).
- Publishing should always use `--expected-tenant` in production.

## APM Management

This repository uses APM to manage skills, agents, and MCP server configuration.

- manifest: `apm.yml`
- lock file: `apm.lock.yaml`
- skills: `.agents/skills/`
- agents: `.claude/agents/`, `.github/agents/`, `.codex/agents/`
- Codex configuration: `.codex/`
- MCP configuration: `.mcp.json`

## License

MIT License. See [LICENSE](LICENSE) for details.
