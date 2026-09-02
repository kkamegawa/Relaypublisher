# tools/yamlcreate.ps1 — manifest 作成 / バージョンアップスクリプト

## 1. 目的と位置づけ

`tools/yamlcreate.ps1` は、Relaypublisher の YAML manifest を対話プロンプトで作成し、既存 manifest を
新しいバージョンへ更新するためのスクリプトである。winget-pkgs の `Tools/YamlCreate.ps1` に相当する。

- schema の正本は [`doc/01-manifest-schema.md`](01-manifest-schema.md)。本スクリプトは入力補助であり、schema を定義しない。
- 検証の正本は `relaypublisher validate`。本スクリプトが保存前に行うチェックは早期フィードバックのための重複実装で、
  保存後に CLI の `validate` を自動実行する。
- バージョンアップ手順の正本は [`doc/05-operation.md`](05-operation.md) §4c。Update モードはその手順を機械化したもの。

前提は PowerShell 7.3 以降。Windows / macOS / Linux のいずれでも動作する(bash 版は用意しない。
対話 UI とハッシュ計算を二重に保守しないため)。

## 2. モード

| モード | 用途 |
|---|---|
| `New` | schema に沿ったプロンプトで manifest を新規作成する |
| `Update` | 既存 manifest を新しい `PackageVersion` へ更新する |

`-Mode` を省略した場合、`-Path` があれば `Update`、なければ対話で選択する。

## 3. パラメーター

| パラメーター | 対象モード | 説明 |
|---|---|---|
| `-Mode <New\|Update>` | 両方 | 省略時は `-Path` の有無、または対話で決定 |
| `-Path <path>` | Update | 既存の `*.yaml`、またはバージョンフォルダー(配下の `*.yaml` を一括更新) |
| `-PackageVersion <version>` | 両方 | Update では新バージョン(必須)。New では `PackageVersion` プロンプトの既定値 |
| `-Platform <windows\|macos>` | 両方 | New では最初の分岐を決定。Update では対象 manifest の絞り込み |
| `-Architecture <x64\|arm64>` | New | 省略時は対話で選択 |
| `-OutputDirectory <dir>` | 両方 | 出力先。省略時は §7 の既定レイアウト |
| `-RepoRoot <dir>` | 両方 | manifest 内の相対パスの基準。既定は `git rev-parse --show-toplevel` |
| `-GroupId <guid[]>` | New | 指定すると assignment プロンプトを出さず、各 GUID に `Intent: required` の include assignment を作る。複数指定はカンマ区切り(`-GroupId 'a','b'`)。`pwsh -File` 経由では空白区切りが配列として渡らないため、複数渡すときは `pwsh -Command` を使う |
| `-FilterId <guid>` | New | `-GroupId` で作る assignment に適用する assignment filter |
| `-FilterMode <include\|exclude>` | New | `-FilterId` 指定時の filter mode。既定 `include` |
| `-EntraGroupCsv <path>` | New | `tools/export-intune-entra.ps1` の `entra-groups.csv`。GUID を表示名から選べるようになる |
| `-AssignmentFilterCsv <path>` | New | 同 `assignment-filters.csv` |
| `-Sha256 <hash>` | Update | ダウンロードせずに digest を指定する。ソースが 1 つの manifest 1 ファイルにのみ適用可 |
| `-NoDownload` | 両方 | ネットワークに一切アクセスしない。digest はすべて手入力 |
| `-SkipValidate` | 両方 | 保存後の `relaypublisher validate` を実行しない |
| `-Force` | 両方 | 既存ファイルを上書きし、最終確認プロンプトを省略する |

`-WhatIf` / `-Confirm` にも対応する(ファイル書き込みのみが対象)。

## 4. New モード

### 4.1 最初に platform を確定する

最初のプロンプト(または `-Platform`)で `windows` / `macos` を確定し、以降のプロンプト集合を切り替える。
一方の platform でしか使えないフィールドは、質問もしないし出力もしない。

