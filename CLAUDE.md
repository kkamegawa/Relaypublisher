# CLAUDE.md

まず [AGENTS.md](AGENTS.md) を読んでください。プロジェクト概要・設計上の不変条件・実装規約・Git 規約はそこに集約されており、このリポジトリでの作業はすべてそれに従います。

## Claude Code 向け補足

- 設計の正本は `doc/00-overview.md`〜`doc/04-ci-azure-pipelines.md` と `doc/issues/`。`doc/99-full-conversation-summary.md` は歴史的スナップショット、`doc/intune-lob-publisher-design-and-copilot-issues.md` はポインタなので、どちらも実内容を編集しない。
- Graph API の仕様(enum 値、v1.0/beta の差、upload flow)に関わる記述を変更するときは、Microsoft Learn MCP(`microsoft_docs_search` / `microsoft_docs_fetch`)で裏を取ってから書く。
- 現時点ではコード未実装。`src/` を作るときは `doc/issues/issue-001` から着手し、AGENTS.md の実装規約(コメントは英語、secrets をログに出さない、path traversal 検証)に従う。
- ライセンスは MIT。コミットメッセージは英語(`add:` / `fix:` / `update:` / `remove:`)。ドキュメントは当面日本語のまま。
