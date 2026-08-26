# Manifest Schema

## 5. YAML schema 案

### 5.0 Schema versioning

すべての manifest は top-level に `SchemaVersion` を必須で持つ。

```yaml
SchemaVersion: "1.0"
```

- 互換性のない変更では major を上げる。
- CLI は未知の major version の manifest を fail とする。

### 5.0.1 ソース指定の統一形式

外部ファイル取得(Windows の `ExternalFiles` 各項目、macOS の `Source`)は同一の item shape を使う。

```yaml
Type: publicHttp | githubRelease | azureBlob
Destination: <staging 内の相対パス>
Sha256: "<sha256>"        # 必須
Auth:                     # publicHttp では省略可
  Type: none | token | workloadIdentity
  SecretName: <環境変数名> # Type: token のとき必須。CI の secret を環境変数として渡す
```

type 固有フィールド:

| Type | フィールド |
|---|---|
| `publicHttp` | `Url` |
| `githubRelease` | `Owner`, `Repository`, `Tag`, `AssetName` |
| `azureBlob` | `AccountName`, `Container`, `BlobName` |

### 5.1 Windows x64 例

```yaml
SchemaVersion: "1.0"
PackageIdentifier: Contoso.Tool
PackageName: Contoso Tool
Publisher: Contoso Ltd.
Description: Internal tool for Contoso employees.
PackageVersion: 1.2.3
AssignmentSync: merge

# Optional app information (Intune mobileApp properties)
Owner: IT Department
Developer: Contoso Ltd.
InformationUrl: <internal-info-url>
Icon: assets/icons/contoso-tool.png        # repository 相対パス。largeIcon として登録
RoleScopeTagIds:                           # RBAC scope tag を使う組織向け(任意)
  - "0"

Apps:
  - Platform: windows
    Architecture: x64
    InstallerType: win32
    DisplayName: Contoso Tool [Windows x64]

    Package:
      IntuneWin:
        SetupFile: install.ps1

      RepositoryFiles:
        - Source: scripts/windows/x64/install.ps1
          Destination: install.ps1
        - Source: scripts/windows/common/uninstall.ps1
          Destination: uninstall.ps1
        - Source: scripts/windows/common/detect.ps1
          Destination: detect.ps1

      ExternalFiles:
        - Type: publicHttp
          Url: https://example.com/downloads/contoso-tool-1.2.3-x64.exe
          Destination: bin/contoso-tool.exe
          Sha256: "<sha256>"

    Install:
      CommandLine: powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
      UninstallCommandLine: powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1
      InstallExperience: system
      RestartBehavior: suppress
      # 省略時は Intune 既定セットを適用:
      #   0 success / 1707 success / 3010 softReboot / 1641 hardReboot / 1618 retry
      ReturnCodes:
        - Code: 0
          Type: success
        - Code: 3010
          Type: softReboot

    Detection:
      Type: script
      ScriptFile: scripts/windows/common/detect.ps1
      RunAs32Bit: false
      EnforceSignatureCheck: false

    Requirements:
      MinimumOSVersion: 10.0.19045
      Architecture: x64

    Assignments:
      - Target: group
        GroupId: "00000000-0000-0000-0000-000000000001"
        Intent: required
      - Target: group
        GroupId: "00000000-0000-0000-0000-000000000002"
        Intent: available
```

### 5.2 Windows Arm64 例

```yaml
  - Platform: windows
    Architecture: arm64
    InstallerType: win32
    DisplayName: Contoso Tool [Windows Arm64]

    Package:
      IntuneWin:
        SetupFile: install.ps1

      RepositoryFiles:
        - Source: scripts/windows/arm64/install.ps1
          Destination: install.ps1
        - Source: scripts/windows/common/uninstall.ps1
          Destination: uninstall.ps1
        - Source: scripts/windows/common/detect.ps1
          Destination: detect.ps1

      ExternalFiles:
        - Type: githubRelease
          Owner: contoso
          Repository: internal-tools
          Tag: v1.2.3
          AssetName: contoso-tool-1.2.3-arm64.exe
          Destination: bin/contoso-tool.exe
          Sha256: "<sha256>"
          Auth:
            Type: token
            SecretName: GH_RELEASE_PAT

    Install:
      CommandLine: powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
      UninstallCommandLine: powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1
      InstallExperience: system
      RestartBehavior: suppress

    Detection:
      Type: script
      ScriptFile: scripts/windows/common/detect.ps1
      RunAs32Bit: false
      EnforceSignatureCheck: false

    Requirements:
      MinimumOSVersion: 10.0.22621
      Architecture: arm64

    Assignments:
      - Target: group
        GroupId: "00000000-0000-0000-0000-000000000001"
        Intent: required
```

