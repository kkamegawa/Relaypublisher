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
      IncludedApps:
        - BundleId: com.contoso.tool
          BundleVersion: 1.2.3

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

- `IncludedApps` は 1 件以上必須。先頭要素がレポート表示に使われる。
- `AppType: pkg` の app に `Intent: uninstall` があれば fail。
- `AppType: lob` の場合、top-level `Icon` を必須とする。
- `AppType: lob` または `Platform: windows` の app entry に `Scripts` があれば fail(§5.4.2)。

content upload の Graph URL は app の具体的な OData 型でキャストする。`contentVersions` は
`mobileLobApp` から継承されるため、型キャストを省略した `/mobileApps/{id}/contentVersions` は
Graph によって解決できず、`Resource not found for the segment 'contentVersions'`(HTTP 400)になる。
`AppType: pkg` は beta の `microsoft.graph.macOSPkgApp`、`AppType: lob` は v1.0 の
`microsoft.graph.macOSLobApp`、Windows は v1.0 の `microsoft.graph.win32LobApp` を使用する。
content version の作成後も、files、状態取得、`renewUpload`、`commit` に同じキャストを付ける。

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
| `MinimumOSVersion: "14.0"` | `minimumSupportedOperatingSystem` | boolean flag の複合型。v1.0 は `v13_0` までしか無く、macOS 14/15 のフラグは beta 専用。`AppType: lob`(v1.0)で 14 以降を指定すると fail する |
| `Detection.IncludedApps`(`AppType: pkg`) | `includedApps`(`macOSIncludedApp`: `bundleId` + `bundleVersion`) | 先頭要素の値がそのまま `primaryBundleId` / `primaryBundleVersion` にもなる |
| `Detection.IncludedApps`(`AppType: lob`) | `childApps`(`macOSLobChildApp`: `bundleId` + `buildNumber` + `versionNumber`)。先頭要素が top-level `buildNumber` / `versionNumber` にもなる | `pkg` の `includedApps` とはフィールド名・形が異なる点に注意 |

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
