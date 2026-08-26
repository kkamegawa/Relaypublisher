# macOS PKG detection primary bundle の指定 (GitHub #112)

## Goal

複数の app bundle を同梱する macOS PKG(例: Global Secure Access クライアントが Microsoft AutoUpdate を
同梱)で、検出・レポートの primary(先頭要素)を manifest から明示的に選択できるようにする。あわせて、
manifest だけでは分からない pkg 実体の bundle 構成を publish パイプライン内で検査し、複数 bundle や
bundle id の不一致を警告・確認する。

正本は GitHub issue #112 と `doc/00-overview.md` §6.21。本ファイルは実装単位の作業メモとして、それらに
追随する。

## 確定仕様

### Manifest

`Detection.PrimaryBundleId`(任意、`string?`)を `Detection` ブロックに追加する。`AppType: pkg` /
`lob` の両方に適用する。

```yaml
Detection:
  IgnoreAppVersion: true
  PrimaryBundleId: com.microsoft.globalsecureaccess
  IncludedApps:
    - BundleId: com.microsoft.globalsecureaccess.client
      BundleVersion: 1.2.3
```

意味論は次のとおり。

| Manifest | 動作 |
|---|---|
| `PrimaryBundleId` 省略 | 現行どおり `IncludedApps[0]` が primary。挙動・`inputHash` とも変更なし |
| 指定(完全一致 または `<値>.` 前置一致がちょうど 1 件) | 一致 entry が primary。Graph payload では先頭へ並べ替え |
| 指定(一致 0 件 / 2 件以上) | validation error |

マッチは Ordinal・大文字小文字区別。同梱 updater(例: `com.microsoft.autoupdate2`)は `IncludedApps` に
**書かないことで除外**する。詳細は `doc/01-manifest-schema.md` §5.4.3。

### Hash 互換性

`Detection.PrimaryBundleId` は **nullable な `string?`(初期値なし)** とする。`Categories`(issue-021)と
同じ契約。

- `InputHashCalculator` の canonical JSON は null property を落とすため、`PrimaryBundleId` を宣言していない
  既存 manifest の `manifestHash` / `inputHash` はこの変更の前後で byte 単位で不変。pinned hash の test で
  固定する。
- 指定すると `inputHash` が変わり、次の `package` / `publish` で再 package / 再 upload が発生する
  (macOS PKG は最大 8 GB)。detection 修正のための republish として意図どおり。
- `ManifestLoader` は `IgnoreUnmatchedProperties()` のため、新旧 CLI を交互に実行すると `inputHash` が
  振動して毎回 upload が発生する。`PrimaryBundleId` を使い始めたら CI と手元の CLI バージョンを揃えることを
  ドキュメントで要求する。
- `SchemaVersion` は `"1.0"` のまま(additive optional field)。

### Validation

ローカル validation(Graph に接続しない)で次を検証する。

- `Platform: windows` の app entry に `PrimaryBundleId` があれば fail。
- 空文字・空白のみなら fail。
- `IncludedApps` に対するマッチ(完全一致 または `<値>.` 前置一致)が 0 件なら fail(候補 BundleId をメッセージに
  列挙)。
- マッチが 2 件以上(prefix の曖昧一致)なら fail。
- `IgnoreAppVersion` とは独立に併用可能。

pkg 実体の検査(下記)は Graph に接続しないダウンロードベースの検査であり、`validate` はローカル参照可能な
source(例: ローカル `filePath`)でのみ実施する。ダウンロードが必要な source(`azureBlob` / `publicHttp` 等)
は `validate` では検査せず、`package` / `publish` のダウンロード直後に検査する。

### pkg introspection(pkg 実体の bundle 検査)

pkg(xar アーカイブ)の TOC(zlib 圧縮 XML)にある `Distribution` / `PackageInfo` の
`<bundle id="..." CFBundleShortVersionString="...">` から同梱 bundle 一覧(bundle id + version)を列挙する。
Payload(cpio.gz)の展開は不要。.NET 標準の `System.IO.Compression`(zlib/deflate)+ `System.Xml` のみで
実装し、サードパーティ依存を追加しない。macOS 専用ツール(`pkgutil`)には依存しない(どの OS の CI ランナー
でも動く)。

