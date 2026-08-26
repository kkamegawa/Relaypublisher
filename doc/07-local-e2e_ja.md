# ローカル E2E テストガイド

このガイドは、ローカルターミナルから Relaypublisher を E2E で実行する手順を説明します。専用のテスト tenant、テスト用 app identity、テスト用 assignment group を使用してください。実際の `publish` は Intune を変更するため、必ず最初に `--dry-run` を実行し、対象 tenant を確認します。

正式な英語版は [07-local-e2e.md](07-local-e2e.md) です。

## 1. 前提条件

リポジトリのルートディレクトリで実行します。必要なものは次のとおりです。

- `global.json` と互換性のある .NET SDK 10.0 以降。
- Azure CLI (`az`)。
- macOS/Linux では Bash または zsh、Windows では PowerShell 7。
- repository file と package source が利用できる有効な manifest。
- テスト用 Microsoft Entra tenant、Intune 権限、assignment group。
- Windows の `.intunewin` を生成するための Windows マシンまたは runner。
- parser/CLI check 用の決定的な macOS XAR fixture と、tenant 検証用の破棄可能な実 `.pkg` source。
- approval gate、expected-tenant、`--force` の audit trail を備えた protected manual-run environment。

リポジトリの sample manifest は、必ずしも E2E 実行できる fixture ではなく、参照用です。ただし例外が 1 つあります: `samples/manifests/Microsoft/Microsoft.PowerShell/7.6.4/` と `7.6.5/` 配下の PowerShell macOS manifest(`powershell-macos-arm64.yaml` / `-x64.yaml`、合計 4 ファイル)は実在する、公開ダウンロード可能な package を指しており、無編集で `plan` → `validate` → `package` が通ります。それ以外の sample は、schema の形や実世界の制約を記録するために、意図的に validation で失敗する、または解決できない package source を参照しています。どのサンプルがどちらかは、失敗をバグと判断する前に [samples/manifests/README_ja.md](../samples/manifests/README_ja.md) を確認してください。手早いローカルの動作確認を超える用途には、実在する package input を持つ組織用のテスト manifest を使用してください。

## 2. Azure CLI によるローカル認証

Relaypublisher は Microsoft Graph と Azure Blob へのアクセスに `DefaultAzureCredential` を使用します。app-only のローカル E2E テストでは、Microsoft Entra app 登録に対応する service principal として Azure CLI にログインします。`az login --tenant <tenant-id>` だけでは対話型のユーザーログインになり、app 登録に設定した application permission のテストにはなりません。ローカル run が意図した service principal を実際に使う(同じマシンにサインイン済みの他の identity ではなく)ためには、credential chain も固定する必要があります(下記「Run の credential を固定する」参照)。

ログイン前に app 登録を次のように構成します。

- application (client) ID と tenant ID を確認します。
- Microsoft Graph の `DeviceManagementApps.ReadWrite.All` を **アプリケーションの許可 (Application permissions)** から付与し、admin consent を取得します。同名の **委任済み (Delegated)** の方ではありません。委任済みの permission は app-only token に一切現れないため、ポータル上は付与済みに見えても permission 不足と同じ 403 になります。[06-troubleshooting_ja.md](06-troubleshooting_ja.md) の section 2a を参照してください。
- client secret または app に登録した PEM 証明書を準備します。ローカル環境で秘密鍵を保護できる場合は、証明書の利用を推奨します。
- 選択した manifest が Azure Blob を使う場合は、必要な storage scope で service principal に `Storage Blob Data Reader` を付与します。

app-only の Graph token は `.default` scope により app 登録に事前設定された permission を使用します。`DefaultAzureCredential` は Azure CLI の service principal ログインを**利用できます**が、それは credential chain を固定した場合に限られます。固定していないと、サインイン済みの Visual Studio・VS Code・broker の identity が先に試され、黙って勝つことがあり、permission 不足と見分けが付かない 403 になります。[06-troubleshooting_ja.md](06-troubleshooting_ja.md) section 2a と下記「Run の credential を固定する」を参照してください。

