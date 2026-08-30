# macOS PKG detection primary bundle の指定 (GitHub #112)

## Goal

複数の app bundle を同梱する macOS PKG(例: Global Secure Access クライアントが Microsoft AutoUpdate を同梱)で、
検出・レポートの primary(先頭要素)を manifest から明示的に選択できるようにする。あわせて、manifest だけでは分からない
pkg 実体の bundle 構成と宣言 version を publish パイプライン内で検査し、取り違えを Graph mutation 前に検知する。

正本は GitHub issue #112 と `doc/00-overview.md` §6.21。本ファイルは、実装を 3 層の stacked PR に分けるための
decision-complete な実装仕様である。

## 確定仕様

### Manifest contract

`Detection.PrimaryBundleId`(任意、`string?`)を `Detection` ブロックに追加する。`AppType: pkg` / `lob` の両方に適用する。

```yaml
Detection:
  IgnoreAppVersion: true
  PrimaryBundleId: com.microsoft.globalsecureaccess.client
  IncludedApps:
    - BundleId: com.microsoft.globalsecureaccess.client
      BundleVersion: 1.2.3
      # AppType: lob の場合は BundleBuildVersion(CFBundleVersion)も必須。
```

意味論は次のとおり。

| Manifest | 動作 |
|---|---|
| `PrimaryBundleId` 省略 | 現行どおり `IncludedApps[0]` が primary。挙動・`inputHash` とも変更なし |
| 指定(完全一致または `<値>.` セグメント境界の前置一致がちょうど 1 件) | 一致 entry が primary。Graph payload では先頭へ並べ替え |
| 指定(一致 0 件 / 2 件以上) | validation error。複数 prefix に一致する値は曖昧指定として拒否 |

マッチは Ordinal・大文字小文字区別。同梱 updater(例: `com.microsoft.autoupdate2`)は `IncludedApps` に**書かないことで除外**する。
`IncludedApps` は 1〜500 件、`BundleId` は Ordinal・大文字小文字区別で重複不可とする。500 件は Graph の
`macOSPkgApp.includedApps` 上限に合わせる。

`IncludedApps[].BundleVersion` は `CFBundleShortVersionString` を表す。`AppType: lob` の場合は各 entry に
`BundleBuildVersion`(`CFBundleVersion`)を必須とする。`AppType: pkg` では `BundleBuildVersion` を省略でき、指定されても
Graph mapping と PKG inspection の version 比較では使用しない。

### Hash compatibility

`Detection.PrimaryBundleId` と `BundleBuildVersion` は nullable・初期値なしとする。

- canonical JSON の null property 省略により、これらを宣言していない既存 pkg manifest の `manifestHash` / `inputHash` は
  byte 単位で不変とする。pinned hash test で固定する。
- `PrimaryBundleId` を指定すると `inputHash` が変わり、detection 修正のため次の `package` / `publish` で再 package / 再 upload
  が発生する。lob の `BundleBuildVersion` も指定時は hash の入力になる。pkg で指定した `BundleBuildVersion` は Graph mapping
  では無視するが、canonical manifest に存在するため指定時は hash が変わる。
- `SchemaVersion` は additive optional field として `"1.0"` のまま維持する。
- `ManifestLoader` は未知 property を無視するため、新旧 CLI を交互に実行すると hash が振動し得る。新フィールドを使い始めたら
  CI と手元の CLI version を揃える。

### Static validation

`validate`、`package`、`publish` は Graph mutation 前に次を検証する。

- `Platform: windows` の app entry に `PrimaryBundleId` があれば fail。
- `PrimaryBundleId` が空文字・空白だけなら fail。
- `IncludedApps` は 1〜500 件、`BundleId` は重複不可。
- exact/segment-boundary prefix の一致が 0 件なら fail(候補 BundleId 一覧をエラーに含める)。
- 一致が 2 件以上なら fail(完全な bundle id または、より狭い segment prefix を指定するよう促す)。
- `AppType: lob` の各 IncludedApps entry は `BundleBuildVersion` 必須。`AppType: pkg` では任意・mapping上は無視。
- `IgnoreAppVersion` は `PrimaryBundleId` と独立して使用できる。

