# plan: surface base-ref fallback-to-all behavior in logs and docs

## Goal

`plan --base-ref` は設計どおり、基準 ref が未解決または zero SHA のとき全 manifest fallback になる。しかし operator 視点では「なぜ全件対象になったのか」が CLI 出力や workflow sample から読み取りづらい。

branch 新規作成、force push 直後、dispatch などで全件対象になっても、現状は意図どおりか異常かを判断しにくい。

## スコープ

- `plan` 実行時に fallback 理由を明示的にログ出力する
  - base-ref 未指定
  - base-ref 未解決
  - zero SHA 相当
- `doc/00-overview.md` 6.6 の fallback 条件を operator 向けに補足する
- `doc/03-ci-github-actions.md` / `doc/04-ci-azure-pipelines.md` に workflow 上の典型ケースを追記する
- `doc/06-troubleshooting*.md` から確認手順を参照できるようにする

## 対象外

- changed detection ロジックの変更
- manifest selection ルールの変更

## 見積もり

- 小規模実装 + ドキュメント更新(約 40–80 行)