### 5.3 macOS 例

```yaml
  - Platform: macos
    Architecture: arm64
    InstallerType: pkg
    AppType: pkg          # pkg (既定: unmanaged macOS PKG app) | lob (macOS LOB app)
    DisplayName: Contoso Tool [macOS Arm64]

    Source:
      Type: azureBlob
      AccountName: contosopackages
      Container: intune-packages
      BlobName: macos/contoso-tool/1.2.3/contoso-tool-arm64.pkg
      Destination: contoso-tool-arm64.pkg
      Sha256: "<sha256>"
      Auth:
        Type: workloadIdentity

    Requirements:
      MinimumOSVersion: "14.0"

    Detection:
      # bundleId + version のリストで判定する
      IgnoreAppVersion: false
      # 任意。複数 bundle を含む pkg で先頭要素以外を primary にしたい場合に指定する(§5.4.3)
      # PrimaryBundleId: com.contoso.tool
      IncludedApps:
        - BundleId: com.contoso.tool
          BundleVersion: 1.2.3
          # AppType: lob の場合は BundleBuildVersion(CFBundleVersion)も必須。
          # AppType: pkg では省略可で、指定しても Graph mapping では使用しない。

    # 任意。AppType: pkg のみ(§5.4.2 参照)
    Scripts:
      PreInstall: scripts/macos/contoso-tool/preinstall.sh
      PostInstall: scripts/macos/contoso-tool/postinstall.sh

    Assignments:
      - Target: group
        GroupId: "00000000-0000-0000-0000-000000000003"
        Intent: required
```

### 5.4 macOS app type 制約

| | `AppType: lob` (`macOSLobApp`) | `AppType: pkg` (`macOSPkgApp`, 既定) |
|---|---|---|
| 署名 | Developer ID Installer 署名必須 | 未署名可 |
| サイズ上限 | 2 GB | 8 GB |
| Icon | 必須 | 任意 |
| uninstall intent | 可 | 非対応 |
| pre/post install script | 不可 | 可(`Scripts`、§5.4.2) |

validation ルール:

- `IncludedApps` は 1〜500 件必須。`BundleId` は Ordinal・大文字小文字区別で重複不可とする。先頭要素
  (`PrimaryBundleId` 指定時は一致した entry、§5.4.3)がレポート表示に使われる。500 件は Graph の
  `macOSPkgApp.includedApps` 上限に合わせる。
- `AppType: pkg` の app に `Intent: uninstall` があれば fail。
- `AppType: lob` の場合、top-level `Icon` を必須とする。
- `AppType: lob` または `Platform: windows` の app entry に `Scripts` があれば fail(§5.4.2)。
- `AppType: lob` の各 `IncludedApps` entry は `BundleBuildVersion`(`CFBundleVersion`)を必須とする。
  `AppType: pkg` では任意で、指定されても Graph mapping と PKG inspection の version 比較では使用しない。
- `Detection.PrimaryBundleId`(任意)は `IncludedApps` のちょうど 1 entry に一致(完全一致または `<値>.` 前置一致)
  しなければ fail。`Platform: windows` の app entry に指定すれば fail(§5.4.3)。

content upload の Graph URL は app の具体的な OData 型でキャストする。`contentVersions` は
`mobileLobApp` から継承されるため、型キャストを省略した `/mobileApps/{id}/contentVersions` は
Graph によって解決できず、`Resource not found for the segment 'contentVersions'`(HTTP 400)になる。
`AppType: pkg` は beta の `microsoft.graph.macOSPkgApp`、`AppType: lob` は v1.0 の
`microsoft.graph.macOSLobApp`、Windows は v1.0 の `microsoft.graph.win32LobApp` を使用する。
content version の作成後も、files、状態取得、`renewUpload`、`commit` に同じ具体型のキャストを付ける。
中断状態の復旧は package metadata が一致する既存 file の `renewUpload` で行い、content version / file の
DELETE や PATCH には依存しない。不一致 file が残る場合は追加 file を作成せず安全に fail する。

