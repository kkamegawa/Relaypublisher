# Relaypublisher

Relaypublisher publishes winget-like YAML manifests as Microsoft Intune LOB apps from CI.

The repository now contains the .NET CLI foundation for the normal workflow:

- `validate` checks manifest schema rules and repository-wide identity uniqueness.
- `plan` resolves the target manifest set once and writes `manifest-list.json` for later CI jobs.
- `package` stages app files: Windows Win32 `.intunewin` packages (Windows runner required) or a
  staged, checksum-verified macOS `.pkg` (any OS).
- `publish` creates or updates Intune apps, uploads packaged content, and reconciles assignments.

The Japanese translation is available in [README_ja.md](README_ja.md).

## Supported Platforms

| | Windows (`win32LobApp`) | macOS `AppType: pkg` (`macOSPkgApp`, default) | macOS `AppType: lob` (`macOSLobApp`) |
|---|---|---|---|
| Graph API version | v1.0 | beta | v1.0 |
| Signing | Not required | Not required | Developer ID Installer required |
| Max package size | - | 8 GB | 2 GB |
| Icon | Optional | Optional | Required |
| `Intent: uninstall` | Supported | Not supported | Supported |
| Detection | PowerShell script | `IncludedApps` (bundleId + version) | `IncludedApps` (bundleId + version) |

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

Japanese translations are provided with the `_ja` postfix, for example [doc/05-operation_ja.md](doc/05-operation_ja.md).

## Basic CLI Flow

```powershell
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  plan --base-ref <base-ref> --output manifest-list.json

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  validate --manifest-list manifest-list.json

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  package --manifest-list manifest-list.json --output ./out

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  publish --manifest-list manifest-list.json --package-dir ./out --expected-tenant <tenant-id>
```

For bash, use the same arguments with line continuations changed to `\`.

## Important Invariants

- The authoritative design sources are `doc/00` through `doc/04` and `doc/issues/`.
- `doc/99-full-conversation-summary.md` is historical context and can differ from the current design.
- `doc/intune-lob-publisher-design-and-copilot-issues.md` is only a pointer to split documents.
- App identity is `PackageIdentifier + Platform + Architecture`.
- Management metadata is stored in the Intune app `notes` field.
- Changed manifest detection is resolved once by `plan` and passed through CI as `manifest-list.json`.
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
