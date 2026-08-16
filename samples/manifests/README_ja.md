# サンプル manifest

このディレクトリには 2 種類の manifest が混在しています。

- **E2E 実行可能なサンプル**: 実在する、公開ダウンロード可能な package を指しています。無編集で `plan` → `validate` → `package` が通ります（`publish` にはテスト tenant、`Assignments` を追加する場合は実在する group GUID が別途必要です。[../../doc/07-local-e2e_ja.md](../../doc/07-local-e2e_ja.md) を参照してください)。
- **参照専用のサンプル**: schema の形や制約を示すためのものです。実世界の制約を記録するために、意図的に `validate` や `package` で失敗するように書かれているものもあります。バグだと思う前に、ファイル内のコメントを読んでください。

以下のコマンドはすべて `--repo-root samples` を付けています。これらの manifest 内の `RepositoryFiles.Source` / `Icon` のパスは、リポジトリルートではなくこの `samples/` ディレクトリからの相対パスだからです(`scripts/windows/...` は `<repo-root>/scripts/windows/...` ではなく `samples/scripts/windows/...` を指します)。同じ manifest に対して `plan`・`validate`・`package`・`publish` を実行するときは `--repo-root samples` を一貫させてください。`manifest-list.json` に記録される manifest path は、`plan` 実行時に渡した `--repo-root` を基準に解決されます。

| Manifest | 位置づけ | `validate` | `package` | 備考 |
|---|---|---|---|---|
| `powershell-macos-arm64.yaml` | E2E 実行可能 | 通る | 通る(GitHub から約 68 MB をダウンロードし SHA-256 を検証) | 詳細は下記 |
| `powershell-macos-x64.yaml` | E2E 実行可能 | 通る | 通る(GitHub から約 73 MB をダウンロードし SHA-256 を検証) | 詳細は下記 |
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

```yaml
Assignments:
  - Target: group
    GroupId: "<your-assignment-group-guid>"
    Intent: required
```

## PowerShell サンプルを E2E で実行する

```bash
CLI_PROJECT="src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  plan --repo-root samples --manifest manifests/powershell-macos-arm64.yaml --output manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  package --repo-root samples --manifest-list manifest-list.json --output ./out
```

```powershell
$CliProject = "src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"

dotnet run --configuration Release --project $CliProject -- `
  plan --repo-root samples --manifest manifests/powershell-macos-arm64.yaml --output manifest-list.json

dotnet run --configuration Release --project $CliProject -- `
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project $CliProject -- `
  package --repo-root samples --manifest-list manifest-list.json --output ./out
```

`publish --dry-run` / `publish` については [doc/07-local-e2e_ja.md §2, §4.5-4.6](../../doc/07-local-e2e_ja.md)(ローカル Azure CLI 認証、`--expected-tenant`)に従い、先に `Assignments` を追加することを忘れないでください。

## PowerShell サンプルを新しいリリースに更新する

`powershell-macos-arm64.yaml` と `powershell-macos-x64.yaml` の両方で、一貫して以下を差し替えます。

- `PackageVersion`(top level)
- `Source.Tag`(`v<version>`)、`Source.AssetName`、`Source.Destination`
- `Source.Sha256` — 記憶ではなく、リリースの `hashes.sha256` アセットから取得すること。`package` が実ダウンロードと突き合わせて検証し、不一致なら失敗する
- `Detection.IncludedApps[0].BundleVersion`
- 新しいリリースが macOS 14 のサポートを打ち切った場合は `Requirements.MinimumOSVersion`

## 後始末

`manifest-list.json`、`./out`、ダウンロードした `.pkg`/`.intunewin` はローカルの再生成可能な成果物です。commit せず、作業が終わったら削除してください([doc/07-local-e2e_ja.md §5](../../doc/07-local-e2e_ja.md) 参照)。
