# docs: expand OIDC / workload identity setup guidance for operators

## Goal

`doc/05-operation.md` / `doc/05-operation_ja.md` には federated credential の前提はあるが、初期導入者向けに必要な OIDC / workload identity setup が十分に具体化されていない。

特に以下が分かりづらい。

- GitHub Actions / Azure Pipelines それぞれで何を作るか
- federated credential の subject をどこまで絞るか
- `azure/login` 後に `DefaultAzureCredential` が Graph token を取得する経路
- token exchange audience の前提

## スコープ

- `doc/05-operation.md` と `doc/05-operation_ja.md` に setup 手順を追記する
- GitHub Actions と Azure Pipelines の差分を対比で説明する
- `azure/login` / AzureCLI task と Graph token acquisition の関係を明記する
- federated credential 作成時の必須入力(subject, issuer, audience)を placeholder つきで整理する
- 失敗時の典型例を troubleshooting guide へ cross-link する

## 対象外

- 認証実装の変更
- 実在 tenant / app registration 値の記載

## 見積もり

- ドキュメント更新(約 50–80 行)

