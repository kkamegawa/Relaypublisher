# 運用ガイド

このガイドは、Relaypublisher で Intune LOB app を publish するために必要な初期設定と日常運用をまとめたものです。

正式ドキュメントは英語版の [05-operation.md](05-operation.md) です。

## 0. ツールのインストールとバージョン運用

Relaypublisher は NuGet global tool として配布します。

Install:

```bash
dotnet tool install --global relaypublisher
```

Update:

```bash
dotnet tool update --global relaypublisher
```

特定バージョンへ pin / rollback:

```bash
dotnet tool update --global relaypublisher --version <x.y.z>
```

インストール済み version の確認:

```bash
dotnet tool list --global | grep relaypublisher
```

リリース version の運用方針:

- Published package ID: `relaypublisher`
- Command name: `relaypublisher`
- Package version source: Git tag `vX.Y.Z` を CI が `-p:Version=X.Y.Z` で注入する

## 1. Microsoft Entra app registration

CI publisher identity 用に Microsoft Entra application registration を 1 つ作成します。

必要な設定:

- Account type: 対象 tenant の single tenant。
- Microsoft Graph application permission: `DeviceManagementApps.ReadWrite.All`。
- Admin consent: 初回 production publish 前に tenant administrator が付与します。
- 推奨 CI setup では client secret は不要です。workload identity federation を使います。

運用メモ:

- Application client ID は CI secret または variable `AZURE_CLIENT_ID` に保存します。
- Tenant ID は `AZURE_TENANT_ID` に保存します。
- Azure Blob source を使う場合は subscription ID も `AZURE_SUBSCRIPTION_ID` に保存し、CI identity に package storage scope への read access を付与します。
- `publish --expected-tenant <tenant-id>` を使い、誤った tenant の token では write 前に fail させます。

## 2. Federated credentials

Federated credential により、CI は runner が発行した OIDC token を Microsoft identity platform の access token と交換できます。Graph publishing に使う Entra app registration に設定します。

Federated credential には Microsoft 推奨の token exchange audience を使います。issuer と subject は完全一致が必要で、wildcard matching はサポートされません。

### GitHub Actions

GitHub Actions federated credential は protected production environment に限定します。

推奨 subject 形式:

```text
repo:<owner>/<repo>:environment:production
```

必要な workflow 設定:

- publish job に `permissions: id-token: write` を付けます。
- publish job に `environment: production` を設定します。
- workflow から Azure login action に `AZURE_CLIENT_ID`、`AZURE_TENANT_ID`、`AZURE_SUBSCRIPTION_ID` を渡します。
- Pull request job には `id-token: write` や production secrets を渡しません。

Packaging 中に `azureBlob` source を使う場合、Windows package job でも OIDC login と storage reader role が必要です。

### Azure Pipelines

Workload identity federation を設定した Azure Resource Manager service connection を使います。

推奨 setup:

- Service connection を workload identity federation で作成、または変換します。
- Project policy が要求しない限り、全 pipeline への broad access は付与しません。
- Intune app を publish する pipeline のみ authorize します。
- `production` environment に Exclusive Lock check を設定し、publish run を直列化します。
- Protected variable group から `<tenant-id>` を渡し、`publish --expected-tenant` で使います。

## 3. Source provider environment variables

Source provider の認証は、manifest item ごとの `Auth` block で制御します。

| Source type | `Auth.Type` | 必須 environment variable | Notes |
|---|---|---|---|
| `publicHttp` | omitted または `none` | なし | Anonymous download です。 |
| `githubRelease` | `token` | `Auth.SecretName` の値。通常は `GH_RELEASE_PAT` | 同じ名前の environment variable から token を読みます。 |
| `azureBlob` | `workloadIdentity` | `AZURE_CLIENT_ID`、`AZURE_TENANT_ID`、CI OIDC variables | Federated CI identity で access します。 |

Manifest fragment の例:

```yaml
ExternalFiles:
  - Type: githubRelease
    Owner: <owner>
    Repository: <repository>
    Tag: <tag>
    AssetName: <asset-name>
    Destination: bin/app.exe
    Sha256: "<sha256>"
    Auth:
      Type: token
      SecretName: GH_RELEASE_PAT
```

CI では secret を正確に同じ environment variable name に map します。

```powershell
$env:GH_RELEASE_PAT = "<token>"
```

