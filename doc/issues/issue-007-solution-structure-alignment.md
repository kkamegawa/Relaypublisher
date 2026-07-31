# docs: align solution structure in doc/02 §7.1 with the implementation

## Goal

doc/02-dotnet-architecture.md §7.1 の solution 構成案は `IntuneLobPublisher.Intune` / `IntuneLobPublisher.Azure` / `IntuneLobPublisher.GitHub` と `tests/IntuneLobPublisher.Intune.Tests` を含むが、実装は `IntuneLobPublisher.Core` + `IntuneLobPublisher.Cli`(テストは `IntuneLobPublisher.Core.Tests`)の 2 プロジェクト構成に集約されている(Graph/Intune 操作は `Core/Publishing`、Azure Blob は `Core/Sources`、GitHub Release は `Core/Sources`)。

設計の正本と実装が乖離したままだと、今後の実装 issue が存在しないプロジェクトを参照してしまう。

## スコープ

次のどちらかを選択して実施する(推奨は前者)。

- **A. ドキュメント更新**: doc/02 §7.1 を現行の 2 プロジェクト構成(+ `IntuneLobPublisher.IntegrationTests` は #48 で追加予定)に合わせて更新し、モジュール分割を将来の拡張として注記する
- **B. プロジェクト分割**: `Sources/` の Azure / GitHub 実装と `Publishing/` を doc/02 どおり別プロジェクトへ抽出し、参照とテストを更新する

## 備考

- ファイル名は `IntuneLobPublisher.slnx` であり、doc/02 の `IntuneLobPublisher.sln` 表記も合わせて修正する

## 見積もり

- A: ドキュメントのみ(約 30–50 行)
- B: 大規模リファクタリング(約 500 行以上の移動)
