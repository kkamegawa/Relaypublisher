# 運用ガイド

このガイドは、Relaypublisher で Intune LOB app を publish するために必要な初期設定と日常運用をまとめたものです。

正式ドキュメントは英語版の [05-operation.md](05-operation.md) です。

ローカルターミナルでの一連の手順は [07-local-e2e_ja.md](07-local-e2e_ja.md) を参照してください。

## 0. ツールのインストールとバージョン運用

Relaypublisher は NuGet global tool として配布します。同じ version を 3 つの feed に publish するため、
環境から到達できる feed を選んでください。

| Feed | 想定利用者 |
| --- | --- |
| nuget.org | 一般利用者。既定の source なので追加指定は不要です。 |
| GitHub Packages | この repository を直接使う利用者。package が public でも `read:packages` 権限の GitHub token が必ず必要です。 |
| Azure Artifacts | 社内 CI / 閉じたネットワーク。組織の feed へのアクセス権が必要です。 |

nuget.org からの install:

```bash
dotnet tool install --global relaypublisher
```

GitHub Packages からの install。`--add-source` は feed URL を渡すだけで認証は行いません。
GitHub Packages は package が public でも匿名の NuGet リクエストに 401 を返すため、先に認証情報つきで
source を登録します:

```bash
# token は環境変数で渡します。コマンドラインに直接書かないでください。
export GH_PACKAGES_TOKEN="<github-pat-with-read-packages>"

dotnet nuget add source "https://nuget.pkg.github.com/<owner>/index.json"   --name relaypublisher-github   --username "<github-username>"   --password "$GH_PACKAGES_TOKEN"   --store-password-in-clear-text

dotnet tool install --global relaypublisher --add-source relaypublisher-github
```

PowerShell 7:

```powershell
$env:GH_PACKAGES_TOKEN = '<github-pat-with-read-packages>'

dotnet nuget add source "https://nuget.pkg.github.com/<owner>/index.json" `
  --name relaypublisher-github `
  --username "<github-username>" `
  --password $env:GH_PACKAGES_TOKEN `
  --store-password-in-clear-text

dotnet tool install --global relaypublisher --add-source relaypublisher-github
```

`--store-password-in-clear-text` はユーザーレベルの *NuGet.config* に token を平文で書き込みます。
NuGet の暗号化ストアが Windows 専用のため、Linux / macOS では必須です。この設定ファイル自体を
secret として扱うか、Windows ではこのフラグを外してください。不要になったら
`dotnet nuget remove source relaypublisher-github` で削除します。

Azure Artifacts からの install。先に credential provider を入れ、初回だけ認証します:

```bash
dotnet tool install --global Microsoft.Artifacts.CredentialProvider.NuGet.Tool   --source https://api.nuget.org/v3/index.json

dotnet nuget add source "<azure-artifacts-feed-v3-index-url>" --name relaypublisher-ado

dotnet tool install --global relaypublisher --add-source relaypublisher-ado --interactive
```

`--interactive` で初回のサインインプロンプトが出ます。以降はキャッシュされた session token を
再利用するため、このフラグは不要です。

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
- リリースの流れ: main に `v*` tag を push すると、`.nupkg`、self-contained single-file app
  (`win-x64` / `win-arm64` / `osx-arm64`)、`SHA256SUMS.txt` を添付した **draft** GitHub release が作られます。
  その draft release を手動で publish した時点で 3 つの feed への push が走ります。
  詳細は [03-ci-github-actions.md](03-ci-github-actions.md) §12a を参照してください。
- single-file app には署名・notarization を行っていません。macOS では Gatekeeper の警告が出ます。

## 1. Microsoft Entra app registration

CI publisher identity 用に Microsoft Entra application registration を 1 つ作成します。

必要な設定:

- Account type: 対象 tenant の single tenant。
- Microsoft Graph application permission: `DeviceManagementApps.ReadWrite.All`。**委任済み
  (Delegated)** ではなく **アプリケーションの許可 (Application permissions)** から追加してください。
  ポータルは同じ名前を両方に表示します。Relaypublisher は service principal としてサインインし、
  app-only token は application permission のみ(`roles` claim)を持つため、委任済みで登録すると管理者の
  同意を与えても 403 になります。[06-troubleshooting_ja.md](06-troubleshooting_ja.md) の section 2a を
  参照してください。
