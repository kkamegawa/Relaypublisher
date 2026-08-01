# docs: clarify canonical workflow sample locations and installation flow

## Goal

`doc/00-overview.md` の構成案は `.github/workflows/publish-intune-apps.yml` と repository root の `azure-pipelines.yml` を示しているが、実際の参照用サンプルは `workflows/github-actions/publish-intune-apps.yml` と `workflows/azure-pipelines/azure-pipelines.yml` に置かれている。

このままだと operator が「そのまま有効な workflow が入っている」と誤認しやすく、初期導入時の配置手順もドキュメント上で分散している。

## スコープ

- workflow sample の正本配置を明確化する
  - `workflows/` を参照用サンプルとして維持する
  - または `.github/workflows/` / repository root に実配置用 template を追加する
- `doc/00-overview.md`、`doc/03-ci-github-actions.md`、`doc/04-ci-azure-pipelines.md`、README の参照先表記を実態に合わせる
- 「どこに copy して何を設定したら有効化できるか」を 1 つの説明にまとめる

## 対象外

- workflow 自体の job 構成変更
- GitHub Actions / Azure Pipelines の認証仕様変更

## 見積もり

- ドキュメント中心(約 30–60 行)