### 4.2 共通の top-level

| プロンプト | 必須 | 既定値 / 備考 |
|---|---|---|
| `PackageIdentifier` | 必須 | app identity の一部。バージョンをまたいで変更しない |
| `PackageName` | 必須 | |
| `Publisher` | 必須 | |
| `Description` | 必須 | |
| `PackageVersion` | 必須 | `-PackageVersion` が既定値になる |
| `Owner` | 任意 | 空で省略 |
| `Developer` | 任意 | 空で省略 |
| `InformationUrl` | 任意 | 空で省略 |
| `Icon` | 任意 | repository 相対パス。`AppType: lob` のときのみ必須。拡張子・1 MiB 上限・実在を検証 |
| `RoleScopeTagIds` | 任意 | カンマ区切り。各要素は必ずクォートして出力 |
| `AssignmentSync` | 任意 | 既定 `merge` |
| `DisplayName` | 必須 | 既定 `<PackageName> [Windows x64]` 形式。`PackageVersion` を含む値は拒否 |
| `Categories` | 任意 | 質問に `n` で答えるとキー自体を出力しない(既存の関連付けを変更しない)。`y` かつ空入力で `Categories: []` |

`SchemaVersion` は `"1.0"` 固定で、プロンプトは出ない。

### 4.3 Windows 固有

| プロンプト | 既定値 / 備考 |
|---|---|
| `Package.IntuneWin.SetupFile` | 既定 `install.ps1` |
| `Package.RepositoryFiles[]` | `Source`(repository 相対、実在確認)と `Destination` の対を 0..n |
| `Package.ExternalFiles[]` | §5 のソース item を 0..n |
| `Install.CommandLine` | 既定 `powershell.exe -ExecutionPolicy Bypass -File .\install.ps1` |
| `Install.UninstallCommandLine` | 既定 `powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1` |
| `Install.InstallExperience` | 既定 `system` |
| `Install.RestartBehavior` | 既定 `suppress` |
| `Install.ReturnCodes[]` | 任意。追加しなければキーごと出力せず、Intune 既定(0/1707 success、3010 softReboot、1641 hardReboot、1618 retry)に委ねる |
| `Detection.ScriptFile` | `Type: script` は固定。repository 相対、実在確認 |
| `Detection.RunAs32Bit` / `EnforceSignatureCheck` | 既定 false |
| `Requirements.MinimumOSVersion` | `WindowsReleaseTable` のキーのみを release 名付きで一覧提示。既定 `10.0.19045` |
| `Requirements.Architecture` | app の `Architecture` を自動設定 |

### 4.4 macOS 固有

| プロンプト | 既定値 / 備考 |
|---|---|
| `AppType` | 既定 `pkg`。`lob` を選ぶと top-level `Icon` が必須になる |
| `Source` | §5 のソース item を 1 つ |
| `Requirements.MinimumOSVersion` | `MacOsMinimumOperatingSystemTable` のキーのみを提示。`AppType: lob` では beta 専用の 14.0 / 15.0 / 26.0 を選択肢から除外する。常にクォートして出力する(裸の `14.0` は YAML が float として読み、version table のキーと一致しなくなる) |
| `Detection.IgnoreAppVersion` | 既定 false |
| `Detection.IncludedApps[]` | `BundleId` + `BundleVersion` を 1 件以上。`BundleVersion` の既定は `PackageVersion` |
| `Scripts.PreInstall` / `PostInstall` | `AppType: pkg` のときだけ質問する。`.sh` / 実在 / 15360 文字未満 / BOM なし / `#!` 開始を検証。両方空なら `Scripts` ブロックを出力しない |

## 5. ソース item と Sha256

`publicHttp` / `githubRelease` / `azureBlob` の統一 item shape(doc/01-manifest-schema.md §5.0.1)を
Windows の `ExternalFiles` 各項目と macOS の `Source` で共用する。

`Auth` の制約はスクリプトが強制する。