この permission は実際の publish だけでなく `publish --dry-run` にも必要です。dry-run は何が変わるかを報告する前に既存の Intune app を解決するため、最初に `GET /deviceAppManagement/mobileApps` を呼び出し、permission がなければ 403 で失敗します。permission を付与する前に pipeline を試す手段として `--dry-run` を使うことはできません。

client secret を使う Bash/zsh の例です。以下の共通構文を使ってください。Bash の `read -p` は zsh と互換性がなく、zsh では `-p` が coprocess からの読み込みを意味します。

```bash
APP_ID="<application-client-id>"
TENANT_ID="<tenant-id>"
printf '%s' 'Client secret: ' >&2
IFS= read -r -s CLIENT_SECRET
printf '\n' >&2
az login --service-principal \
  --username "$APP_ID" \
  --password "$CLIENT_SECRET" \
  --tenant "$TENANT_ID"
unset CLIENT_SECRET
az account show
```

client secret を使う PowerShell 7 の例:

```powershell
$AppId = "<application-client-id>"
$TenantId = "<tenant-id>"
$Credential = Get-Credential -UserName $AppId -Message "Enter the client secret for the service principal"
az login --service-principal `
  --username $Credential.UserName `
  --password $Credential.GetNetworkCredential().Password `
  --tenant $TenantId
$Credential = $null
az account show
```

client secret の代わりに証明書を使う場合は、service principal の秘密鍵を含む PEM 証明書を指定します。

```bash
APP_ID="<application-client-id>"
TENANT_ID="<tenant-id>"

az login --service-principal \
  --username "$APP_ID" \
  --certificate "/path/to/certificate.pem" \
  --tenant "$TENANT_ID"
```

```powershell
$AppId = "<application-client-id>"
$TenantId = "<tenant-id>"

az login --service-principal `
  --username $AppId `
  --certificate "C:\path\to\certificate.pem" `
  --tenant $TenantId
```

### Run の credential を固定する

サインイン後、`DefaultAzureCredential` の chain を固定し、この run がたった今サインインした service
principal を実際に使う(同じマシンにサインイン済みの他の identity ではなく)ようにします
(doc/00-overview.md §6.19)。この設定は shell session 全体に効き、Graph publish 経路と Azure Blob
download の両方をカバーします。未設定の場合 `publish` は warning を出します。

