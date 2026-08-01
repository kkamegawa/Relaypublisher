# publish: expose management metadata notes-size checks to operators

## Goal

management metadata は `notes` に JSON として保存され、実装上は長すぎる場合に fail する。しかし operator からは「今どれくらい余裕があるか」「なぜ fail したか」が見えにくい。

特に dry-run や事前検証時に size 情報が出ないため、manifest path や sourceCommit を増やしたときの影響を予測しづらい。

## スコープ

- metadata serialize 時または publish 前に size / limit をログ出力する
- limit 超過時のメッセージを operator 視点で補強する
- `doc/05-operation*.md` または `doc/06-troubleshooting*.md` に failure mode を追記する
- 既存の size check を壊さない単体テストを追加する

## 対象外

- metadata schema の変更
- Intune 側 `notes` の仕様変更

## 見積もり

- 小規模実装 + ドキュメント更新(約 40–80 行)

