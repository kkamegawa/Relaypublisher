# サンプル manifest

このディレクトリには 2 種類の manifest が混在しています。

- **E2E 実行可能なサンプル**: 実在する、公開ダウンロード可能な package を指しています。無編集で `plan` → `validate` → `package` が通ります（`publish` にはテスト tenant、`Assignments` を追加する場合は実在する group GUID が別途必要です。[../../doc/07-local-e2e_ja.md](../../doc/07-local-e2e_ja.md) を参照してください)。
- **参照専用のサンプル**: schema の形や制約を示すためのものです。実世界の制約を記録するために、意図的に `validate` や `package` で失敗するように書かれているものもあります。バグだと思う前に、ファイル内のコメントを読んでください。

以下のコマンドはすべて `--repo-root samples` を付けています。これらの manifest 内の `RepositoryFiles.Source` / `Icon` のパスは、リポジトリルートではなくこの `samples/` ディレクトリからの相対パスだからです(`scripts/windows/...` は `<repo-root>/scripts/windows/...` ではなく `samples/scripts/windows/...` を指します)。同じ manifest に対して `plan`・`validate`・`package`・`publish` を実行するときは `--repo-root samples` を一貫させてください。`manifest-list.json` に記録される manifest path は、`plan` 実行時に渡した `--repo-root` を基準に解決されます。

`Microsoft/Microsoft.PowerShell/` は、実際のリポジトリ運用者が使うべきバージョン別フォルダ構成
(doc/00-overview.md §6.8、doc/05-operation.md §4c)を採用しています。`PackageVersion` ごとに
`<Publisher>/<PackageIdentifier>/<version>/` 配下の別フォルダを持ち、旧バージョンフォルダは上書きせず残します。
他のサンプルは単一の schema の形や制約を示すためのものでバージョンアップのライフサイクルを表すものではないため、
`manifests/` 直下にフラットなまま置いています。

| Manifest | 位置づけ | `validate` | `package` | 備考 |
|---|---|---|---|---|
| `Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml` | E2E 実行可能・現行バージョン | 通る | 通る(GitHub から約 68 MB をダウンロードし SHA-256 を検証) | 詳細は下記。`Scripts.PreInstall`/`PostInstall`(§5.4.2)のサンプルでもある |
| `Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-x64.yaml` | E2E 実行可能・現行バージョン | 通る | 通る(GitHub から約 73 MB をダウンロードし SHA-256 を検証) | 詳細は下記。`Scripts.PreInstall`/`PostInstall`(§5.4.2)のサンプルでもある |
| `Microsoft/Microsoft.PowerShell/7.6.4/powershell-macos-arm64.yaml` | E2E 実行可能・旧バージョン | 通る | 通る(GitHub から約 68 MB をダウンロードし SHA-256 を検証) | 上記 7.6.5 の manifest と同一 identity。両方を解決すると `publish` はこちらを superseded として扱う — 詳細は下記。7.6.5 と異なり `Scripts` は無し(あり/なし両方をこのディレクトリでカバーするため) |
| `Microsoft/Microsoft.PowerShell/7.6.4/powershell-macos-x64.yaml` | E2E 実行可能・旧バージョン | 通る | 通る(GitHub から約 73 MB をダウンロードし SHA-256 を検証) | 上記と同様 |
| `contoso-tool-windows-x64.yaml` | E2E 実行可能 | 通る | 通る(ローカルの `RepositoryFiles` を staging し、実際に `.intunewin` を生成 — Windows マシン/runner が必要) | 外部ダウンロードは無し。`.intunewin` 生成には `IntuneWinAppUtil.exe` が必要だが自動ダウンロードされる |
| `contoso-tool-windows-arm64.yaml` | E2E 実行可能 | 通る | 通る(上記と同様) | |
| `contoso-tool-macos-arm64.yaml` | 参照専用(schema 例) | 通る | **落ちる** — `Source` が架空の Azure Blob account(`contosopackages`)と、全ゼロのプレースホルダ `Sha256` を指している | [doc/01-manifest-schema.md §5.3](../../doc/01-manifest-schema.md) の `azureBlob` の形を示すためのもの。解決される想定ではない |
| `apple-container-macos-arm64.yaml` | 参照専用(意図的な失敗) | **落ちる** — `Detection.IncludedApps` が空 | — | Apple Container の PKG は `.app` バンドルを一切インストールしないため、架空の bundleId を捏造しない限り `IncludedApps` を実値で埋められない。Intune の macOS 検出における実際の制約を記録したもの。ファイル冒頭のコメントと [doc/01-manifest-schema.md §5.4](../../doc/01-manifest-schema.md) を参照 |