```bash
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
```

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
```

client secret、秘密鍵、access token を shell history、log、manifest、artifact に残さないでください。証明書や秘密鍵を commit しないでください。

service principal に Azure subscription がない場合は、`az login` に `--allow-no-subscriptions` を追加します。これは Graph のみを使うテストには十分です。Azure Blob のテストは `DefaultAzureCredential` を使い、storage scope の RBAC (例: `Storage Blob Data Reader`) が必要ですが、Azure CLI 上で service principal 自体が Azure subscription を持つ必要はありません。

この手順は Microsoft Learn の [Sign in with Azure CLI using a service principal](https://learn.microsoft.com/cli/azure/authenticate-azure-cli-service-principal?view=azure-cli-latest) および [Get access without a user - Microsoft Graph](https://learn.microsoft.com/graph/auth-v2-service) のガイダンスに従っています。

他の Azure CLI command で subscription context が必要な場合は、storage account が存在する subscription を選択します。Blob download の認可自体は、storage scope に対する service principal の RBAC assignment で行われます。

```bash
az account set --subscription <subscription-id>
```

```powershell
az account set --subscription <subscription-id>
```

`az account show` で、期待した tenant と service principal のアカウントが表示されることを確認します。service principal には、必要な Graph/Intune 権限とテスト用 assignment group へのアクセス権が必要です。すべての publish コマンドで tenant guard を指定します。

`--expected-tenant <tenant-id>`

Relaypublisher は Graph に書き込む前に token の `tid` claim を検証し、期待した tenant と一致しない場合は失敗します。

## 3. package input のダウンロード

`package` コマンドは、選択された manifest が参照するファイルを download または copy します。source provider は manifest の各 item で選択します。

| Source type | ローカル認証 | ダウンロード動作 |
|---|---|---|
| `publicHttp` | なし、または `Auth.Type: none` | 匿名でダウンロードします。 |
| `githubRelease` | `Auth.Type: token` の場合に `Auth.SecretName` が指定する環境変数を設定します。 | GitHub API 経由で release asset をダウンロードします。 |
| `azureBlob` | `Auth.Type: workloadIdentity` を指定します。ローカルの `DefaultAzureCredential` は Azure CLI の service principal login を利用します。 | Azure Blob Storage からダウンロードします。 |

private GitHub Release asset を使う場合は、`Auth.Type: token` を設定し、`package` の前に manifest の secret variable を設定します。

```bash
export GH_RELEASE_PAT="<token>"
```

```powershell
$env:GH_RELEASE_PAT = "<token>"
```

環境変数名は `Auth.SecretName` と完全に一致させます。token、storage key、SAS URL、Authorization header を manifest、log、artifact に書かないでください。

`package` は `--output` に指定したディレクトリへ staging 済み package を出力します。Windows app では `.intunewin` と package metadata、macOS app では staging 済み `.pkg` と `package-metadata.json` が出力されます。

CI では同じ output を `intunewin-packages` artifact として job 間で渡します。publish job はこの artifact を download して `--package-dir` に渡し、`plan` が生成した同じ `manifest-list.json` を使用する必要があります。

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

## 4. ローカル E2E ワークフロー

以下の例は source tree の CLI を起動します。`--` は `dotnet run` の option と Relaypublisher の option を分けるために必要です。branch の変更を検証するときは、この source tree command をそのまま使用してください。global tool の `relaypublisher` は公開済み NuGet tool のため、古い content URL 実装が残っている場合があります。修正を含む release version をインストールした後だけ global tool に置き換えてください。

### 4.1 build と test

Bash/zsh:

```bash
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
```

PowerShell:

```powershell
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
```

通常の test コマンドは macOS / Linux でもそのまま安全に使用できます。Windows 専用の IntuneWin パッケージング境界を検証するテストは、
これらのプラットフォームでは MSTest によってスキップされます。IntuneWin の解析、メタデータ、Graph マッピング、macOS 向けのポータブルな
テストは引き続き実行されます。スキップされる Windows 専用のテストカバレッジを実行する必要がある場合は、Windows runner を使用してください。

### 4.2 manifest set を一度だけ確定

対象を絞ったローカルテストでは manifest path を明示します。これにより現在の Git base ref に依存せず、入力集合を確認しやすくなります。

Bash/zsh:

```bash
CLI_PROJECT="src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"
MANIFEST="manifests/<manifest-file>.yaml"

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  plan \
  --manifest "$MANIFEST" \
  --output manifest-list.json
```

PowerShell:

```powershell
$CliProject = "src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"
$Manifest = "manifests/<manifest-file>.yaml"

dotnet run --configuration Release --project $CliProject -- `
  plan `
  --manifest $Manifest `
  --output manifest-list.json
```

変更 manifest のテストでは `plan --base-ref <base-ref> --output manifest-list.json` を使います。後続手順で changed manifest を再計算しないでください。

自組織の manifest を組む前にワークフローを試すには、`--repo-root samples --manifest manifests/Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml` のように実在する manifest を使えます。`--repo-root samples` が必要な理由と、他にどのサンプルが実行可能かは [samples/manifests/README_ja.md](../samples/manifests/README_ja.md) を参照してください。

### 4.3 validate

Bash/zsh:

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  validate --manifest-list manifest-list.json
```

PowerShell:

```powershell
dotnet run --configuration Release --project $CliProject -- `
  validate --manifest-list manifest-list.json
```