検査結果と manifest(`IncludedApps` / `PrimaryBundleId`)を突合し、次の場合に warning とする。

| 条件 | 挙動 |
|---|---|
| pkg 内に複数 bundle があり `PrimaryBundleId` 省略 | warning(検出 bundle 一覧・先頭要素で検出される旨を表示) |
| 指定 bundle id が pkg 実体に存在しない | warning(取り違え・typo の可能性) |
| `PrimaryBundleId` 指定済みで一致 bundle が pkg 内にも存在 | 警告なし |

**確認と `--force`**:

| 実行環境 | 挙動 |
|---|---|
| 対話実行(TTY) | warning 表示後、続行確認([y/N])。拒否時は exit code 非 0 で中断 |
| `--force` 指定時 | 確認せず warning ログのみで続行(CI を妨げない) |
| `--force` なしの非対話環境(TTY なし) | 安全側に倒して fail し、`--force` の付与を促す |

現行 pipeline は FluentValidation の Warning severity を CLI 出力に伝搬させていないため、warning の
伝搬経路(`ValidationResult` → CLI 出力)、対話確認プロンプト、`--force` オプションの追加が必要。

### Graph mapping

新しい Graph エンドポイント・permission は不要。

- `AppType: pkg`: 一致 entry が `primaryBundleId` / `primaryBundleVersion` になり、`includedApps` では
  先頭へ並べ替える。
- `AppType: lob`: 一致 entry が top-level `buildNumber` / `versionNumber` になり、`childApps` では先頭へ
  並べ替える。

## スコープ(実装フェーズ、本 issue のドキュメントフェーズでは着手しない)

- `src/IntuneLobPublisher.Core/Manifests/DetectionManifest.cs` — `public string? PrimaryBundleId { get; set; }` を追加。
- `src/IntuneLobPublisher.Core/Validation/ManifestValidator.cs` — `DetectionManifestValidator` に上記 validation
  ルールを追加(Warning severity の CLI 出力への伝搬を含む)。
- `src/IntuneLobPublisher.Core/Publishing/MacOsAppPayloadMapper.cs` — `includedApps[0]` / `childApps[0]` の
  決め打ちを、`PrimaryBundleId` によるマッチ・並べ替えロジック(`SelectPrimary` 相当のヘルパー)に置き換える。
- 新規 `src/IntuneLobPublisher.Core/Packaging/PkgBundleInspector.cs`(仮)— xar TOC reader、
  `Distribution`/`PackageInfo` XML から bundle id + version を列挙する。
- CLI(`validate` / `package` / `publish` コマンド)— 検査結果の warning 表示、対話確認プロンプト、
  `--force` オプションの追加。
- テスト: `tests/IntuneLobPublisher.Core.Tests` に manifest validation、pinned hash(変更前後)、
  mapper の並べ替え、pkg introspection(最小 xar フィクスチャ)、CLI の確認プロンプト/`--force` の
  各テストを追加。
- `doc/02-dotnet-architecture.md`、`doc/05-operation.md`/`_ja`、`doc/06-troubleshooting.md`/`_ja` の
  該当節追記(実装フェーズで着手)。

## 対象外

- 既知 updater(`com.microsoft.autoupdate*` 等)を判定する組み込みリストや自動警告。
- pkg から bundle を自動抽出して manifest を自動生成すること。
- `IncludedApps` の暗黙フィルタ(除外は常に「書かない」ことで行う)。
- manifest ファイル自体の並べ替え(並べ替えは Graph payload 生成時のみ)。
- pkg payload(cpio.gz)の展開・実ファイルの検証(TOC の宣言情報のみを信頼する)。

## 見積もり

- ドキュメントフェーズ(本 issue): 約 0.5 日。
- 実装フェーズ(別 issue または本 issue のスコープ更新): pkg introspection・validation・CLI 確認プロンプト・
  実テナント E2E を含めて 1〜2 日(約 700〜900 行)。