| Type | 選択できる `Auth.Type` |
|---|---|
| `publicHttp` | `none`(既定) / `token` |
| `githubRelease` | `none`(既定) / `token` |
| `azureBlob` | `workloadIdentity` 固定 |

`Auth.Type: token` では**環境変数名**を入力する。トークンの値は manifest にもログにも出力しない。

`Sha256` の取得経路は 3 つある(`-NoDownload` 指定時はいずれも行わず手入力)。

| Type | 取得方法 |
|---|---|
| `publicHttp` | `Url` を一時ディレクトリにダウンロードして `Get-FileHash -Algorithm SHA256` |
| `githubRelease` | release タグのアセット一覧を取得して `AssetName` を選択させ、そのアセットをダウンロードして計算。`Auth.Type: token` の場合は環境変数の値を Bearer に使う |
| `azureBlob` | 自動取得しない(workload identity が必要なため)。手入力にフォールバックし、`az storage blob download` の手順を案内する |

自動計算に失敗した場合は警告を出して手入力に切り替える。手入力値は 64 桁の 16 進数であることを検証する。
ダウンロードした一時ファイルは必ず削除する。

## 6. Assignments

`GroupId` / `FilterId` は**すべて任意**である。1 件も追加しなければ `Assignments: []` を出力する。これは
テナント固有の ID を含まない形なので、`plan` → `validate` → `package` → `publish --dry-run` まで
どのテナントでもそのまま通る。

| プロンプト | 既定値 / 備考 |
|---|---|
| `Target` | 既定 `group`。`allDevices` / `allLicensedUsers` では `GroupId` を出力しない |
| `GroupId` | `Target: group` のとき必須。GUID を検証 |
| `Mode` | 既定 `include` |
| `Intent` | `Mode: include` のとき必須。`AppType: pkg` では `uninstall` を選択肢から除外する |
| `FilterId` | 任意。指定したときのみ `FilterMode` を質問する(必須) |
| `Settings.Notifications` / `RestartGracePeriodMinutes` | Windows(win32)のときのみ |

同一 manifest 内で `Target` + `GroupId` + `Mode` が重複する assignment は追加しない。

`-EntraGroupCsv` / `-AssignmentFilterCsv` に [`tools/export-intune-entra.ps1`](../tools/export-intune-entra.ps1)
が出力した CSV を渡すと、GUID を直接入力する代わりに表示名から選択できる。CSV に想定する列名が無い場合は
警告を出して GUID の手入力に戻る。

`-GroupId` を指定した場合は assignment プロンプトを出さず、各 GUID に対して
`Target: group` / `Mode: include` / `Intent: required` の assignment を作る。`-FilterId` を併用すると
すべての assignment に同じ filter が付く。

## 7. 出力先

`-OutputDirectory` を指定した場合はそのフォルダーに書き込む。省略した場合の既定は次のとおり。

| モード | 既定の出力先 |
|---|---|
| New | `<RepoRoot>/manifests/<Publisher>/<PackageIdentifier>/<PackageVersion>/` |
| Update(元ファイルの親フォルダー名が旧バージョンと一致) | 元フォルダーの兄弟 `<...>/<新バージョン>/` |
| Update(それ以外) | `<RepoRoot>/manifests/<Publisher>/<PackageIdentifier>/<新バージョン>/` |

ファイル名は、New では `<packageidentifier>-<platform>-<architecture>.yaml`(小文字)、Update では元のファイル名。
既存ファイルは `-Force` を付けない限り上書きしない。

文字コードは UTF-8(BOM なし)。改行は New では LF、Update では**元ファイルの改行コードを保持**する
(ローカルの diff がバージョンアップ分だけになるようにするため)。

## 8. Update モード(バージョンアップ)

### 8.1 書き換える対象

行ベースで書き換えるため、コメント・キー順・書式はそのまま残る。

