# macos: manifest model / validation / staging foundation

## Goal

Issue #45(publish: macOS support)は Graph クライアントと `PublishOrchestrator` の platform gate 拡張をスコープとしているが、その前提となる manifest model / validation / staging 側の macOS 対応がまだ存在しない。現状は以下のとおり。

- `ManifestValues.Platforms` が `["windows"]` のみで、`Platform: macos` の manifest は validate で fail する
- `DetectionManifest` に `IncludedApps`(bundleId + version のリスト)フィールドがない(doc/01-manifest-schema.md §5.4 / doc/00-overview.md §6.13 では macOS の必須検出手段)
- `AppManifest.Source`(unified source item shape)は model に存在するが、macOS 用の validation と staging(download + SHA256 検証)が未実装

## スコープ

- `ManifestValues.Platforms` に `macos` を追加し、macOS 向けの許容値(`InstallerType: pkg`、`AppType: pkg | lob`、architecture)を validation に反映する
- `DetectionManifest` に `IncludedApps`(BundleId + BundleVersion のリスト)を追加し、macOS app では 1 件以上必須とする validation を実装する
- macOS `Source` の必須 validation(`Sha256` 必須、path traversal 検証を含む)と、既存 source provider(publicHttp / githubRelease / azureBlob)を使った staging を実装する
- `samples/` の macOS manifest が validate / plan を通ることを確認する
- 単体テスト

## 対象外

- Graph への publish(`macOSPkgApp` / `macOSLobApp` の create/update、`IncludedApps` の Graph 反映)は #45 のスコープ

## 見積もり

約 300–400 行