package の前にすべての validation error を修正します。validation では manifest schema、path safety、file-backed asset、選択された集合における identity と display name の uniqueness を確認します。package 内容については schema/static validation だけであり、source の download や PKG/XAR の検査は行いません。

### 4.4 package

Windows では通常のコマンドで Windows `.intunewin` を生成します。

```powershell
dotnet run --configuration Release --project $CliProject -- `
  package --manifest-list manifest-list.json --output ./out
```

このコマンドは外部ファイルを download し、SHA-256 を検証し、repository file を staging し、package metadata を生成します。macOS `.pkg` entry では SHA 検証を XAR 検査より先に行います。検査 report には検出 bundle ID/version、選択した primary、source SHA、manifest identity、正確な CLI version を記録します。semantic warning は下記の TTY/non-TTY/`--force` policy に従い、hard error は force できません。

macOS app だけを含む manifest であれば、macOS/Linux でも通常の `package` を実行できます。macOS の packaging には Windows packaging tool の工程がないためです。

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  package --manifest-list manifest-list.json --output ./out
```

macOS/Linux で Windows entry を扱う場合は staging-only を使います。

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  package --manifest-list manifest-list.json --output ./out --stage-only
```

`--stage-only` は対象 entry の `.intunewin` と package metadata を生成しません。この output を Windows の `publish` に使わないでください。CLI には platform 選択 option がないため、混在 list で `--stage-only` を使った場合は、macOS entry 用に `plan --manifest <macos-manifest-path> --output macos-manifest-list.json` のように別の manifest list を作り、その list に対して通常の `package` を実行してから macOS entry を publish します。

### 4.5 publish の preview

Graph に書き込む前に、package directory に対して dry-run を実行します。

Bash/zsh:

```bash
TENANT_ID="<tenant-id>"

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  publish \
  --manifest-list manifest-list.json \
  --package-dir ./out \
  --expected-tenant "$TENANT_ID" \
  --dry-run
```

PowerShell:

```powershell
$TenantId = "<tenant-id>"

dotnet run --configuration Release --project $CliProject -- `
  publish `
  --manifest-list manifest-list.json `
  --package-dir ./out `
  --expected-tenant $TenantId `
  --dry-run
```

選択された app identity、既存 app の照合、package version、input hash、category plan、assignment plan、tenant、platform 固有の mapping error、primary bundle 検査結果を確認します。`publish --dry-run` は staging 済み macOS package を再 hash・再検査し、結果を表示する前に選択された全 entry の preflight を完了します。

対話的な semantic warning では検出 bundle 一覧を確認して `[y/N]` に回答します。非対話実行では protected command が明示的に `--force` を指定しない限り同じ warning で fail します。`--force` は semantic difference だけを確認するもので、破損 archive、曖昧な primary、checksum mismatch、古い/改ざんされた artifact、tenant/Graph safety error は回避できません。

manifest が `Categories` を宣言している場合、dry-run は tenant の category catalog と app の現在の category も
read し、`Category plan for app <id>: N add, N keep, N remove` のブロックを表示します(新規 app は placeholder ID
`(new app)`)。tenant に存在しない category 名を検出できるのはここだけです — `validate` は Graph に接続しません。
後続 entry で warning 拒否や hard error が発生した場合も含め、dry-run では何も書き込まれません。

### 4.6 テスト tenant へ publish

dry-run の内容が正しいことを確認してから、実際の publish を実行します。

Bash/zsh:

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  publish \
  --manifest-list manifest-list.json \
  --package-dir ./out \
  --expected-tenant "$TENANT_ID" \
  --result-file publish-result.json
```

PowerShell:

```powershell
dotnet run --configuration Release --project $CliProject -- `
  publish `
  --manifest-list manifest-list.json `
  --package-dir ./out `
  --expected-tenant $TenantId `
  --result-file publish-result.json
```

Intune admin center で display name、`notes` の management metadata、committed content、detection rule、assignment、および manifest が `Categories` を宣言している場合は app の category を確認します。運用上の情報を含む場合があるため、`publish-result.json` を公開 artifact に含めないでください。