- Admin consent: 初回 production publish 前に tenant administrator が付与します。
- 推奨 CI setup では client secret は不要です。workload identity federation を使います。

運用メモ:

- Application client ID は CI secret または variable `AZURE_CLIENT_ID` に保存します。
- Tenant ID は `AZURE_TENANT_ID` に保存します。
- Azure Blob source を使う場合は subscription ID も `AZURE_SUBSCRIPTION_ID` に保存し、CI identity に package storage scope への read access を付与します。
- `publish --expected-tenant <tenant-id>` を使い、誤った tenant の token では write 前に fail させます。
- `AZURE_TOKEN_CREDENTIALS`(§3 参照)を設定し、`DefaultAzureCredential` がこの identity に決定的に解決されるようにします。同じ tenant 内の誤った identity は `--expected-tenant` では検出できません。

## 2. Federated credentials

Federated credential により、CI は runner が発行した OIDC token を Microsoft identity platform の access token と交換できます。Graph publishing に使う Entra app registration に設定します。

Federated credential には Microsoft 推奨の token exchange audience `api://AzureADTokenExchange` を使います。
`issuer`、`subject`、`audience` は incoming OIDC token と大文字小文字を含めて完全一致させます。通常の
credential では wildcard matching は使用できません。

Setup 後の代表的な failure は、[トラブルシューティングガイド](06-troubleshooting_ja.md) の
`TenantMismatchException`（§2）と Azure Blob source（§5）を参照してください。

### GitHub Actions

GitHub Actions federated credential は protected production environment に限定します。issuer は
`https://token.actions.githubusercontent.com/`、audience は `api://AzureADTokenExchange`、subject は
workflow の owner、repository、environment と一致する値にします。

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

Azure DevOps workload identity federation service connection が生成した issuer と subject identifier を、
Entra federated credential にそのままコピーします。GitHub の issuer を流用したり、subject を推測したり
しないでください。audience は `api://AzureADTokenExchange` とし、service connection は対象 pipeline
だけに authorize します。

### CI login 後の token acquisition

GitHub Actions の `azure/login` と Azure Pipelines の workload identity service connection を使う
`AzureCLI@2` は runner 上の Azure CLI login を確立します。Relaypublisher の `DefaultAzureCredential` は
そこから最初に解決できた credential source で `https://graph.microsoft.com/.default` scope の Graph
token を取得します — Azure CLI login は chain の中の1候補に過ぎず、選ばれる保証はありません。login
step の直後に job environment で `AZURE_TOKEN_CREDENTIALS` を設定してください(§3 参照)。Azure CLI
login だけでは Graph application permission と admin consent の代わりになりません。`publish` は
`AZURE_TOKEN_CREDENTIALS` が未設定なら warning を出し、最初の Graph 呼び出しで取得した identity の
`appid`/`idtyp`/`roles` をログします。

Bash / zsh、`azure/login` の直後:

```bash
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
```

PowerShell 7、`AzureCLI@2` の直後:

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
```

代表的な failure の確認先は [06-troubleshooting.md](06-troubleshooting.md) です。

- OIDC または tenant mismatch: [TenantMismatchException](06-troubleshooting.md#2-tenantmismatchexception)
- GitHub Release token の不足: [GitHub Release Token Is Missing](06-troubleshooting.md#4-github-release-token-is-missing)
- Azure Blob の権限または download failure: [Azure Blob Source Cannot Be Downloaded](06-troubleshooting.md#5-azure-blob-source-cannot-be-downloaded)

## 3. Source provider environment variables

Source provider の認証は、manifest item ごとの `Auth` block で制御します。

| Source type | `Auth.Type` | 必須 environment variable | Notes |
|---|---|---|---|
| `publicHttp` | omitted または `none` | なし | Anonymous download です。 |
| `githubRelease` | `token` | `Auth.SecretName` の値。通常は `GH_RELEASE_PAT` | 同じ名前の environment variable から token を読みます。 |
| `azureBlob` | `workloadIdentity` | `AZURE_CLIENT_ID`、`AZURE_TENANT_ID`、CI OIDC variables | Federated CI identity で access します。 |

### Credential の選択(`AZURE_TOKEN_CREDENTIALS`)

`AZURE_TOKEN_CREDENTIALS` は特定の source provider に限定された設定ではありません。`Azure.Identity`
(1.15.0 以降)がプロセス内部で読み取るプロセス全体の環境変数なので、1回設定するだけで、プロセス内の
すべての `DefaultAzureCredential` 構築 — `publish` の Graph publish 経路、および `package`/`plan` 中の
すべての `azureBlob` download — に効きます。CI runner や CLI login ベースのローカル利用での推奨値は
`AzureCliCredential` で、chain を Azure CLI login のみに制限します。設定は任意です(`AZURE_TOKEN_CREDENTIALS`
なしでも `DefaultAzureCredential` は動作します)が推奨します — pin しない chain は別のサインイン済み
identity に黙って解決されることがあるためです(doc/00-overview.md §6.19、
[06-troubleshooting_ja.md](06-troubleshooting_ja.md) §2a)。`publish` は未設定時に warning を出します。

```bash
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
```

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
```

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

