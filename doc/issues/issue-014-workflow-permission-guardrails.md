# ci: add guardrails for least-privilege workflow permissions

## Goal

設計では「PR で動く job に `id-token: write` を付けない」「permissions は job 単位で最小化する」を前提としているが、repository にはその方針を自動で検査する guardrail がない。

現状は sample YAML が正しくても、将来の編集で権限が広がっても検知できない。

## スコープ

- workflow 定義を検査する CI チェックを追加する
  - PR で実行される job に `id-token: write` が付いていないこと
  - publish job にだけ production 向け権限が付与されていること
  - workflow-level permissions が過剰に広くないこと
- guardrail の意図を `doc/03-ci-github-actions.md` または `SECURITY.md` に追記する
- 失敗時に何を直すべきか分かるメッセージにする

## 対象外

- GitHub 側の permission model 自体の変更
- Azure Pipelines 側の RBAC 設計変更

## 見積もり

- 小規模実装 + ドキュメント更新(約 80–120 行)

