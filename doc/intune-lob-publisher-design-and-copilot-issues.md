# Intune LOB App Publisher 設計メモ / Copilot 実装用 Issue 一式

この文書は分割版へのポインタです。内容は以下の分割ドキュメントを参照してください(こちらが常に最新です)。

## 設計ドキュメント

- [00-overview.md](00-overview.md) - 要件、基本方針、設計判断(app identity、changed detection、冪等性、同時実行制御、Graph 権限、macOS app type などを含む)
- [01-manifest-schema.md](01-manifest-schema.md) - YAML schema(SchemaVersion、統一ソース形式、assignment 拡張、Graph mapping)
- [02-dotnet-architecture.md](02-dotnet-architecture.md) - .NET solution / project / interface 設計と実装フェーズ
- [03-ci-github-actions.md](03-ci-github-actions.md) - GitHub Actions workflow 案
- [04-ci-azure-pipelines.md](04-ci-azure-pipelines.md) - Azure Pipelines workflow 案

## 実装 Issue

- [issues/issue-001-dotnet-cli-foundation.md](issues/issue-001-dotnet-cli-foundation.md) - .NET CLI foundation
- [issues/issue-002-intunewinapputil.md](issues/issue-002-intunewinapputil.md) - IntuneWinAppUtil integration
- [issues/issue-003-intune-graph-win32.md](issues/issue-003-intune-graph-win32.md) - Intune Graph create/update flow
- [issues/issue-004-assignment-merge.md](issues/issue-004-assignment-merge.md) - Assignment merge
- [issues/issue-005-source-providers.md](issues/issue-005-source-providers.md) - Source providers

## 履歴

- [99-full-conversation-summary.md](99-full-conversation-summary.md) - 初期検討時の会話スナップショット(歴史的記録。最新の設計とは差分あり)
