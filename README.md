# Intune LOB App Publisher - Copilot 実装資料

Intune LOB App Publisher の設計メモと GitHub Copilot / Coding Agent に渡すための Issue 群を分割した Markdown 一式です。

## Files

- `doc/00-overview.md` - 要件、基本方針、設計判断
- `doc/01-manifest-schema.md` - YAML schema 案と sample manifest
- `doc/02-dotnet-architecture.md` - .NET solution / project / interface 設計
- `doc/03-ci-github-actions.md` - GitHub Actions workflow 案
- `doc/04-ci-azure-pipelines.md` - Azure Pipelines workflow 案
- `doc/issues/issue-001-dotnet-cli-foundation.md` - 初期 Issue: .NET CLI foundation
- `doc/issues/issue-002-intunewinapputil.md` - IntuneWinAppUtil integration
- `doc/issues/issue-003-intune-graph-win32.md` - Intune Graph create/update flow
- `doc/issues/issue-004-assignment-merge.md` - Assignment merge
- `doc/issues/issue-005-source-providers.md` - GitHub Release / Azure Blob source providers
- `doc/intune-lob-publisher-design-and-copilot-issues.md` - 分割版へのポインタ
- `doc/99-full-conversation-summary.md` - 初期検討時の会話スナップショット(歴史的記録。最新の設計とは差分あり)

## Recommended order

1. `issue-001-dotnet-cli-foundation.md`
2. `issue-002-intunewinapputil.md`
3. `issue-003-intune-graph-win32.md`
4. `issue-004-assignment-merge.md`
5. `issue-005-source-providers.md`

## License

MIT License. See [LICENSE](LICENSE).
