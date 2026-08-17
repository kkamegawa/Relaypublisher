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

client secret を使う Bash/zsh の例:

```bash
APP_ID="<application-client-id>"
TENANT_ID="<tenant-id>"
read -r -s -p "Client secret: " CLIENT_SECRET
echo
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

以下の例は source tree から CLI を起動します。`--` は `dotnet run` の option と Relaypublisher の option を分けるために必要です。global tool としてインストール済みの `relaypublisher` を使う場合は、コマンド先頭を置き換えてください。

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

package の前にすべての validation error を修正します。validation では manifest schema、path safety、file-backed asset、選択された集合における identity と display name の uniqueness を確認します。

### 4.4 package

Windows では通常のコマンドで Windows `.intunewin` を生成します。

```powershell
dotnet run --configuration Release --project $CliProject -- `
  package --manifest-list manifest-list.json --output ./out
```

このコマンドは外部ファイルを download し、SHA-256 を検証し、repository file を staging し、package metadata を生成します。

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

選択された app identity、既存 app の照合、package version、input hash、assignment plan、tenant、platform 固有の mapping error を確認します。

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

Intune admin center で display name、`notes` の management metadata、committed content、detection rule、assignment を確認します。運用上の情報を含む場合があるため、`publish-result.json` を公開 artifact に含めないでください。

## 5. 安全な再実行と cleanup

- `plan`、`validate`、`package --stage-only` は安全に再実行できます。
- download または staging に失敗した場合は、同じ `manifest-list.json` を使って `package` を再実行できます。
- `publish --dry-run` は Intune に書き込まずに再実行できます。
- 実際の publish は収束するように設計されていますが、content activation を tool で取り消すことはできません。
- 意図した rollback では、以前の manifest version を package 化し、明示的に `--allow-downgrade` を指定します。
- 不要になったら、ローカルの `out`、`manifest-list.json`、`publish-result.json` を削除します。package output、token、signed download URL は commit しないでください。

CI 由来の package artifact を使う場合は、`publish` 前に download 済み artifact に同じ manifest entry と `package-metadata.json` が含まれていることを確認します。

## 6. Exit codes

| Exit code | 意味 | Operator action |
|---|---|---|
| `0` | Command が成功しました。 | 次の E2E 手順へ進みます。 |
| `1` | Validation、packaging、authentication、tenant、Graph、publish のいずれかが失敗しました。 | Error を読み、manifest または environment を修正して該当手順を再実行します。 |
| `2` | 未実装 command path 用の予約値です。 | Operator retry ではなく tool implementation gap として扱います。 |
