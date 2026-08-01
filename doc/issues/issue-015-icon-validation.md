# publish: validate icon existence, format, and size before Graph calls

## Goal

manifest の `Icon` は現在 repository-relative path としてしか検証しておらず、publish 時に初めて `largeIcon` へ変換される。

この状態だと以下の失敗が Graph 呼び出し段階まで遅延する。

- file が存在しない
- 期待外の形式
- 過大サイズ

`AppType: lob` の macOS では icon 必須でもあるため、早い段階での validation が必要。

## スコープ

- `Icon` file の存在確認を validation / publish 前段で行う
- 許可形式を明文化し、少なくとも PNG / JPEG 以外を reject する
- 運用上の上限サイズを決め、超過時は fail する
- 単体テストを追加する
- `doc/01-manifest-schema.md` に icon 制約を追記する

## 対象外

- icon の自動リサイズや変換
- macOS publish 全体(#45)

## 見積もり

- 実装 + テスト + ドキュメント更新(約 80–140 行)