最初の Graph write より前に、command は staging 済みの全 macOS `.pkg` を再 hash・再検査し、選択された全 entry の preflight を完了します。package report だけを信頼せず、現在の byte 列を必ず照合します。artifact が古い、または改ざんされている場合は、同じ manifest list と pin された CLI で `package` を rerun して置き換え、report を手編集しないでください。どの entry で warning を拒否しても、hard error でも、batch の Graph write は 0 件でなければなりません。

Relaypublisher は content hash で skip を判断する前に app の `publishingState` を確認します。
`processing` の app は `published` になるまで polling し、`notPublished` の app は中断した単一 content version を
再利用します。file が 0 件なら最初の file を作成し、対応する終端失敗 state の互換な未 commit file が総数 1 件なら
renew して再利用します。一致しない、または複数 file の場合は追加せず安全に停止します。
同じ `inputHash` の commit 済み file がある場合は activation を再開します。
polling が timeout した場合は Intune の処理完了後に同じ
publish を再実行してください。app を削除・再作成する必要はありません。既存 app の metadata と category
write は content activation 後にだけ実行されます。Graph は app が `Published` でない間これらの write を拒否します。

content upload の Graph URL には app ID の直後に具体的な型キャストが必要です。既定の macOS `pkg` では
`.../mobileApps/<app-id>/microsoft.graph.macOSPkgApp/contentVersions` になります。ログに
`.../mobileApps/<app-id>/contentVersions` が出て `Resource not found for the segment 'contentVersions'`(HTTP 400)
になった場合は、古い CLI が実行されています。現在のソースから Release 構成の CLI を再ビルドし、上記の
source tree command で publish を再実行してください(または修正を含む release version を install します)。
古い global tool に置き換えないでください。この失敗は content version 作成前に発生するため、app を削除・再作成する必要はありません。

PKG でログに `Content upload step 'commit' failed with Graph uploadState 'commitFileFailed'` と出る場合は、
旧 CLI が必要な `[MAC (32 バイト)][IV (16 バイト)]` header なしで ciphertext を upload した、または
ciphertext だけの `sizeEncrypted` を報告した可能性があります。Release CLI を再ビルドします
(`dotnet build IntuneLobPublisher.slnx --configuration Release`)。その後、既存の `manifest-list.json` と
`./out` の package artifact を使い、同じ `publish` command を再実行してください。PKG の暗号化は
`publish` 中に行われるため `package` の再実行は不要です。commit が失敗しただけでは新しい content は
activate されないため、app を削除・再作成しないでください。

次の実行で `The mobile app content cannot be updated before the first content version is committed` が出た場合は、
古い CLI が 2 件目の version を作ろうとしています。現在の source から再ビルドして同じ publish を再実行します。
修正版は、互換な未 commit file を renew できる場合だけ最初の version を再利用します。一致しない古い file が
残る場合は、同じ version に sibling file を追加せず停止します。

使い捨てのテスト tenant で category フローを end-to-end で確認する手順:

1. Intune admin center(**アプリ** > **アプリ カテゴリ**)で使い捨ての category を 1〜2 個作成する
   (例: `Relaypublisher E2E A`、`Relaypublisher E2E B`)。
2. app entry に `Categories: [Relaypublisher E2E A]` を追加し、`publish --dry-run` を実行する(`+` が 1 行)。
   その後実際に publish し、admin center で category が付いたことを確認する。`publish-result.json` は
   `"categoryOutcome":"applied"` になる。
3. 同じ publish をもう一度実行する。plan は `=`(keep)だけになり、result file は
   `"categoryOutcome":"unchanged"` になる — これが冪等性の確認。
4. 一覧を `[Relaypublisher E2E B]` に変更して publish する。plan は B を add し A を remove し、admin center が
   指定どおりの集合になる。