PKG の download、SHA256、XAR inspection は `validate` の責務ではない。`validate` は schema、manifest 内の件数・重複・selector、
既存の repository-relative path などの静的検証だけを行う。今回 `filePath` source は追加しない。download が必要な source は
`package` で検査し、`publish` では artifact を再検査する。

### Bounded PKG introspection

PKG は XAR archive として扱い、TOC に記録された `Distribution` / `PackageInfo` の heap entry 本体を bounded reader で読み取る。
TOC に列挙された entry の offset/length を使い、payload(cpio.gz)は展開しない。実装は .NET 標準ライブラリのみで行い、macOS の
`pkgutil` には依存しない。

> **2026-08-30 追記**: ここで記録した「.NET 標準ライブラリのみ」という制約は issue #127 で見直した。
> heap entry の bzip2 compression に対応するため MIT license の `SharpZipLib` を adapter 越しに許可している。
> 詳細は `doc/adr-phase-2.md` を参照。`pkgutil` 非依存の要件(完全な managed 実装で macOS runner を
> 必要としない)は変更していない。

安全上の上限は次のとおり固定する。

| 対象 | 上限 |
|---|---:|
| compressed TOC | 16 MiB |
| decompressed TOC | 64 MiB |
| 1 heap entry | 16 MiB |
| 検出 bundle 数 | 4,096 |
| XML depth | 64 |

DTD と外部 entity は `DtdProcessing.Prohibit` / resolver 無効で禁止する。未知または未対応 compression、header/offset/length の
不整合、切り詰め、展開エラー、malformed XML、上限超過は hard fail とし、`--force` でも回避できない。

inspection は source の期待 SHA256を検証して一致した後に限り開始する。結果には次を含める。

- 実ファイルの content SHA256
- inspector version / inspection schema version
- 検出 bundle の `bundleId`、`CFBundleShortVersionString`、取得できる場合の `CFBundleVersion`
- selector で解決した primary の完全な bundle ID
- warning code の配列と `force` 使用有無

source URL、token、Authorization header、署名付き URLは metadata、ログ、例外に記録しない。

### Semantic warning と version mismatch

parser が正常終了した後の意味上の不一致だけを semantic warning とし、次を対象にする。

| 条件 | warning |
|---|---|
| PKG 内に複数 bundle があり `PrimaryBundleId` 省略 | 検出 bundle 一覧と、先頭要素が primary になることを表示 |
| `IncludedApps` または `PrimaryBundleId` の bundle ID が PKG 実体に存在しない | typo / 取り違えの可能性を表示 |
| manifest `BundleVersion` と実体 `CFBundleShortVersionString` が不一致 | stale な detection version の可能性を表示 |
| `AppType: lob` の `BundleBuildVersion` と実体 `CFBundleVersion` が不一致 | stale な build version の可能性を表示 |
| 検出 bundle が 0 件 | manifest と PKG の対象が一致しない可能性を表示 |

`IgnoreAppVersion` は Graph の version detection を無視する指定であり、manifest と実体の version mismatch warning を抑止しない。
`PrimaryBundleId` 指定時の selector は manifest 内の exactly-one ルールで解決した値と、inspection 結果の bundle ID を別々に比較する。

semantic warning のみ `--force` で承認できる。hard fail(静的 validation、SHA mismatch、XAR破損・未対応・上限超過、XML安全性違反など)
は `--force` の対象外である。

### package / publish の force と preflight

| 実行環境 | 挙動 |
|---|---|
| `package` / `publish` の TTY | warning をまとめて表示し、一度だけ `[y/N]` で確認。拒否時は非 0 で終了 |
| `package --force` / `publish --force` | semantic warning のみ確認せず続行し、warning と force 使用を metadata/result に記録 |
| 非対話環境で `--force` なし | warning があれば fail し、`--force` を促す |

`package` は SHA256 検証後に inspection を実行し、artifact metadata に inspection report を保存する。`publish` は package metadata を
信頼するだけにせず、対象 artifact の実バイト列を再ハッシュして期待 SHA256および metadata SHA256と照合し、XAR を再 inspection する。
`publish --force` の承認はその実行の batch にだけ有効で、過去の report の force 記録を再利用しない。