## 4a. Package input と CI artifact の受け渡し

`package` コマンドは、manifest で指定された source provider から input を取得し、`--output` に指定したディレクトリへ package file を出力します。

- `publicHttp` は匿名でダウンロードします。
- `githubRelease` は `Auth.Type: token` の場合に `Auth.SecretName` が指定する環境変数を読み取ります。
- `azureBlob` は `Auth.Type: workloadIdentity` が必要です。ローカルの `DefaultAzureCredential` は Azure CLI login を利用でき、CI では workload identity login を利用します。

private GitHub Release asset を使う場合は、`Auth.Type: token` を設定し、package 前に manifest の secret variable を設定します。

```bash
export GH_RELEASE_PAT="<token>"
```

```powershell
$env:GH_RELEASE_PAT = "<token>"
```

Windows packaging は `publish` に必要な `.intunewin` を生成します。macOS packaging は staging 済み `.pkg` と `package-metadata.json` を生成します。`package --stage-only` の output には最終 `.intunewin` と package metadata がないため、Windows の publish に使わないでください。

CI では package job が package directory を `intunewin-packages` artifact として upload します。publish job はこの artifact を download して、download 先のディレクトリを `--package-dir` に渡します。また、`plan` が生成した同じ `manifest-list.json` を再利用します。

macOS `.pkg` の packaging は次の順序で行います。

1. source を download し、download した byte 列と manifest の `Source.Sha256` を照合する。
2. staging 済み XAR archive を検査し、宣言された application bundle ID と version を記録する。
3. 検査結果と `Detection.IncludedApps` / `Detection.PrimaryBundleId` を照合する。
4. source SHA と使用した CLI の正確な version を含む package metadata と検査 report を書き出す。

この検査は `validate` では行いません。`validate` は schema およびその他の静的な repository check だけを行い、source の download や package 内容の検査は行いません。credential が必要な source は `validate` ではなく `package` で検証されます。

publish job は private source を再 download しません。Graph への write を行う前に、`publish` は staging 済みの全 macOS `.pkg` を再 hash し、XAR 内容を再検査し、結果を manifest、package metadata、検査 report と照合します。その後、選択された**全 entry**について preflight を完了させます。operator が拒否した warning、hard error、古い report、SHA mismatch のいずれかがあれば、最初の Graph write より前に batch 全体を停止します。package job と publish job は同じ正確な CLI version を使用し、両 job で `relaypublisher --version` を記録・確認します。

GitHub Actions:

```yaml
- uses: actions/download-artifact@v4
  with:
    name: manifest-list

- uses: actions/download-artifact@v4
  with:
    name: intunewin-packages
    path: ./out

- run: >
    relaypublisher publish
    --manifest-list manifest-list.json
    --package-dir ./out
    --expected-tenant "<tenant-id>"
```

Azure Pipelines:

```yaml
- download: current
  artifact: manifest-list

- download: current
  artifact: intunewin-packages

- script: >
    relaypublisher publish
    --manifest-list '$(Pipeline.Workspace)/manifest-list/manifest-list.json'
    --package-dir '$(Pipeline.Workspace)/intunewin-packages'
    --expected-tenant '<tenant-id>'
```

## 4b. macOS に関する注記

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
- `AppType: pkg` 限定: 任意の `Scripts.PreInstall` / `Scripts.PostInstall` ブロックは Graph の
  `preInstallScript` / `postInstallScript` に対応する(doc/01-manifest-schema.md §5.4.2)。デバイス側で
  Intune management agent for macOS **2309.007 以降**が必要。pre-install script が非 0 終了で app install は
  失敗し(次回 device check-in で再試行)、post-install script の失敗は一切報告されない(app は "success" の
  まま)。スクリプト本文は決定的 inputHash に含まれないため、スクリプトを編集して `publish` を再実行しても
  (数 GB になり得る)`.pkg` の再アップロードは発生しない。

