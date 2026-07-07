# Relaypublisher

Relaypublisher は、winget 風の YAML manifest を CI から Microsoft Intune の LOB アプリとして公開するためのプロジェクトです。

このリポジトリには、通常運用に必要な .NET CLI の基礎実装が含まれています。

- `validate` は manifest schema と repository 全体の identity 一意性を検証します。
- `plan` は対象 manifest set を一度だけ確定し、後続 CI job 用の `manifest-list.json` を書き出します。
- `package` は Windows Win32 app 用ファイルを staging し、Windows 上で `.intunewin` package を作成します。
- `publish` は Intune app の作成・更新、package content upload、assignment 同期を行います。

正式ドキュメントは英語版の [README.md](README.md) です。

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

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  plan --base-ref <base-ref> --output manifest-list.json

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  validate --manifest-list manifest-list.json

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  package --manifest-list manifest-list.json --output ./out

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  publish --manifest-list manifest-list.json --package-dir ./out --expected-tenant <tenant-id>
```

bash の場合は同じ引数を使い、行継続だけ `\` に変更します。

## 重要な不変条件

- 設計の正本は `doc/00` から `doc/04` と `doc/issues/` です。
- `doc/99-full-conversation-summary.md` は歴史的記録で、現在の設計と差分がある場合があります。
- `doc/intune-lob-publisher-design-and-copilot-issues.md` は分割ドキュメントへの pointer のみです。
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