`publish` は全対象 entry の static validation、artifact存在、hash照合、XAR inspection、semantic warning の承認、tenant検証を
**全件完了してから** Graph の create/upload/PATCH/assignment を開始する。1 件でも hard fail または未承認 warning があれば Graph
mutation は 0 件とする。inspection report が欠落・古い・SHA256と不一致の場合は、Graph mutation 前に再 inspection する。

### Graph mapping

新しい Graph endpoint / permission は不要。

- `AppType: pkg`: selected entry が `primaryBundleId` / `primaryBundleVersion` になり、`includedApps` は selected entry を先頭に
  並べる。`BundleVersion` は `bundleVersion` に使う。
- `AppType: lob`: selected entry の `BundleId` を top-level `bundleId`、`BundleVersion`(`CFBundleShortVersionString`)を
  `buildNumber`、必須の `BundleBuildVersion`(`CFBundleVersion`)を `versionNumber` に設定する。`childApps` も selected entry を
  先頭に並べ、各 entry を `bundleId` / `buildNumber` / `versionNumber`へ対応付ける。

## 3層 stacked implementation PR

各 PR は直前の PR の head から作成し、1層のテストが通った状態を次層の base とする。層の下位で Graph mutation を導入せず、
上位層で安全境界を追加する。

### Layer 1: Manifest contract / mapper

- Detection model に nullable `PrimaryBundleId` と `BundleBuildVersion` を追加(初期値なし)。
- static validation(exact/segment prefix exactly-one、duplicate、1〜500件、lob build version required)を追加。
- pkg/lob payload mapping と selected-first ordering を追加。lob は top-level `bundleId` / `buildNumber` / `versionNumber`を正しく設定。
- existing pkg manifest の pinned hash compatibility、mapper/validation regression tests を追加。

### Layer 2: Bounded PKG inspection / package artifact

- SHA256検証後に XAR header/TOC/heap entry を bounded に読む `PkgBundleInspector` を追加。
- 上限、DTD/外部 entity禁止、unsupported/corrupt hard fail、bundle/version semantic warningを実装。
- package artifact metadata に content SHA256、inspection schema/version、detected bundles、selected primary、warning codes、force stateを保存。
- `package` の TTY/non-TTY/`--force` と deterministic XAR fixtures の CLI integration test を追加。Graph mutation は追加しない。

### Layer 3: Publish preflight / operation

- 全 batch の static validation・artifact hash・再 inspection・tenant検証を Graph mutation 前に完了させる。
- `publish` の TTY/non-TTY/`--force`、warning aggregation、hard fail 非回避、0-mutation failure boundary を追加。
- package/publish の CLI version pin、fake Graph payload contract、idempotent rerun、tampered artifact、semantic warning拒否を検証。
- protected manual real-tenant E2E で pkg/lob の create/update/read-back を行い、必要なら enrolled macOS device の detection/reporting まで確認。

## 対象外

- 既知 updater(`com.microsoft.autoupdate*` 等)を判定する組み込みリストや自動警告。
- pkg から bundle を自動抽出して manifest を自動生成すること。
- `IncludedApps` の暗黙フィルタ(除外は常に「書かない」ことで行う)。
- manifest ファイル自体の並べ替え(並べ替えは Graph payload 生成時のみ)。
- pkg payload(cpio.gz)の展開・実ファイルの検証。XAR の宣言情報が存在しない場合は semantic warning とし、自動補完は行わない。

## Acceptance criteria

- exact、segment-boundary prefix、0件、複数件、duplicate、501件、lob build version欠落を static test で検証する。
- valid XAR と、compressed/decompressed TOC、heap entry、bundle count、XML depth の各上限境界を test で検証する。
- malformed/truncated/unsupported/DTD XML は `--force` でも fail し、Graph mutation が発生しない。
- package は SHA256成功後にのみ inspectionし、publish は artifact再ハッシュ・再 inspection後に全件preflightを通過する。
- semantic warning の TTY確認、非対話 fail、`--force` 続行、warning/force metadataを CLI test で検証する。
- fake Graphで pkg/lob mapping と selected-first orderingを検証し、lobの3 top-level version fieldsを read-backする。
- protected manual E2E で実テナントへの create/update/idempotent rerunを行い、`--expected-tenant`不一致・warning拒否時に変更がないことを確認する。
