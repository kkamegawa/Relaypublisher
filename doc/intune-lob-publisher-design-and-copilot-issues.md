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
- [issues/issue-006-macos-manifest-foundation.md](issues/issue-006-macos-manifest-foundation.md) - macOS manifest model / validation / staging foundation
- [issues/issue-007-solution-structure-alignment.md](issues/issue-007-solution-structure-alignment.md) - doc/02 solution structure alignment
- [issues/issue-008-workflow-sample-installation.md](issues/issue-008-workflow-sample-installation.md) - workflow sample locations and installation flow
- [issues/issue-009-workflow-slnx-alignment.md](issues/issue-009-workflow-slnx-alignment.md) - workflow build command alignment for `.slnx`
- [issues/issue-010-cli-command-doc-alignment.md](issues/issue-010-cli-command-doc-alignment.md) - CLI command example alignment
- [issues/issue-011-operation-guide-oidc-setup.md](issues/issue-011-operation-guide-oidc-setup.md) - OIDC / workload identity operator guide
- [issues/issue-012-workflow-setup-checklist.md](issues/issue-012-workflow-setup-checklist.md) - workflow setup checklist
- [issues/issue-013-changed-detection-fallback-observability.md](issues/issue-013-changed-detection-fallback-observability.md) - changed detection fallback visibility
- [issues/issue-014-workflow-permission-guardrails.md](issues/issue-014-workflow-permission-guardrails.md) - least-privilege workflow guardrails
- [issues/issue-015-icon-validation.md](issues/issue-015-icon-validation.md) - icon validation before Graph calls
- [issues/issue-016-management-metadata-notes-observability.md](issues/issue-016-management-metadata-notes-observability.md) - notes-size observability
- [issues/issue-017-macos-sample-status-clarification.md](issues/issue-017-macos-sample-status-clarification.md) - macOS sample status clarification
- [issues/issue-018-input-hash-specification.md](issues/issue-018-input-hash-specification.md) - inputHash normalization specification

## 履歴

- [99-full-conversation-summary.md](99-full-conversation-summary.md) - 初期検討時の会話スナップショット(歴史的記録。最新の設計とは差分あり)
