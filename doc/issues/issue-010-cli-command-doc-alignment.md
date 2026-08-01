# docs: align CLI command examples with the implemented plan/manifest-list flow

## Goal

`doc/02-dotnet-architecture.md` には `plan --changed --base-ref <sha>` のような旧表記が残っている一方、実装済み CLI は `plan --base-ref` / `--manifest-root` / `--manifest-list` を前提とする構成になっている。

現状でも README、operation guide、troubleshooting guide の説明が完全には揃っておらず、custom script や CI を書く operator が古い command shape を参照してしまう。

## スコープ

- `doc/02-dotnet-architecture.md` の command examples を現行 CLI に合わせて更新する
- `--manifest`、`--manifest-list`、`--manifest-root` の使い分けを明文化する
- `plan` → `manifest-list.json` → `validate/package/publish` の一方向フローを全ドキュメントで統一する
- command example のうち、旧来の `--changed` 前提の記述が残っていれば整理する

## 対象外

- CLI option の追加や rename
- changed detection algorithm 自体の変更

## 見積もり

- ドキュメント更新(約 30–50 行)