### 4b.1. Primary bundle の検査と warning policy

`Detection.PrimaryBundleId` は任意です。省略時は従来どおり `IncludedApps` の先頭 entry が宣言上の primary です。指定時は ordinal の完全一致または segment boundary の prefix 一致で manifest entry を 1 件選択し、その entry を payload の先頭へ移動します。manifest file 自体は変更しません。

XAR 検査は package 内容に対する semantic check です。`IncludedApps` を書き換えたり、updater を自動削除したりしません。次の条件は semantic warning であり、`--force` で確認済みとして続行できます。

| 条件 | 既定動作 |
|---|---|
| package が複数の application bundle を含み、`PrimaryBundleId` が省略されている | 検出 bundle 一覧と、宣言上の先頭 entry が使われることを表示する。 |
| 宣言した `PrimaryBundleId` が package に存在しない | 検出 bundle 一覧を表示し、operator の確認を要求する。 |
| package に `IncludedApps` へ列挙されていない application bundle がある | 未列挙 bundle を表示し、operator の確認を要求する。 |
| package の metadata が application bundle を 1 件も宣言していない | bundle が検出されなかったことを表示し、operator の確認を要求する。XAR metadata の parse 自体は成功しているが `.app` bundle が 0 件だった状態であり、parse 失敗とは異なる。 |

対話的な TTY では semantic warning ごとに `[y/N]` を表示し、既定値は停止です。非対話環境では `--force` が無い限り fail します。`--force` は確認済みであることを記録し、これらの semantic warning だけを回避します。schema error、primary の曖昧な選択、XAR entry の欠落・破損、未対応 archive、source SHA mismatch、古い/改ざんされた artifact、Graph/tenant safety check は回避できません。

`PrimaryBundleId` が検出 bundle に 2 件以上一致する場合は選択が曖昧なため hard error です。使用可能な application bundle が 0 件の場合や、XAR/XML を安全に parse できない場合も hard error です。manifest または source を修正して `package` を再実行し、report を手編集しないでください。

`AppType: lob` では、`BundleVersion` が Graph の `buildNumber` に対応する short bundle version、`BundleBuildVersion` が `versionNumber` に対応する build version です。package が `CFBundleShortVersionString` と `CFBundleVersion` を区別している場合は両方を正しく更新し、既定で同じ値を両 field にコピーしないでください。選択した primary は LOB の top-level bundle field にも設定し、`childApps` の先頭にします。

### 4b.2. macOS entry の `Architecture` を省略する

