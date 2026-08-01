# docs: align workflow build commands with IntuneLobPublisher.slnx

## Goal

repository の solution file は `IntuneLobPublisher.slnx` だが、`doc/03-ci-github-actions.md`、`doc/04-ci-azure-pipelines.md`、および `workflows/` 配下の sample YAML はまだ `IntuneLobPublisher.sln` を参照している。

sample をそのまま使うと `dotnet build` / `dotnet test` が失敗するため、workflow ドキュメントと sample を実装に合わせて修正する必要がある。

## スコープ

- `doc/03-ci-github-actions.md` の `dotnet build` / `dotnet test` 例を `IntuneLobPublisher.slnx` に更新する
- `doc/04-ci-azure-pipelines.md` の同箇所を更新する
- `workflows/github-actions/publish-intune-apps.yml` を更新する
- `workflows/azure-pipelines/azure-pipelines.yml` を更新する
- `README.md` / `README_ja.md` / `doc/05-operation*.md` と表記揺れがないことを確認する

## 対象外

- solution 構成そのものの再設計(issue-007 のスコープ)

## 見積もり

- ドキュメント + sample YAML 更新(約 20–40 行)