## PowerShell サンプルが E2E fixture として成立する理由

Intune の macOS `Detection.IncludedApps` には、PKG が**実際にインストールする**アプリケーションの bundleId + version を列挙する必要があります([Add an Unmanaged macOS PKG App to Microsoft Intune](https://learn.microsoft.com/intune/app-management/deployment/add-unmanaged-pkg-macos#step-4-%E2%80%93-detection-rules) 参照)。Apple Container のような CLI 専用の PKG には、指す先がありません。

PowerShell の macOS PKG にはそれがあります。インストーラが `/Applications` 配下に `PowerShell.app` を配置し、次の値を持ちます。

- `BundleId`: `com.microsoft.powershell`
- `BundleVersion`: リリースバージョン(例: `7.6.5`)

(根拠: PowerShell/PowerShell リポジトリの `tools/packaging/packaging.psm1` の `New-MacOSLauncher` / `Get-MacOSPackageIdentifierInfo`、および `packaging.strings.psd1` の `MacOSLauncherPlistTemplate`。) 実機の Mac にインストール後、次のコマンドで自分で確認できます。

```bash
defaults read /Applications/PowerShell.app/Contents/Info CFBundleIdentifier
defaults read /Applications/PowerShell.app/Contents/Info CFBundleShortVersionString
```

`Requirements.MinimumOSVersion: "14.0"` は意図的な選択です。PowerShell 7.6 (LTS) が対応する最小バージョンであり、同時に beta 専用の Graph フラグ `v14_0` を要求する最小バージョンでもあるため、`AppType: pkg`(beta)経路を `MacOsMinimumOperatingSystemTable` で正しく通します。`AppType: lob` は v1.0 に `v14_0`/`v15_0` フラグが無いため `14.0` 以上を使えません。

`Assignments` はどの tenant でも無編集で使えるよう、意図的に `[]` のままにしています。実際の(dry-run でない)`publish` 前に、自組織の group を追加してください。

## pre/post-install script(7.6.5 のみ)

7.6.5 の manifest はさらに `Scripts.PreInstall` / `Scripts.PostInstall`
([doc/01-manifest-schema.md §5.4.2](../../doc/01-manifest-schema.md))を設定しており、
`samples/scripts/macos/powershell/preinstall.sh` と `postinstall.sh` を参照しています。これらは
`AppType: pkg` 限定の Graph プロパティ(`macOSPkgApp.preInstallScript`/`postInstallScript`)なので、
ここにのみ適用され、仮に `AppType: lob` のバリアントがあったとしても適用されません。

サンプルスクリプトは、インストール前に Homebrew 版 `pwsh` と古い `pwsh` symlink を削除し、500 MB の空き
容量を確認します。インストール後は `pwsh` の存在を確認し、`/usr/local/bin` を `/etc/paths.d` に追加して
新しいログインシェルで `PATH` に載るようにします。あくまで説明用であり、あらゆる macOS 構成で網羅的に
テストされているわけではないため、実際のテナントで使う前に調整してください。

```yaml
Assignments:
  - Target: group
    GroupId: "<your-assignment-group-guid>"
    Intent: required
```

## App category

どのサンプルも `Categories` を宣言していません。これは意図的です。category 名は tenant 固有であり、対象 tenant に
存在しない名前は publish の preflight で失敗するためです。`Categories` の省略は「何も触らない」唯一の指定でもあり、
app の現在の category は維持され、category 関連の Graph 呼び出しも一切行われません。

試す場合は、まず Intune 管理センターで category を作成してから app entry に追加します。

```yaml
Apps:
  - Platform: macos
    Architecture: arm64
    Categories:
      - Business Apps
```

`Categories: []` はキーの省略とは意味が異なり、app のすべての category relationship を解除します。名前は tenant の
`mobileAppCategory.displayName` と大小文字を無視して照合しますが、それ以外は verbatim です。Relaypublisher は
category の作成・改名・削除を行いません。`validate` は tenant と照合できないため、category plan を確認するには
`publish --dry-run` を実行します。詳細は
[doc/05-operation_ja.md §4d](../../doc/05-operation_ja.md#4d-intune-app-category) と
[doc/01-manifest-schema.md §5.8](../../doc/01-manifest-schema.md) を参照してください。

なお `Categories` の追加・変更は manifest 全体の `inputHash` を変えるため、metadata だけの変更でも次回の
`package` / `publish` で content が再package・再upload されます。

## PowerShell サンプルを E2E で実行する

```bash
CLI_PROJECT="src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  plan --repo-root samples --manifest manifests/Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml --output manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  package --repo-root samples --manifest-list manifest-list.json --output ./out
```

```powershell
$CliProject = "src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"

dotnet run --configuration Release --project $CliProject -- `
  plan --repo-root samples --manifest manifests/Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml --output manifest-list.json

dotnet run --configuration Release --project $CliProject -- `
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project $CliProject -- `
  package --repo-root samples --manifest-list manifest-list.json --output ./out
```

`publish --dry-run` / `publish` については [doc/07-local-e2e_ja.md §2, §4.5-4.6](../../doc/07-local-e2e_ja.md)(ローカル Azure CLI 認証、`--expected-tenant`)に従い、先に `Assignments` を追加することを忘れないでください。

## PowerShell サンプルを新しいバージョンに更新する

これは [doc/05-operation_ja.md §4c](../../doc/05-operation_ja.md#4c-既存-app-を新しいバージョンに更新する) の
一般的な更新手順を実行可能にした例です — 新しいバージョンフォルダを追加し、既存フォルダは上書きしません。
`Microsoft/Microsoft.PowerShell/7.6.4/` と `Microsoft/Microsoft.PowerShell/7.6.5/` はすでにこれを並べて示して
います。両方とも同じ `PackageIdentifier + Platform + Architecture` を持つため同一 Intune app identity に
解決され、`7.6.4` はバージョンを上げる前の `7.6.5` のコピー元にあたります。

サンプルを 7.6.5 より新しいリリースへ進める場合は、`7.6.5` の各ファイルをコピーして
`Microsoft/Microsoft.PowerShell/<新しい version>/` を作り、`arm64` / `x64` 両方の manifest で一貫して
以下を差し替えます。

- `PackageVersion`(top level)
- `Source.Tag`(`v<version>`)、`Source.AssetName`、`Source.Destination`
- `Source.Sha256` — 記憶ではなく、リリースの `hashes.sha256` アセットから取得すること。`package` が実ダウンロードと突き合わせて検証し、不一致なら失敗する
- `Detection.IncludedApps[0].BundleVersion` — 旧値のままにすると、更新後も Intune の検出ルールが旧 bundle version を探し続けるため、新しい content が publish されても既存管理下のデバイスが未更新と判定されることがある
- 新しいリリースが macOS 14 のサポートを打ち切った場合は `Requirements.MinimumOSVersion`

`PackageIdentifier`・`Platform`・`Architecture`・`DisplayName` は変更しないでください。理由は
[doc/05-operation_ja.md §4c](../../doc/05-operation_ja.md#4c-既存-app-を新しいバージョンに更新する) を参照。

バージョン選択の挙動そのものを確認するには、7.6.4 と 7.6.5 の両方を同じ `manifest-list.json` に解決してから
publish をプレビューします。

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  plan --repo-root samples \
  --manifest manifests/Microsoft/Microsoft.PowerShell/7.6.4/powershell-macos-arm64.yaml manifests/Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml \
  --output manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  publish --repo-root samples --manifest-list manifest-list.json --package-dir ./out \
  --expected-tenant <tenant-id> --dry-run
```

`validate` は通ります — バージョンフォルダをまたいで同一 identity・別 `PackageVersion` が存在するのは想定内で
矛盾ではないためです(doc/00-overview.md §6.8)。`publish` は高い方のバージョンのみを選び、他はログに出力します。
出力には次のような行が含まれます。

```text
Skipping Microsoft.PowerShell macos-arm64 version 7.6.4 from '.../7.6.4/powershell-macos-arm64.yaml' (superseded by version 7.6.5).
```

## 後始末

`manifest-list.json`、`./out`、ダウンロードした `.pkg`/`.intunewin` はローカルの再生成可能な成果物です。commit せず、作業が終わったら削除してください([doc/07-local-e2e_ja.md §5](../../doc/07-local-e2e_ja.md) 参照)。
