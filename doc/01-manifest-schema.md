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
| pre/post install script | 不可 | 可 |

validation ルール:

- `IncludedApps` は 1 件以上必須。先頭要素がレポート表示に使われる。
- `AppType: pkg` の app に `Intent: uninstall` があれば fail。
- `AppType: lob` の場合、top-level `Icon` を必須とする。

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
| `MinimumOSVersion: "14.0"` | `minimumSupportedOperatingSystem` | |
| `Detection.IncludedApps` | `includedApps` (bundleId + version) | |

---