### 5.4.1 Icon の制約(issue #63)

top-level `Icon`(§2 参照)には次の制約がある。`validate` / `package` / `publish` はすべて、Graph 呼び出し前にこれらを検証する。

- 形式: `.png` / `.jpg` / `.jpeg` のみ(大文字小文字は区別しない)。それ以外の拡張子は fail。
- サイズ: 1 MiB(1,048,576 バイト)以下。超過は fail。
- 存在: `--repo-root` からの相対パスにファイルが実在すること。存在しなければ fail。
- `AppType: lob` の app が 1 つでもある場合、top-level `Icon` は必須(§5.4)。

自動リサイズ・変換は行わない。要件を満たす画像を事前に用意すること。

### 5.4.2 pre/post install script の制約(issue #86)

`AppType: pkg` の app entry は、任意で `Scripts` ブロックを持てる。Graph `macOSPkgApp` の
`preInstallScript` / `postInstallScript`(型 `macOSAppScript`、`scriptContent` は base64 エンコードされた
shell script)に対応する。`macOSLobApp` / `macOSDmgApp` にはこのプロパティ自体が存在しない。

```yaml
Scripts:
  PreInstall: scripts/macos/contoso-tool/preinstall.sh   # 任意
  PostInstall: scripts/macos/contoso-tool/postinstall.sh # 任意
```

- 値は `--repo-root` からの相対パス(`Icon` / `Detection.ScriptFile` と同じ扱い)。
- `PreInstall` / `PostInstall` は片方だけの指定も可。ただし `Scripts` ブロックがある場合、両方 null は fail。

validation ルール(`validate` / `package` / `publish` はすべて、Graph 呼び出し前にこれらを検証する):

- `Platform: windows` または `AppType: lob` に `Scripts` があれば fail。
- パスが path traversal / 絶対パスであれば fail。
- 拡張子が `.sh` 以外であれば fail。
- `--repo-root` からの相対パスにファイルが実在しなければ fail。
- 15360 文字(Graph の documented limit)以上であれば fail。
- UTF-8 BOM 付きであれば fail(shebang の前に BOM があると起動しない)。
- shebang(`#!`)で始まらなければ fail。

publish 時の挙動:

- スクリプト本文は決定的 **inputHash には含めない**(`Icon` / `Detection.ScriptFile` と同じ前例)。app
  メタデータの更新は publish のたび無条件に実行されるため、スクリプトのみの変更は `.pkg` の再アップロードを
  伴わずに反映される。
- 改行コードは base64 化の直前に CRLF / CR → LF へ正規化する(正規化が発生した場合は情報ログを出す)。
- `plan --base-ref` の changed detection は `scripts/**` の変更も対象 manifest の逆引きに含める。

運用前提(doc/05-operation.md も参照):

- Intune management agent for macOS **2309.007 以降**が必要。
- pre-install script が非 0 終了で app は "failed" となり、次回 device check-in で再試行される。
- post-install script の失敗は報告されない(app は "success" のまま)。

### 5.4.3 Detection primary bundle の選定(GitHub #112)

macOS PKG は 1 つの pkg に複数の app bundle を含むことがある(例: Global Secure Access クライアントが
Microsoft AutoUpdate を同梱)。既定では `IncludedApps` の**先頭要素**が Graph の `primaryBundleId` /
`primaryBundleVersion`(`AppType: lob` では top-level `bundleId` / `buildNumber` / `versionNumber`)になり、検出・
レポートに使われる。任意の `Detection.PrimaryBundleId` で、先頭以外の entry を primary に指定できる。

```yaml
Detection:
  IgnoreAppVersion: true                                # self-updating な primary に推奨
  PrimaryBundleId: com.microsoft.globalsecureaccess     # 完全一致 または セグメント境界の前置一致
  IncludedApps:
    - BundleId: com.microsoft.globalsecureaccess.client # 例示。実際の値は実機で確認する
      BundleVersion: 1.2.3
      # AppType: lob の場合は BundleBuildVersion(CFBundleVersion)も必須。
```