5. `Categories: []` にして publish するとすべての relationship が解除される。次に `Categories` キー自体を削除して
   publish すると、app の category はそのまま維持され、category 関連の Graph 呼び出しも行われない
   (`"categoryOutcome":"not-requested"`)。
6. 終わったら使い捨ての category を tenant から削除する。Relaypublisher は category リソース自体を削除しない。

### 4.7. Primary bundle acceptance run

破棄可能な tenant に対する protected な手動承認 E2E として実行します。決定的 XAR fixture は自動 test に残し、ここでは source から device までの経路を確認するため実 `.pkg` source を使います。

1. 選択する application bundle と2つ目の application bundleを含む fixture/manifest の組を使用します。`validate` が静的 check のみを行うことを確認します。`package` では source SHA 検証が XAR 検査より先に行われ、report に検出 ID、version、selected primary、manifest identity、CLI version が記録されることを確認します。
2. TTY で `publish --dry-run` を実行し、semantic warning を拒否して Graph write が無いことを確認します。TTY なしでも実行し、`--force` が無い限り fail することを確認します。protected な `--force` 承認で再実行し、warning は記録されるが hard error は依然 fail することを確認します。
3. `AppType: pkg` と `AppType: lob` の両 variant を publish します。Graph resource を read-back し、selected primary が `includedApps`/`childApps` の先頭であることを確認します。`lob` では `BundleVersion` → `buildNumber`、`BundleBuildVersion` → `versionNumber`、top-level primary bundle field も確認します。
4. 各 app を破棄可能な test group に assignment し、managed macOS device の check-in を待ち、selected bundle と期待した version が device から report されることを確認します。これは Graph payload read-back では代替できない device-detection E2E です。
5. 変更なしで publish を再実行し、idempotency を確認します。2つ目の app、重複 content、primary の不要な変更が無いことを確認します。`PrimaryBundleId` だけを変更して再package・publishし、primary の順序と device detection が意図どおり変わることを確認します。
6. staging 済み `.pkg` の byte または report を変更して `publish` を実行し、stale/tampered preflight が Graph write 0 件で fail することを確認します。artifact を戻して成功するまで rerun します。

この手順は、protected `intune-e2e` environment で gate された `workflow_dispatch` 専用 workflow として自動化する想定です(`doc/03-ci-github-actions.md` の "Protected manual E2E (Intune publish)" 参照)。手順4の device check-in だけは workflow が完全には自動化できず、人間による out-of-band 確認が必要です。

## 5. 安全な再実行と cleanup

- `plan`、`validate`、`package --stage-only` は安全に再実行できます。
- download または staging に失敗した場合は、同じ `manifest-list.json` を使って `package` を再実行できます。
- `publish --dry-run` は Intune に書き込まずに再実行できます。
- 実際の publish は収束するように設計されていますが、content activation を tool で取り消すことはできません。
- category の `$ref` add/remove は冪等なので、途中で中断した category 同期は次回実行で収束します。
- stale または改ざんされた package artifact は metadata を編集して修復せず、同じ manifest list と正確な CLI version で `package` を再実行します。
- 意図した rollback では、以前の manifest version を package 化し、明示的に `--allow-downgrade` を指定します。
- protected E2E の後は、tenant の test app、content version、assignment、使い捨て category を削除します。不要になったら、ローカルの `out`、`manifest-list.json`、`publish-result.json` も削除します。package output、token、signed download URL は commit しないでください。

CI 由来の package artifact を使う場合は、`publish` 前に download 済み artifact に同じ manifest entry と `package-metadata.json` が含まれていることを確認します。

## 6. Exit codes

| Exit code | 意味 | Operator action |
|---|---|---|
| `0` | Command が成功しました。 | 次の E2E 手順へ進みます。 |
| `1` | Validation、packaging、authentication、tenant、Graph、publish のいずれかが失敗しました。 | Error を読み、manifest または environment を修正して該当手順を再実行します。 |
| `2` | 未実装 command path 用の予約値です。 | Operator retry ではなく tool implementation gap として扱います。 |
