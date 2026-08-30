# macOS manifest の Architecture を任意化 (GitHub #122)

## Goal

Intune の macOS app リソース(`macOSPkgApp` / `macOSLobApp`)には architecture を表す Graph プロパティが存在せず、
`MacOsAppPayloadMapper` は `Architecture` を一切参照しない。それにもかかわらず現行の validation は
`Platform: macos` の app entry にも `Architecture` を必須とするため、universal binary の pkg を 1 つ配るだけの
利用者にとって意味のない必須項目になっている。

正本は GitHub issue #122 と `doc/00-overview.md` §3.2/§6.1。本ファイルは、実装を 3 つのサブ issue に分けるための
decision-complete な実装仕様である。

## 確定仕様

### Manifest contract

`Platform: macos` の app entry では `Architecture` を省略できる。省略時の実効値は `universal` とする。

```yaml
Apps:
  - Platform: macos
    # Architecture は省略可。省略時の実効値は universal。
    InstallerType: pkg
    AppType: pkg
    DisplayName: Contoso Tool [macOS]
```

| Platform | `Architecture` | 判定 |
|---|---|---|
| `windows` | 省略 | fail(現状どおり) |
| `windows` | `x64` / `arm64` | ok(現状どおり) |
| `windows` | `universal` | fail(Windows の allow-list には含めない) |
| `macos` | 省略(YAML に key なし) | ok — 実効値 `universal` |
| `macos` | `x64` / `arm64` / `universal` | ok |
| `macos` | `""` / 空白のみ | fail — 省略扱いにしない |

対象は `AppType: pkg`(`macOSPkgApp`)と `AppType: lob`(`macOSLobApp`)の両方。architecture はこの codebase では
`AppType` ではなく `Platform` 単位の概念であるため、AppType で挙動を分けない。

`SchemaVersion` は `"1.0"` のまま。ただし後述のとおり、`Requirements.Architecture` の変更は additive な緩和では
なく意図的な厳格化である。

### 実効値の解決(単一責任化)

`IntuneLobPublisher.Core.Manifests.AppArchitecture.Resolve(AppManifest)` という単一の resolver だけが
「`Platform: macos` かつ `Architecture` が `null`」を `"universal"` に解決する。app identity、staging
ディレクトリ名、management metadata(`notes`)、package metadata、publish 結果 JSON など、`Architecture` を
消費するすべての下流処理はこの resolver 経由で値を読み、raw の manifest field(`app.Architecture!` や
`Require(app.Architecture, ...)`、`?? ""` の直接参照)を使わない。

**null のみを「省略」として扱う。** `Architecture: ""` や空白のみの値は resolver では丸めず、そのまま
validation の allow-list チェックに渡して fail させる。

resolver は manifest load 時には適用されない。`ManifestLoader` が生成する model の `Architecture` は
YAML に書かれたとおり(省略時は `null`)であり、resolver は publish/staging/packaging の各処理が値を
必要とする時点で都度呼び出す。

### Hash 互換性(§6.7 の契約をそのまま適用)

- `AppManifest.Architecture` は `string?` のまま、loader のどこにも既定値を代入しない。canonical JSON は
  null property を落とすため、`Architecture: arm64` のようにすでに明示している既存 macOS manifest の
  `manifestHash` / `inputHash` は本変更の前後で **byte 単位で不変**。
- **resolver の結果を model に書き戻してはならない。** 書き戻すと、省略形 manifest の hash が CLI
  バージョン間で振動し、毎回 re-package / 再 upload(macOS pkg は最大 8 GB)が発生する。
- 帰結として、**省略** と **`Architecture: universal` の明示** は実効 identity(app identity・staging
  ディレクトリ名・notes)は同じだが、`inputHash` は**異なる**。両者を切り替えると 1 回だけ再 package /
  再 upload が起きる。
- `ManifestLoader` は `IgnoreUnmatchedProperties()` のため、省略形 manifest を実装前の CLI に食わせると
  `Architecture` 必須で fail する(hash 振動ではなく validation error)。省略形を使い始めたら CI と
  手元の CLI version を揃えること。

### Static validation

`validate` / `package` / `publish` はすべて、Graph 呼び出し前に次を検証する。

- `Platform: windows` の `Architecture`: 必須。allow-list は `["x64", "arm64"]`(現状どおり)。
- `Platform: macos` の `Architecture`: 任意(`null` 許容)。値がある場合のみ allow-list
  `["x64", "arm64", "universal"]` で検証する。
- `Requirements.Architecture` は `Platform: macos` の app entry にあれば **fail**(§5.4.2 の
  `Scripts` 禁止 rule と同じ形)。
  - これは **additive な緩和ではなく validation の厳格化**である。従来は macOS で
    `Requirements.Architecture` を書いても(app-level `Architecture` と一致していれば)受理されていた。
    このプロジェクトは初期開発段階にあり後方互換シムを追加する方針ではない(`AGENTS.md`)ため、
    厳格化そのものは許容するが、doc・adr にはこれを「緩和」ではなく明示的な「厳格化」として記録する。
  - 既存の「`Requirements.Architecture` は app-level `Architecture` と一致必須」rule
    (`ManifestValidator.cs` の `Ordinal` 比較)は `Platform: windows` に限定する。macOS entry で
    `Requirements.Architecture` を指定した場合に禁止 rule と一致 rule が二重にエラーを出さないようにする。