**マッチ規則**: `entry.BundleId == PrimaryBundleId` または `entry.BundleId` が `PrimaryBundleId + "."` で
始まる(Ordinal、大文字小文字区別)。`com.microsoft.global` のような不完全な前置きが
`com.microsoft.globalsecureaccess.*` に誤爆しないよう、区切り文字 `.` を含めて比較する。

validation ルール(`validate` / `package` / `publish` はすべて、Graph 呼び出し前にこれらを検証する):

- 省略時は現行どおり `IncludedApps[0]` が primary。挙動・`inputHash` とも変更なし。
- `Platform: windows` の app entry に指定すれば fail。
- 空文字・空白のみであれば fail。
- `IncludedApps` は 1〜500 件、`BundleId` は Ordinal・大文字小文字区別で重複不可とする。
- 上記マッチ規則で `IncludedApps` に一致する entry が **0 件** なら fail(候補の BundleId 一覧をエラーに含める)。
- 一致する entry が **2 件以上**(prefix の曖昧一致)なら fail(より長い/完全な bundle id の指定を促す)。
- `IgnoreAppVersion` とは独立に併用可能。`PrimaryBundleId` は「どの app を primary にするか」、
  `IgnoreAppVersion` は「primary のバージョンを検出に使うか」という別軸の設定。
- `AppType: lob` の各 entry は `BundleBuildVersion`(`CFBundleVersion`)が必須。`AppType: pkg` では任意で、
  指定されても Graph mapping と PKG inspection の version 比較では使用しない。
- `AppType: lob`(`childApps`)にも同じ primary 意味論を適用する。一致した entry は `childApps` の先頭へ並べ、
  選択した `bundleId` を top-level `bundleId`、`BundleVersion`(`CFBundleShortVersionString`)を `buildNumber`、
  `BundleBuildVersion`(`CFBundleVersion`)を `versionNumber` に反映する。各 child entry も同じ対応で生成する。

