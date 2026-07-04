# Relaypublisher

Relaypublisher は、winget 風の YAML manifest を Git に commit すると、CI が Microsoft Intune の LOB アプリを登録・更新するためのプロジェクトです。

現時点では **設計ドキュメントと実装 Issue が中心**で、実装は `doc/issues/` の順に進める前提になっています。

## このリポジトリでできること

- Intune LOB 配布の要件・設計判断を一元管理
- YAML manifest schema と Graph mapping の定義
- .NET 9 / C# 実装方針の明文化
- GitHub Actions / Azure Pipelines の CI 設計
- Copilot/Claude/Agent Skills 向け APM 管理設定

## まず読むべきドキュメント

- `doc/00-overview.md` - 要件、基本方針、設計判断（最重要）
- `doc/01-manifest-schema.md` - YAML schema とサンプル
- `doc/02-dotnet-architecture.md` - .NET solution / interface 設計
- `doc/03-ci-github-actions.md` - GitHub Actions 案
- `doc/04-ci-azure-pipelines.md` - Azure Pipelines 案

## 実装の進め方

実装は `doc/issues/` の Issue をこの順で進めます。

1. `doc/issues/issue-001-dotnet-cli-foundation.md`
2. `doc/issues/issue-002-intunewinapputil.md`
3. `doc/issues/issue-003-intune-graph-win32.md`
4. `doc/issues/issue-004-assignment-merge.md`
5. `doc/issues/issue-005-source-providers.md`

## 重要な前提

- 設計の正本は `doc/00`〜`04` と `doc/issues/`
- `doc/99-full-conversation-summary.md` は歴史的記録（最新設計ではない）
- `doc/intune-lob-publisher-design-and-copilot-issues.md` は分割版へのポインタ

## APM 管理（Copilot / Claude / Agent Skills）

このリポジトリは APM で skill / agent / MCP server を管理しています。

- manifest: `apm.yml`
- lockfile: `apm.lock.yaml`
- skills: `.agents/skills/`
- agents: `.claude/agents/`, `.github/agents/`
- MCP config: `.mcp.json`

## ライセンス

MIT License. 詳細は [LICENSE](LICENSE) を参照してください。
