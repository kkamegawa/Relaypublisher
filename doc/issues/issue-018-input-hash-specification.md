# docs: make the inputHash normalization algorithm authoritative

## Goal

`doc/00-overview.md` 6.7 は `inputHash` を「manifest 正規化ハッシュ + 入力ファイル群」で定義しているが、正規化の具体的な contract が文書側では十分に固定されていない。

一方、実装は canonical JSON serialization、null 除外、`/` 区切り path、ordinal sort を前提としている。仕様として固定しないと、将来の実装変更や他実装との互換性判断が難しい。

## スコープ

- `doc/00-overview.md` 6.7 に manifest normalization の具体則を追記する
- 必要なら `doc/01-manifest-schema.md` に補足を追加する
- path separator 正規化と sort 順序を仕様として明文化する
- formatting / comment 差分で hash が変わらないことをテスト観点として明記する

## 対象外

- hash algorithm の変更
- package metadata schema の変更

## 見積もり

- ドキュメント更新(約 20–40 行)