- repository 全体の identity 一意性 lint(`ManifestSetValidator`)は実効 architecture(resolver の出力)で
  判定する。macOS の 2 entry がともに `Architecture` を省略している場合、あるいは片方が省略・片方が
  `Architecture: universal` 明示の場合も、重複 identity として検出する。

### 既存 app からの移行

`Architecture: arm64` で publish 済みの macOS app の manifest から `Architecture` を削除すると、identity が
`arm64` → `universal` に変わる。既存の app 解決ルールの組み合わせで挙動が説明できる(doc/05-operation.md に
運用手順として追記する)。

1. `notes` の management metadata 照合は architecture が一致しなくなるため外れる。
2. `DisplayName` を変えていなければ `DisplayName` fallback が一致し、既存 app を adopt して `notes` を
   書き戻す(doc/00-overview.md §6.1)。同じ Intune app と assignment が維持される。
3. staging ディレクトリが変わり(`macos-arm64` → `macos-universal`)、`inputHash` も変わるため、
   1 回だけ再 package / 再 upload が発生する。
4. 同時に `DisplayName` も変えると fallback 照合が外れ、doc/06-troubleshooting.md に既出の identity
   drift と同じ形で新規 Intune app が作られる。

旧 version folder を残したまま全 manifest を選択すると、旧 `arm64` entry と新 `universal` entry は
identity が異なるため、identity 単位の最高 version 選択だけでは両方が publish 対象に残る。両者の
`DisplayName` が同じ場合、処理順によって旧 entry が DisplayName fallback で同じ app を再 adopt し、
metadata が無い扱いで downgrade guard を回避できる。これを防ぐため、publish は identity 単位の選択後、
同じ `PackageIdentifier + Platform + DisplayName`、異なる実効 architecture、かつ `universal` を含む macOS
候補を移行 alias として最高 `PackageVersion` の 1 entry へ collapse する。同一 version では `universal` を
優先し、`universal` を含まない x64/arm64 の組み合わせは対象外とする。この選択は preflight と Graph 呼び出し
より前に完了する。

## 3 つのサブ issue

各 issue は同一の feature branch に対する非 stacked の通常 PR として着地させる(規模が小さく、層を分ける
必要がないため)。

1. **#123 — 実効値の解決と validation**: `AppArchitecture.Resolve`、`ManifestValues` の allow-list 分離、
   `ManifestValidator` の platform 条件付き rule、`Requirements.Architecture` の macOS 禁止・Windows 限定化、
   `ManifestSetValidator` の実効値ベースの一意性判定、pinned hash test。
2. **#124 — staging / packaging / publish への伝播**: `PublishOrchestrator`、`PublishResultOutput`
   (`FromResult`/`FromFailure` が raw の `Architecture` を `Require` していたため、macOS 省略形 entry の
   publish 成功後に結果 JSON 生成で例外になっていた欠陥の修正を含む)、`MacOsStagingService`、
   `MacOsPackager`、`PublishCommand.cs` の各 call site(`?? ""` による `"architecture": ""` 出力の修正を含む)。
3. **#125 — ドキュメント**: 本ファイル、manifest schema、overview の設計判断、運用・トラブルシューティング
   ガイド、sample README、ADR。

## 対象外

- `Platform: windows` の architecture 挙動の変更、および Windows での `universal` 受け入れ。
- pkg 実体から architecture(universal / single-arch)を自動判定すること。
- Intune 側での macOS architecture ターゲティング(Graph に該当プロパティが無いため対象外)。
- 既存 sample manifest の省略形への書き換え、および既存 publish 済み Intune app の自動移行処理。
- `macOSDmgApp` など未対応の Graph app type の追加。

## Acceptance criteria

- macOS の省略/`universal`/`x64`/`arm64`、Windows の省略/`universal`/`x64`/`arm64`、macOS の空文字・空白のみを
  static test で検証する。
- macOS `Requirements.Architecture` 指定時に禁止 rule 由来のエラーが 1 件だけ出ることを検証する。
- 省略形 macOS entry 2 件、または省略形と `universal` 明示の組み合わせが repository-wide 一意性 lint で
  重複として検出されることを検証する。
- 既存 macOS manifest(`Architecture` 明示)の `manifestHash`/`inputHash` が本変更の前後で pinned test により
  不変であることを検証する。省略形と `universal` 明示の hash が異なることも検証する。
- `PublishResultOutput.FromResult`/`FromFailure` が macOS 省略形 entry で例外を投げず、結果 JSON の
  `architecture` が `"universal"` になることを検証する。
- staging / packaging が省略形 entry を `macos-universal` として扱い、staging 結果と manifest entry の
  突合が実効値ベースで成功することを検証する。
- 同じ `PackageIdentifier + Platform + DisplayName` の旧明示 architecture / 新 `universal` entry が同時に
  publish 対象となっても、入力順に関係なく最高 `PackageVersion` の 1 entry に collapse されること、同一
  version では `universal` が選ばれること、`universal` を含まない x64/arm64 は別 entry のままであることを
  検証する。
