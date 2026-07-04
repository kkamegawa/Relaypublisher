# AGENTS.md

このファイルは、このリポジトリで作業するコーディングエージェント(GitHub Copilot Coding Agent、Claude Code など)向けのガイドです。

## プロジェクト概要

Relaypublisher は、winget 風の YAML manifest を Git に commit すると、CI が Microsoft Intune の LOB アプリを登録・更新するツールです。

- 対象プラットフォーム: Windows Win32 (x64 / Arm64)、macOS (PKG)
- 実装言語: .NET 9 / C#(**現時点では設計ドキュメントのみで、実装コードはまだ存在しません**)
- 配布バイナリは Git に置かず、publicHttp / private GitHub Release / Azure Blob から取得する

## リポジトリ構成と正本

| パス | 内容 |
|---|---|
| `doc/00-overview.md` | 要件、基本方針、**設計判断(6.x)** — 最重要 |
| `doc/01-manifest-schema.md` | YAML manifest schema と Graph mapping |
| `doc/02-dotnet-architecture.md` | .NET solution / interface 設計、実装フェーズ |
| `doc/03-ci-github-actions.md` | GitHub Actions workflow 案 |
| `doc/04-ci-azure-pipelines.md` | Azure Pipelines workflow 案 |
| `doc/issues/issue-001 〜 005` | 実装 Issue(この順に実装する) |
| `doc/intune-lob-publisher-design-and-copilot-issues.md` | 分割版へのポインタ。**実内容を書かない** |
| `doc/99-full-conversation-summary.md` | 初期検討のスナップショット。**歴史的記録なので更新しない**(最新設計と差分あり) |

設計の正本は `doc/00`〜`04` と `doc/issues/` です。矛盾を見つけたら 99 ではなく正本側を信頼してください。

## 設計上の不変条件

以下を変更する場合は、コードや issue だけでなく **先に `doc/00-overview.md` の該当する設計判断を更新**してください。

- App identity は `PackageIdentifier + Platform + Architecture`。Intune app の `notes` に management metadata (JSON) を保存する。照合が複数一致したら fail、DisplayName fallback 一致時は adopt(notes を書き戻す)。
- Display name にバージョンを含めない。
- `AssignmentSync` の既定は `merge`(グループ単位 upsert。manifest にない既存 assignment を削除しない)。`replace` のみ完全同期。
- 更新スキップ判定は決定的 **inputHash**(manifest + 入力ファイル群)。`.intunewin` 自体のハッシュは暗号鍵がランダムなため使わない。
- Windows Arm64 は Graph の `allowedArchitectures` で表現する(v1.0 の `applicableArchitectures` enum に `arm64` はない)。
- macOS の既定 app type は `macOSPkgApp`(unmanaged)。検出は `IncludedApps`(bundleId + version のリスト)。`AppType: pkg` に uninstall intent は不可。
- Changed detection は `plan --base-ref` で一度だけ確定し、`manifest-list.json` を CI の後続 job に artifact で渡す。後続 job で再計算しない。
- manifest は top-level `SchemaVersion` 必須。未知の major は fail。
- ダウングレードは既定 skip(`--allow-downgrade` で明示)。publish は `--expected-tenant` で tid を照合。
- すべての Graph 呼び出しで 429/503 の `Retry-After` を尊重する。

## 実装規約(`src/` を追加する場合)

- .NET 9 / C#。使用パッケージ: System.CommandLine, YamlDotNet, FluentValidation, xUnit, Microsoft.Extensions.Logging / DependencyInjection, Azure.Identity, Azure.Storage.Blobs
- Microsoft Graph SDK は使わず `HttpClient` + `Azure.Identity`(REST URL と payload をレビュー可能に保つため)
- ソースコードのコメントは英語(MIT ライセンス)
- secrets / token / Authorization ヘッダー / 署名付き URL をログ・成果物・例外メッセージに出さない
- manifest 由来のパスは必ず path traversal / 絶対パス検証を通す
- `dotnet build` と `dotnet test` が通ることを変更の完了条件とする

## ドキュメント規約

- ドキュメントは当面日本語で書く(技術用語は英語のまま)。LICENSE / SECURITY.md は英語を維持する
- 実在の URL・IP アドレス・テナント ID を書かない。`<tenant-id>` のようなプレースホルダを使う(例示用の `example.com` / `contoso` は可)
- Intune / Microsoft Graph の API 仕様(endpoint、enum 値、v1.0 と beta の差)に触れる変更は、Microsoft Learn で最新仕様を確認してから行う

## Git 規約

- コミットメッセージは英語で `[add|fix|update|remove]: description` 形式
  - 機能追加 `add:` / バグ修正 `fix:` / 更新 `update:` / 削除 `remove:`
- ブランチ名: `feature/<issue番号>-` `fix/<issue番号>-` `refactor/` `perf/` `chore/` `docs/`
- コミットは issue 単位でまとめる