1. top-level `PackageVersion` を新バージョンにする。
2. `Url` / `Tag` / `AssetName` / `BlobName` / `Destination` / `BundleVersion` の各行のうち、値に旧バージョン
   文字列を含むものを置換する。`v7.6.4` のような `v` プレフィックス付きタグも置換される。
   `1.2` が `1.2.3` の一部として誤置換されないよう、前後が数字・ドットでない位置だけを対象にする。
3. すべての `Sha256` を再計算する。バージョンが変われば digest も必ず変わるため、更新漏れを許さない。
   `-NoDownload` / `-Sha256` / 自動取得の失敗時は手入力になる。
4. 置換後もまだ旧バージョン文字列を含む行(コメント内の記述など)を一覧で警告する。自動では書き換えない。
5. 変更行を旧 / 新の色付き diff で表示し、確認してから保存する(`-Force` で確認を省略)。

### 8.2 書き換えないもの

`PackageIdentifier` / `Platform` / `Architecture` / `DisplayName` は書き換えない。これらは app identity
そのもので、変更すると Intune 上に別アプリが作られ、既存アプリが取り残される(AGENTS.md 設計上の不変条件)。

`Requirements.MinimumOSVersion`、`Icon`、`Scripts`、`Assignments`、`Categories` も変更しない。新バージョンで
これらを変えたい場合は、生成後に手で編集する。

### 8.3 実行例

```powershell
# PowerShell 7.6.4 -> 7.6.5。フォルダー配下の *.yaml をまとめて更新し、兄弟の 7.6.5/ に出力する
./tools/yamlcreate.ps1 -Mode Update `
    -Path samples/manifests/Microsoft/Microsoft.PowerShell/7.6.4 `
    -PackageVersion 7.6.5
```

```powershell
# ネットワークを使わず、digest を明示して 1 ファイルだけ更新する
./tools/yamlcreate.ps1 -Mode Update `
    -Path manifests/Contoso/Contoso.Tool/1.2.3/contoso.tool-macos-arm64.yaml `
    -PackageVersion 1.2.4 -NoDownload -Sha256 <sha256> -Force
```

## 9. 保存前の検証

保存の直前に、Graph も CLI も使わずに次を検証する。いずれも `src/IntuneLobPublisher.Core/Validation/` の
対応するルールと同じ内容である。

- manifest 由来のパス(`Destination` / `Source` / `SetupFile` / `ScriptFile` / `Icon` / `Scripts.*`)の
  path traversal・絶対パス・ドライブレター
- `Sha256` が 64 桁の 16 進数であること
- `GroupId` / `FilterId` が GUID であること
- `DisplayName` が `PackageVersion` を含まないこと
- `Icon` の拡張子(`.png` / `.jpg` / `.jpeg`)、1 MiB 上限、実在
- macOS `Scripts` の `.sh` 拡張子、実在、15360 文字未満、BOM なし、`#!` 開始
- `AppType: lob` で `Icon` が指定されていること、macOS 14 以降を選べないこと
- `AppType: pkg` で `Intent: uninstall` を選べないこと

保存後、`relaypublisher` が PATH にあれば `relaypublisher validate --manifest <保存先> --repo-root <RepoRoot>`
を実行する。見つからない場合は実行すべきコマンドを表示する。`-SkipValidate` で抑止できる。

## 10. 制限事項

- YAML の完全なパースは行わない。Update モードは §8.1 のキーだけを対象とした行編集である。手で大きく崩した
  インデントや、フロースタイル(`{ }` / `[ ]`)で書かれた manifest は対象外。
- `AssignmentSync` の意味論(merge / replace)には関与しない。値を書き込むだけである。
- `Categories` に指定した名前がテナントに存在するかは検証できない。検出は publish / dry-run の Graph
  preflight で行われる(doc/01-manifest-schema.md §5.8)。
- `azureBlob` の `Sha256` は自動計算しない。
- Update モードは `Requirements` / `Assignments` / `Scripts` / `Categories` を変更しない。
