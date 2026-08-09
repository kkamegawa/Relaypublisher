# Relaypublisher

Relaypublisher は、winget 風の YAML manifest を CI から Microsoft Intune の LOB アプリとして公開するためのプロジェクトです。

配布形態:

- NuGet global tool package id: `relaypublisher`
- Command name: `relaypublisher`
- Package version: Git tag `vX.Y.Z` から CI が注入

Install:

```bash
dotnet tool install --global relaypublisher
```

このリポジトリには、通常運用に必要な .NET CLI の基礎実装が含まれています。

- `validate` は manifest schema と repository 全体の identity 一意性を検証します。
- `plan` は対象 manifest set を一度だけ確定し、後続 CI job 用の `manifest-list.json` を書き出します。
- `package` は app ファイルを staging します。Windows Win32 は `.intunewin` package を Windows 上で生成し、
  macOS は checksum 検証済みの `.pkg` を staging します(OS 不問)。
- `publish` は Intune app の作成・更新、package content upload、assignment 同期を行います。

正式ドキュメントは英語版の [README.md](README.md) です。

## 対応プラットフォーム

| | Windows (`win32LobApp`) | macOS `AppType: pkg`(既定、`macOSPkgApp`) | macOS `AppType: lob`(`macOSLobApp`) |
|---|---|---|---|
| Graph API バージョン | v1.0 | beta | v1.0 |
| 署名 | 不要 | 不要 | Developer ID Installer 署名必須 |
| package サイズ上限 | - | 8 GB | 2 GB |
| Icon | 任意 | 任意 | 必須 |
| `Intent: uninstall` | 対応 | 非対応 | 対応 |
| 検出方法 | PowerShell script | `IncludedApps`(bundleId + version) | `IncludedApps`(bundleId + version) |

macOS manifest の詳細な形式と validation ルールは [doc/01-manifest-schema.md](doc/01-manifest-schema.md) §5.3-5.4 を、
設計の背景(`macOSPkgApp` が Graph beta を必要とする理由を含む)は [doc/00-overview.md](doc/00-overview.md) §6.13 を参照してください。

## このリポジトリでできること

- Intune LOB publishing の設計判断を一元管理。
- YAML manifest schema と Microsoft Graph mapping の定義。
- .NET 10 / C# CLI 実装。
- GitHub Actions と Azure Pipelines の workflow 例。
- 本番運用のための operation / troubleshooting guide。
- Copilot、Claude、Codex、Agent Skills 向け APM 設定。

## まず読むべきドキュメント

- [doc/00-overview.md](doc/00-overview.md) - 要件、基本方針、主要な設計判断。
- [doc/01-manifest-schema.md](doc/01-manifest-schema.md) - YAML schema と例。
- [doc/02-dotnet-architecture.md](doc/02-dotnet-architecture.md) - .NET solution と interface 設計。
- [doc/03-ci-github-actions.md](doc/03-ci-github-actions.md) - GitHub Actions workflow。
- [doc/04-ci-azure-pipelines.md](doc/04-ci-azure-pipelines.md) - Azure Pipelines workflow。
- [doc/05-operation.md](doc/05-operation.md) - 運用設定と日常コマンド。
- [doc/06-troubleshooting.md](doc/06-troubleshooting.md) - 復旧と障害対応。

日本語訳は `_ja` postfix 付きで提供します。例: [doc/05-operation_ja.md](doc/05-operation_ja.md)。

## 基本 CLI フロー

```powershell
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build

relaypublisher plan --base-ref <base-ref> --output manifest-list.json

relaypublisher validate --manifest-list manifest-list.json

relaypublisher package --manifest-list manifest-list.json --output ./out

relaypublisher publish --manifest-list manifest-list.json --package-dir ./out --expected-tenant <tenant-id>
```

bash の場合も同じコマンドを使えます。

## 重要な不変条件

- 設計の正本は `doc/00` から `doc/04` と `doc/issues/` です。
- `doc/99-full-conversation-summary.md` は歴史的記録で、現在の設計と差分がある場合があります。
- `doc/relaypublisher-design-and-copilot-issues.md` は分割ドキュメントへの pointer のみです。
- App identity は `PackageIdentifier + Platform + Architecture` です。
- Management metadata は Intune app の `notes` field に保存します。
- Changed manifest detection は `plan` で一度だけ確定し、CI では `manifest-list.json` を引き回します。
- 本番 publish では常に `--expected-tenant` を使います。

## APM 管理

このリポジトリは APM で skills、agents、MCP server configuration を管理しています。

- manifest: `apm.yml`
- lock file: `apm.lock.yaml`
- skills: `.agents/skills/`
- agents: `.claude/agents/`, `.github/agents/`, `.codex/agents/`
- Codex configuration: `.codex/`
- MCP configuration: `.mcp.json`

## ライセンス

MIT License. 詳細は [LICENSE](LICENSE) を参照してください。
