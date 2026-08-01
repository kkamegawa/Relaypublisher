# docs: clarify that the macOS sample manifest is schema-target only for now

## Goal

`samples/manifests/contoso-tool-macos-arm64.yaml` は存在するが、現状の CLI は `Platform: macos` を正式には受け付けず、publish も Windows 以外を skip する。

sample 自体は将来形の schema を示す意図だが、README / operation guide / sample 導線では「今すぐ使える sample」と誤解されやすい。

## スコープ

- README / `README_ja.md` に macOS sample の位置づけを追記する
- sample manifest の注記から関連 issue(issue-006 / issue-045)を辿れるようにする
- validation / package / publish それぞれの対応状況を簡潔に整理する
- `doc/05-operation*.md` または `doc/06-troubleshooting*.md` に operator 向け補足を追加する

## 対象外

- macOS validation/staging 実装(issue-006)
- macOS publish 実装(issue-045)

## 見積もり

- ドキュメント更新(約 20–40 行)