macOS の app リソースには architecture を表す Graph プロパティが無いため、`Platform: macos` の entry は
`Architecture` を省略できる(issue #122、doc/01-manifest-schema.md §5.3.1)。この場合、app identity・
staging ディレクトリ名・`notes` metadata で使われる実効値は `universal` になる。

publish 済みの macOS app の manifest entry を、明示的な `Architecture`(例: `arm64`)から省略へ切り替えると、
identity は `<PackageIdentifier>|macos|arm64` から `<PackageIdentifier>|macos|universal` に変わる。既存の
app 解決ルール(§6.1)がそのまま適用される。

1. `notes` の management metadata 照合は `architecture` が一致しなくなるため外れる。
2. `DisplayName` を変えていなければ `DisplayName` fallback は引き続き一致し、既存 app を **adopt** して
   `notes` を新しい(`universal` の)identity で書き戻す — 同じ Intune app ID と assignment が維持される。
3. staging ディレクトリが変わり(`macos-arm64` → `macos-universal`)、決定的な `inputHash` も変わるため、
   次の `package`/`publish` で 1 回だけ再 package / 再 upload が発生する。実体の `.pkg` に変更が無くても
   発生する。
4. `Architecture` の削除と同時に `DisplayName` も変えると fallback 照合まで外れ、新規 Intune app が
   作られる — doc/06-troubleshooting.md に既出の identity drift と同じ障害モード。同じ publish で
   両方を同時に変えないこと。

この切り替えを自動化する移行ツールは提供しない。manifest を編集して行う、operator 主導の一回限りの
再 package / 再 upload である。

## 4c. 既存 app を新しいバージョンに更新する

app identity は `PackageIdentifier + Platform + Architecture` であり、バージョンを含まない
(doc/00-overview.md §6.1/§6.2)。したがって同じ identity のまま新しい `PackageVersion` を publish すると、
既存の Intune app が in-place で更新される — app ID・assignment・すでに配布済みのデバイスはすべて維持される。
これがバージョンを更新する唯一のサポート方法であり、Relaypublisher は別 app を新規作成したり、バージョン間に
Intune の supersedence 関係を設定したりはしない。

手順:

1. `manifests/<Publisher>/<PackageIdentifier>/<新しい version>/` を作り、直前バージョンの manifest をコピーする。
   旧バージョンのフォルダは削除しない — 履歴として機能する(doc/00-overview.md §6.8)。
2. top-level の `PackageVersion` を更新する。
3. `Source` の版数依存フィールド(`Tag`、`AssetName`、`BlobName`、`Destination` など)を新しいリリースに合わせて
   更新する。`Sha256` は記憶や以前の manifest からではなく、新しいリリースの公表チェックサムから取得する —
   `package` は実際にアセットをダウンロードして照合し、不一致なら失敗する。
4. macOS のみ: `Detection.IncludedApps[].BundleVersion` を新リリースの short bundle version に更新する。
   `AppType: lob` で package の `CFBundleVersion` が変わる場合は `BundleBuildVersion` も更新する。どちらかが
   古いままだと、更新後も Intune の検出ルールが旧バージョンを探し続けるため、新しい content が publish されても
   既存管理下のデバイスが「未インストール/未更新」と判定されることがある。
5. Windows のみ: `SetupFile`、`RepositoryFiles`、検出スクリプトの中に版数依存の参照が残っていないか確認する。
6. バージョンを上げる際に `PackageIdentifier`・`Platform`・`Architecture`・`DisplayName` を変更しない。
   これらを変更すると identity 解決が壊れ(doc/00-overview.md §6.1)、`publish` は既存 app を見つけられずに
   別 app を新規作成してしまい、旧 app と assignment は移行されないまま残る。
7. 通常のフローを実行する — `plan` が新しい manifest を選択する。解決された set に同じ manifest の旧バージョンが
   残っている場合、最高バージョンのみが publish され、他は superseded としてログに出る(doc/00-overview.md §6.8)。

```powershell
relaypublisher plan --base-ref <base-ref> --output manifest-list.json
relaypublisher validate --manifest-list manifest-list.json
relaypublisher package --manifest-list manifest-list.json --output ./out
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

```bash
relaypublisher plan --base-ref <base-ref> --output manifest-list.json
relaypublisher validate --manifest-list manifest-list.json
relaypublisher package --manifest-list manifest-list.json --output ./out
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out \
  --expected-tenant <tenant-id> --dry-run
```

8. `--dry-run` の出力を確認してから、`--dry-run` なしで `publish` を実行する。内部では、`publish` は notes
   metadata から既存 app を解決し、downgrade guard(§6.8)を適用する。`inputHash` の skip 判定の前に app の
   `publishingState` を読み取り、`processing` なら `published` になるまで待機する。`notPublished` では 2 件目を
   作成せず、中断した単一 content version を再利用する。file が 0 件なら最初の file を作成する。stale file が
   ある場合は、対応する終端失敗 state の互換な未 commit file が総数 1 件のときだけ renew して再利用し、
   一致する file が無い場合や複数 file は追加 file を作成せず失敗する。同じ hash の単一 commit 済み
   file は activation から再開する。未知または曖昧な state は app / committed content を削除せず失敗する。
   `published` で hash が一致する場合だけ
   content upload を skip し(§6.7)、それ以外は新しい content version をアップロード・commit する。content の
   activation が完了してから既存 app の metadata、category、assignment を更新する — Graph は app が Published
   でない間これらの write を拒否する。state polling が timeout した場合は Intune の処理完了後に同じ publish を
   再実行し、app を削除・再作成しない。`committedContentVersion` が patch された時点で新しい content が有効になり、
   この tool では戻せない(§6.10) — rollback するには、以前のバージョンの manifest を `--allow-downgrade` 付きで
   再度 publish する。

新しいバージョンフォルダを既存のものと並べて追加する実行可能な例は
[samples/manifests/README_ja.md](../samples/manifests/README_ja.md#powershell-サンプルを新しいバージョンに更新する)
を参照。

## 4d. Intune app category

app entry には、その app が属する Intune app category を宣言できる。category は tenant 全体で共有される
`mobileAppCategory` リソースであり、Relaypublisher は app と既存 category の *relationship* だけを同期する。
category 自体の作成・改名・削除は行わないため、category は事前に Intune 管理センターで作成しておく。

```yaml
Apps:
  - Platform: windows
    Architecture: x64
    Categories:
      - Business Apps
      - Productivity
```

| Manifest | 動作 |
|---|---|
| `Categories` 省略 | app の現在の category を変更しない。category 関連の Graph 呼び出しを一切行わない |
| `Categories: []` | app のすべての category relationship を解除する |
| 1 件以上 | 列挙した集合を app の category 集合そのものとする(それ以外は解除) |

運用上の注意:

- 名前は `mobileAppCategory.displayName` と照合する。大小文字は無視するが、それ以外は verbatim(trim も Unicode
  正規化もしない)。`validate` は tenant に接続しないため、**tenant に存在しない名前は `publish` または
  `publish --dry-run` でしか検出できない**。検出はその app の最初の write より前の preflight で行われる。
  名前の不存在・曖昧一致はその manifest entry だけを失敗させ、batch の残りは継続し、再実行で収束する。
- `publish --dry-run` は tenant catalog と app の現在の category を read し、add/keep/remove の plan を表示する
  だけで write は行わない。新規 app では placeholder ID `(new app)` を表示する。
- content を Published 化してから既存 app の metadata と category relationship を更新する。Graph は
  `publishingState` が `published` でない app へのこれらの write を拒否する。`processing` の app は設定済みの
  polling interval / timeout で待機する。`notPublished` の app は中断した単一 content version を再利用し、未 commit
  file だけを置換するか、同じ `inputHash` の commit 済み file の activation を再開するため、同じ publish の再実行で
  復旧できる。
- result file(`--result-file`)には entry ごとに additive field `categoryOutcome` が 1 つ増える。値は `applied` /
  `unchanged` / `not-requested`、および category 処理に到達しなかった場合の null。category 単位の詳細は console
  出力と log に出る。
- `inputHash` は manifest 全体を対象とするため、**`Categories` だけを変更しても再package と content の再 upload が
  発生し得る**。`Categories` を宣言していない manifest の hash は従来どおり変わらない。
- いずれかの manifest が `Categories` を宣言したら、**その repository を扱うすべての CLI をバージョンで揃える**。
  古い CLI は未知の manifest field を無視して古い hash を計算するため、新旧を交互に実行すると `inputHash` が振動
  して毎回 content が再 upload される。
- 追加の Graph permission は不要。`DeviceManagementApps.ReadWrite.All` で category relationship も操作できる。
  tenant catalog 一覧取得での 403 は identity-wide であり、app 一覧での 403 と同じく batch 全体を停止する
  ([06-troubleshooting_ja.md](06-troubleshooting_ja.md) の 2a を参照)。

## 5. Exit codes

| Exit code | 意味 | Operator action |
|---|---|---|
| `0` | Command が成功しました。 | CI workflow を続行します。 |
| `1` | Validation、packaging、authentication、tenant、Graph、publish のいずれかが失敗しました。 | Error message を読み、manifest または environment を修正して rerun します。 |
| `2` | 未実装 command path 用の予約値です。 | Operator retry ではなく tool implementation gap として扱います。 |

## 6. Workflow setup checklist

`workflows/` の参照 sample をコピーした後、次を確認します。sample は対象 repository にコピーするまで
自動的には有効になりません。

この節は Intune app を publish する**利用者側**の workflow を対象とします。Relaypublisher 自身の CI/CD は
`.github/workflows/` にあり、この repository で既に有効です。そちらの checklist は後述の
「Relaypublisher release pipeline」を参照してください。

### 共通

- [ ] 対象 repository に workflow をコピーし、trigger が参照する manifest / script path が存在する。
- [ ] Entra app に `DeviceManagementApps.ReadWrite.All` application permission と admin consent がある。
- [ ] `AZURE_CLIENT_ID`、`AZURE_TENANT_ID`、Azure login 用の `AZURE_SUBSCRIPTION_ID` を protected CI configuration に保存している。
- [ ] expected tenant を保護し、`publish --expected-tenant <tenant-id>` に渡している。
- [ ] Federated credential の issuer、subject、audience が CI token と完全一致している。
- [ ] `plan`、`validate`、`package`、`publish` のすべてで同じ正確な Relaypublisher CLI version を pin し、各 job が `relaypublisher --version` を log に出している。
- [ ] semantic PKG warning は、protected workflow が明示的に `--force` を渡さない限り非対話 job で fail する。`--force` で hard error を回避していない。

### GitHub Actions

- [ ] `workflows/github-actions/publish-intune-apps.yml` を `.github/workflows/publish-intune-apps.yml` にコピーする。
- [ ] `production` environment を作成し、reviewer または policy で保護する。
- [ ] `id-token: write` は OIDC が必要な job だけに付与し、PR validation job には付与しない。
- [ ] GitHub federated credential に issuer `https://token.actions.githubusercontent.com/`、subject `repo:<owner>/<repo>:environment:production`、audience `api://AzureADTokenExchange` を設定する。
- [ ] `githubRelease` を使う manifest では `Auth.SecretName` の secret（例: `GH_RELEASE_PAT`）を package job だけに渡す。
- [ ] `azureBlob` を使う場合は package job に OIDC login と storage reader role を設定する。
- [ ] publish job が pin された package job の artifact を使用し、再 hash・再検査を行い、Graph write 前に全 entry の preflight を完了する。

### Azure Pipelines

- [ ] `workflows/azure-pipelines/azure-pipelines.yml` を対象 repository root の `azure-pipelines.yml` にコピーする。
- [ ] workload identity federation の Azure Resource Manager service connection を作成または選択し、この pipeline だけを authorize する。
- [ ] service connection が生成した issuer / subject と audience `api://AzureADTokenExchange` で Entra federated credential を設定する。
- [ ] `production` environment と Exclusive Lock check を設定する。
- [ ] sample が使う protected variable group に `AZURE_CLIENT_ID`、`AZURE_TENANT_ID`、`AZURE_SUBSCRIPTION_ID`、expected tenant を登録する。
- [ ] `githubRelease` を使う場合は `Auth.SecretName` の secret（例: `GH_RELEASE_PAT`）を package job だけに map する。
- [ ] `azureBlob` を使う場合は package job が authorized service connection と storage reader role を使うことを確認する。
- [ ] publish job が pin された package job の artifact を使用し、再 hash・再検査を行い、Graph write 前に全 entry の preflight を完了する。

### Relaypublisher release pipeline

これは Relaypublisher repository 自身に対する項目です。利用者 repository には適用しません。

- [ ] `release` GitHub environment を作成し、publishing secrets を repository ではなくこの environment に置く。
- [ ] `NUGET_API_KEY` は nuget.org の package publish 権限のみを持つ key にする。
- [ ] `AZURE_ARTIFACTS_FEED_URL` に feed の v3 `index.json` URL を入れる。URL が workflow のログに出ないよう
      必ず secret にする。
- [ ] user-assigned managed identity を作成し、その client ID / tenant ID / subscription ID を
      `AZURE_ARTIFACTS_CLIENT_ID` / `AZURE_ARTIFACTS_TENANT_ID` / `AZURE_ARTIFACTS_SUBSCRIPTION_ID` に設定する。
- [ ] その managed identity に、この repository の `release` environment を信頼する federated identity
      credential を audience `api://AzureADTokenExchange` で設定する。
- [ ] Azure DevOps 側で、その managed identity を対象プロジェクトの **Contributors** グループに追加する。
- [ ] `packages: write` と `id-token: write` を持つ workflow が `release-publish.yml` だけであることを確認する。
- [ ] `ci.yml` が secrets を一切参照していないことを確認する（fork からの PR を通すため）。

## 7. Production checklist

- Full repository で静的 schema / repository check としての `validate` が成功している。`validate` を PKG 内容検査とは見なしていない。
- `plan` output を `manifest-list.json` として保存し、後続 job で再利用している。
- Package job が changed manifests を再計算していない。
- すべての macOS package source SHA を XAR 検査前に検証し、publish ごとに staging artifact を再 hash・再検査している。
- 選択された全 entry が最初の Graph write 前に preflight を完了し、warning の拒否または hard error が Graph を変更していない。
- Publish job が protected environment と serialized execution で実行される。
- `publish` が常に `--expected-tenant` を使っている。
- すべての job で CLI version が同一に pin され、job log と package metadata から確認できる。
- GitHub release token などの source provider secrets は、必要な job にだけ渡している。
- Authorization header、token、signed package URI、secret value を log や artifact に出していない。