```bash
export GH_RELEASE_PAT="<token>"
```

## 4. 日常コマンド

Publish 前に build と test を実行します。

```powershell
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
```

```bash
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
```

Manifest set を一度だけ確定します。

```powershell
relaypublisher plan --base-ref <base-ref> --output manifest-list.json
```

```bash
relaypublisher plan --base-ref <base-ref> --output manifest-list.json
```

選択された manifest を validate します。

```powershell
relaypublisher validate --manifest-list manifest-list.json
```

```bash
relaypublisher validate --manifest-list manifest-list.json
```

Windows 上で Windows Win32 app を package します。

```powershell
relaypublisher package --manifest-list manifest-list.json --output ./out
```

Windows 以外の runner では、Windows entry の `.intunewin` 生成をスキップしつつ staging validation を行うために
`--stage-only` を使います。macOS entry は Windows 以外の runner でも `--stage-only` を付ける必要はありません。
macOS の packaging には外部ツール呼び出しの工程が無いため、`--stage-only` を付けない通常の `package` だけで
`.pkg` の staging・checksum 検証・`package-metadata.json` の書き出しまで完了します。ただし `--stage-only` は
manifest list 内の全 platform に一律に適用されるため、Windows entry のために `--stage-only` を付けた場合、
同じ実行に含まれる macOS entry も staging はされますが `package-metadata.json` は書き出されません。その状態の
出力に対して `publish` を実行すると、macOS entry 側で package metadata missing により fail する。macOS entry
については `--stage-only` を外して `package` を再実行するか、Windows entry と分けて別の `package` 呼び出しに
すること:

```bash
relaypublisher package --manifest-list manifest-list.json --output ./out --stage-only
```

Intune に write せず publish changes を確認します。

```powershell
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

Intune に publish します。

```bash
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out \
  --expected-tenant <tenant-id>
```

## 4a. macOS に関する注記

macOS 対応(doc/00-overview.md §6.13)には `AppType` によって Graph・運用上の特性が異なる 2 種類がある。

- `AppType: pkg`(既定、`macOSPkgApp`): 未署名可、8 GB まで、`Intent: uninstall` 非対応。この app 種別に関する
  すべての Graph 呼び出し(作成・更新、content upload、notes/committedContentVersion の patch、app resolution
  での一覧取得)は Graph **beta** を経由する。`macOSPkgApp` が v1.0 に存在しないためで、これは内部的に処理され
  operator の作業は不要だが、テナント側で beta API に障害があると `pkg` の publish のみが影響を受ける点に注意する。
- `AppType: lob`(`macOSLobApp`): Developer ID Installer 署名必須、2 GB 上限、top-level `Icon` 必須で、Graph
  **v1.0** のまま。v1.0 の `minimumSupportedOperatingSystem` には macOS 13 より先のフラグが無いため、
  `Requirements.MinimumOSVersion` に macOS 14 以降を指定した `lob` の manifest entry は `publish`(および
  `--dry-run`)時に `UnsupportedMacOsVersionException` で fail し、`AppType: pkg` への変更を促すメッセージが出る。
  これは Graph API バージョンの制約であり manifest schema のルールではないため、`validate` では検出されない。
- `.pkg` の content は publish 時にその場で暗号化される(macOS には IntuneWinAppUtil に相当する packaging 時
  ツールが無い)。そのため Windows のように「暗号化済み package を再生成する」個別の手順は無く、`publish` を
  再実行すればその時点で staging されている `.pkg` が再暗号化される。

## 5. Exit codes

| Exit code | 意味 | Operator action |
|---|---|---|
| `0` | Command が成功しました。 | CI workflow を続行します。 |
| `1` | Validation、packaging、authentication、tenant、Graph、publish のいずれかが失敗しました。 | Error message を読み、manifest または environment を修正して rerun します。 |
| `2` | 未実装 command path 用の予約値です。 | Operator retry ではなく tool implementation gap として扱います。 |

## 6. Production checklist

- Full repository で `validate` が成功している。
- `plan` output を `manifest-list.json` として保存し、後続 job で再利用している。
- Package job が changed manifests を再計算していない。
- Publish job が protected environment と serialized execution で実行される。
- `publish` が常に `--expected-tenant` を使っている。
- GitHub release token などの source provider secrets は、必要な job にだけ渡している。
- Authorization header、token、signed package URI、secret value を log や artifact に出していない。