**同梱 updater の除外**: `IncludedApps` に**書かないことで除外する**。Microsoft Learn の
[Add an Unmanaged macOS PKG App to Microsoft Intune](https://learn.microsoft.com/intune/app-management/deployment/add-unmanaged-pkg-macos#step-4-%E2%80%93-detection-rules)
は、`includedApps` には実際にインストールされる app のみを列挙すること、含まれる app が 1 つでも
インストールされなければ install status が success を報告しないことを明記している。既知 updater(例:
`com.microsoft.autoupdate2`)を自動判定して除外する仕組みは対象外(下記)。

**pkg 実体の検査(package / publish フェーズ)**: `validate` は schema、manifest 内の重複・件数、selector、path
などの静的検証だけを行い、source を download しない。今回 `filePath` source は追加しない。PKG を取得する
`package` フェーズで、宣言された source SHA256 を検証して一致した直後に inspection を一度行い、`publish` では
artifact の実ファイルを再ハッシュしてから再 inspection する。

PKG(xar アーカイブ)の inspection は XAR header、compressed TOC、TOC が指す heap entry を bounded reader で読む。
TOC の `Distribution` / `PackageInfo` entry 本体から `<bundle id="..." CFBundleShortVersionString="..."
CFBundleVersion="...">` を読み取り、同梱 bundle 一覧(bundle id + version)を検査する。Payload(cpio.gz)の展開や
`pkgutil` への依存は不要で、.NET 標準ライブラリだけを使用する。compressed TOC は 16 MiB、decompressed TOC は
64 MiB、1 heap entry は 16 MiB、bundle は 4,096 件、XML depth は 64 を上限とする。DTD/外部 entity は禁止する。
未知の compression、header/offset/length の不整合、切り詰め、展開・XMLエラー、上限超過は hard fail とし、
`--force` でも回避できない。

検査の結果、次のいずれかに該当すれば semantic warning とする。

| 条件 | 挙動 |
|---|---|
| pkg 内に複数 bundle があり `PrimaryBundleId` 省略 | warning: 検出した bundle 一覧と、先頭要素で検出される旨を表示 |
| `IncludedApps` / `PrimaryBundleId` の bundle id が pkg 実体に存在しない | warning: 取り違え・typo の可能性を表示 |
| manifest の `BundleVersion` と `CFBundleShortVersionString` が不一致 | warning: stale な検出値の可能性を表示 |
| `AppType: lob`で `BundleBuildVersion` と `CFBundleVersion` が不一致 | warning: stale な build 値の可能性を表示 |
| `PrimaryBundleId` 指定済みで一致 bundle が pkg 実体にも存在 | 警告なし |

`IgnoreAppVersion` は Graph で version detection を無視する指定であり、manifest と PKG 実体の version mismatch
warning を抑止しない。検査結果には content SHA256、inspector version、検出 bundle、selector で解決した primary、
warning code、`force` 使用有無を記録する。source URL、token、署名付き URL は記録しない。

| 実行環境 | 挙動 |
|---|---|
| 対話実行(TTY) | warning 表示後、続行確認を求める。拒否時は exit code 非 0 で中断 |
| `package --force` / `publish --force` | semantic warning のみ確認せず warning ログで続行。hard fail は回避不可 |
| `--force` なしの非対話環境(TTY なし) | 安全側に倒して fail し、`--force` の付与を促すメッセージを出す |

`publish` は全対象 entry の static validation、artifact の存在、期待 SHA256 と package metadata の SHA256 の一致、
XAR inspection、semantic warning の承認、tenant 検証を全件完了してから Graph の create/upload/PATCH/assignment を開始する。
1 件でも hard fail または未承認 warning があれば Graph mutation は 0 件とする。package の inspection report が欠落・古い・
content SHA256 と一致しない場合は report を信頼せず、publish 前に実ファイルを再ハッシュして再 inspection する。

**hash 互換性**(§6.20 Categories と同じ契約): `Detection.PrimaryBundleId` と `BundleBuildVersion` は nullable・
既定値なしとする。`InputHashCalculator` の canonical JSON は null property を落とすため、これらを宣言していない
既存 pkg manifest の `inputHash` はこの変更の前後で byte 単位で不変。`BundleBuildVersion` は lob で指定すると
hash が変わり、pkg で指定しても値は Graph mapping に使用しない。`PrimaryBundleId` を指定すると hash が変わり、
次の `package` / `publish` で再 package / 再 upload が発生する(detection 修正のための意図的な republish)。
`ManifestLoader` は `IgnoreUnmatchedProperties()` のため、新旧 CLI を交互に実行すると `inputHash` が振動して毎回
upload が発生する。新フィールドを使い始めたら CI と手元の CLI バージョンを揃えること。`SchemaVersion` は `"1.0"` のまま。

対象外:

- 既知 updater(`com.microsoft.autoupdate*` 等)を判定する組み込みリストや自動警告。
- pkg から bundle を自動抽出して manifest を自動生成すること。
- `IncludedApps` の暗黙フィルタ(除外は常に「書かない」ことで行う)。
- manifest ファイル自体の並べ替え(並べ替えは Graph payload 生成時のみ)。
- PKG payload(cpio.gz)の展開・実ファイルの検証(TOC の宣言情報のみを信頼する)。

### 5.5 Assignment schema

```yaml
Assignments:
  - Target: group             # group | allDevices | allLicensedUsers
    GroupId: "<guid>"         # Target: group のとき必須。それ以外では指定不可
    Mode: include             # include (既定) | exclude
    Intent: required          # required | available | uninstall
    FilterId: "<guid>"        # 任意。assignment filter の GUID
    FilterMode: include       # FilterId 指定時必須。include | exclude
    Settings:                 # 任意。win32 のみ
      Notifications: showAll  # showAll | showReboot | hideAll
      RestartGracePeriodMinutes: 1440
```

validation ルール:

- `Target: group` は `GroupId` が有効な GUID であること。
- `Target: allDevices` / `allLicensedUsers` に `GroupId` があれば fail。
- `Mode: exclude` に `Intent` の意味はない(除外対象)。`Intent` は include 側のみ有効。
- 同一 manifest 内で同じ target が重複したら fail。

### 5.6 Merge / Replace の意味論

Intune は同一グループに複数 intent を持てないため、merge を「グループ単位の upsert」と定義する。

- `merge`(既定):
  - manifest にある target は追加、既存なら **intent / settings / filter を manifest の値で更新**する(intent 競合時は manifest が勝つ)。
  - manifest にない既存 assignment は**削除しない**。
- `replace`:
  - manifest の assignment 一覧を正として完全同期する。manifest にない既存 assignment は**削除**する。
  - 事故防止のため dry-run diff の確認を推奨。

### 5.7 Requirements → Graph mapping

Windows:

| manifest | Graph (win32LobApp) | 備考 |
|---|---|---|
| `Architecture: x64` | `allowedArchitectures: x64` | |
| `Architecture: arm64` | `allowedArchitectures: arm64` | v1.0 の `applicableArchitectures` enum に `arm64` は**存在しない**。`allowedArchitectures` を使用し、その場合 `applicableArchitectures` は `none` になる |
| `MinimumOSVersion: 10.0.19045` | `minimumSupportedWindowsRelease: Windows10_22H2` | build 番号 → release 名のマッピングテーブルを Core に持つ。未知の build は fail |
| `MinimumOSVersion: 10.0.22621` | `minimumSupportedWindowsRelease: Windows11_22H2` | |

macOS:

| manifest | Graph | 備考 |
|---|---|---|
| `MinimumOSVersion: "14.0"` | `minimumSupportedOperatingSystem` | boolean flag の複合型。v1.0 は `v13_0` までしか無く、macOS 14/15/26 のフラグは beta 専用。`AppType: lob`(v1.0)で 14 以降を指定すると fail する |
| `Detection.IncludedApps`(`AppType: pkg`) | `includedApps`(`macOSIncludedApp`: `bundleId` + `bundleVersion`) | 選択した先頭要素(`PrimaryBundleId` 指定時は一致した entry、§5.4.3)の `BundleId` / `BundleVersion` が `primaryBundleId` / `primaryBundleVersion` になり、payload では先頭へ並べ替えられる。`BundleBuildVersion` は省略可で、指定しても無視する |
| `Detection.IncludedApps`(`AppType: lob`) | `childApps`(`macOSLobChildApp`: `bundleId` + `buildNumber` + `versionNumber`) | 選択した先頭要素(`PrimaryBundleId` 指定時は一致した entry)を先頭へ並べ替える。選択 entry の `BundleId` は top-level `bundleId`、`BundleVersion`(`CFBundleShortVersionString`)は `buildNumber`、必須の `BundleBuildVersion`(`CFBundleVersion`)は `versionNumber` にも設定する。`pkg` の `includedApps` とはフィールド名・形が異なる点に注意 |

### 5.8 Categories(Intune app category / GitHub #99)

`Categories` は `Apps[]` 配下の任意フィールドで、その app entry を関連付ける Intune app category の
`displayName` を列挙する。platform / architecture ごとに Intune app が分かれるため、カテゴリも app entry ごとに
独立して指定する。

```yaml
Apps:
  - Platform: windows
    Architecture: x64
    Categories:
      - Business Apps
      - Productivity
```

意味論は次のとおり。`Categories` は **nullable** な model(省略時は `null`)であり、省略と空配列は別物として扱う。

| Manifest | 動作 |
|---|---|
| `Categories` 省略 | 既存の app-category relationship を変更しない。category 関連の Graph 呼び出しを一切行わない |
| `Categories: []` | 既存の app-category relationship をすべて解除する(desired set が空集合) |
| 1 件以上 | 指定されたカテゴリ集合に完全同期する(未指定の既存 relationship は解除) |

カテゴリ名は tenant 内の `mobileAppCategory.displayName` を正本とする。tenant 固有の category ID は manifest に
保存しない。カテゴリそのものの作成・改名・削除は対象外で、Relaypublisher は app との relationship だけを操作する。

ローカル validation(`validate`)は Graph に接続せず、次のみを検証する。

- 各要素が空文字・空白のみでないこと。
- 各要素が前後に空白を持たないこと(名前は trim も Unicode 正規化もしない)。
- 同一 app entry 内で `OrdinalIgnoreCase` の重複がないこと。
- 件数上限・文字数上限・使用可能文字の制限は設けない。

そのため、**tenant に存在しないカテゴリ名は `validate` では検出できない**。検出は publish / dry-run の Graph
preflight(§6.20)で行われる。

`SchemaVersion` は `"1.0"` のまま(additive optional field)。`Categories` を宣言していない既存 manifest の
`manifestHash` / `inputHash` は本変更の前後で変わらない(§6.7、doc/00-overview.md §6.20)。

---
