# docs: add a single setup checklist for workflow secrets, variables, and environments

## Goal

workflow を有効化するための前提条件は `doc/03-ci-github-actions.md`、`doc/04-ci-azure-pipelines.md`、`doc/05-operation.md` に分散している。

現状でも情報は存在するが、operator が以下を 1 回で確認できる checklist がない。

- 必要な secret / variable 名
- protected environment / Exclusive Lock の設定
- `githubRelease` 用 token mapping
- `azureBlob` 用 OIDC login 前提
- `--expected-tenant` に渡す値の管理場所

## スコープ

- setup checklist を新規ドキュメントとして追加、または `doc/05-operation*.md` に集約する
- GitHub Actions 用 checklist を追加する
- Azure Pipelines 用 checklist を追加する
- source provider ごとの必要変数を一覧化する
- `workflows/` sample から checklist への導線を追加する

## 対象外

- secret 名の変更
- workflow job の再設計

## 見積もり

- ドキュメント更新(約 40–70 行)

