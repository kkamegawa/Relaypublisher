# トラブルシューティングガイド

このガイドは、Relaypublisher の production failure と recovery path をまとめたものです。

正式ドキュメントは英語版の [06-troubleshooting.md](06-troubleshooting.md) です。

## 0. Recovery command の manifest selection

通常の CI flow は一方向です。`plan` で対象集合を確定して `manifest-list.json` に書き出し、後続の
`validate`、`package`、`publish` は同じ list を入力にします。Repository の manifest tree を探索する場合は
`plan --manifest-root <directory>`、明示した集合には `plan --manifest <path>...`（または `--manifests`）、
後続 command には `--manifest-list <file>` を使います。下記の focused recovery 例で使う直接の
`--manifest <path>` は、単一 manifest の local check 用です。CI の対象集合を再計算するためには使いません。

## 1. Intune notes の management metadata が壊れた

Relaypublisher は management metadata を JSON として Intune app の `notes` field に保存します。`notes` field は Intune admin center から編集できるため、operator が誤って metadata を削除または破損する可能性があります。

期待される recovery path:

1. Manifest の `DisplayName` が同じままであることを確認します。
2. Full manifest root に対して `plan` を実行し、続けて `validate --manifest-list` で repository-wide の `DisplayName` 一意性を確認します。
3. 対象 manifest に対して `publish --dry-run` を実行します。Full batch を確認する場合は同じ manifest list を使います。
4. `DisplayName` に一致する Intune app が 1 件だけであれば、Relaypublisher は DisplayName fallback で解決します。
5. 次の実 publish で、Relaypublisher は fresh management metadata を `notes` に書き戻して app を adopt します。

Commands:

```powershell
relaypublisher plan --manifest-root manifests --output manifest-list.json

relaypublisher validate --manifest-list manifest-list.json

relaypublisher publish --manifest <manifest-path> --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

```bash
relaypublisher plan --manifest-root manifests --output manifest-list.json

relaypublisher validate --manifest-list manifest-list.json

relaypublisher publish --manifest <manifest-path> --package-dir ./out \
  --expected-tenant <tenant-id> --dry-run
```

Management metadata または DisplayName fallback のどちらかで複数 app が一致した場合、publish は fail します。この failure を bypass しないでください。先に重複した Intune app または manifest を修正してから rerun します。

## 2. `TenantMismatchException`

`TenantMismatchException` は Graph token の tenant claim が `--expected-tenant` と一致しないことを意味します。Relaypublisher は Intune に write する前に fail します。

確認項目:

- `<tenant-id>` に使っている CI variable または secret が意図した tenant である。
- Entra app registration がその tenant に所属している。
- Federated credential が CI で使う同じ app registration に設定されている。
- CI login step が意図した `AZURE_CLIENT_ID` と `AZURE_TENANT_ID` を受け取っている。
- GitHub Actions environment または Azure Pipelines service connection が古い identity を参照していない。

Recovery:

1. CI identity または expected tenant value を修正します。
2. 失敗した workflow を rerun します。
3. `--expected-tenant` は維持します。Run を通すために削除しないでください。

## 2a. App 一覧取得で Graph が 403 を返した

```
error: <package-identifier> macos-arm64: Failed to list Intune mobile apps. Graph request to
'/beta/deviceAppManagement/mobileApps?$select=id,displayName,notes' returned 403 (Forbidden): ...
```

`GET /deviceAppManagement/mobileApps` は、`publish --dry-run` を含むすべての publish で最初に実行される
Graph 呼び出しです(後述の「dry-run でも Graph 権限が必要な理由」を参照)。すべての app entry がこの呼び出しを
経由するため、ここでの 401/403 は identity 全体の問題として扱い、entry ごとに同じ error を繰り返さずに
batch を中断します。

まず 2 つの failure class を切り分けます。

- **401** - token を取得できなかった、または token が拒否された。CI login step と `--expected-tenant` を
  確認します(section 2)。
- **403** - token は有効だが identity に権限がない。以下に進みます。

### Token が実際に何を持っているか確認する

App-only token は **application** permission を `roles` claim に持ちます。`roles` が存在しない、または
`DeviceManagementApps.ReadWrite.All`(あるいは `DeviceManagementApps.Read.All`)を含まない場合、403 の
説明がつきます。Access token 自体は secret として扱い、issue・chat・log に貼らないでください。

Bash / zsh:

```bash
TOKEN=$(az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv)
PAYLOAD=$(printf '%s' "$TOKEN" | cut -d. -f2 | tr '_-' '/+')
while [ $(( ${#PAYLOAD} % 4 )) -ne 0 ]; do PAYLOAD="${PAYLOAD}="; done
printf '%s' "$PAYLOAD" | base64 -d | grep -o '"roles":\[[^]]*\]'
unset TOKEN PAYLOAD
```

PowerShell 7:

```powershell
$Token = az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv
$Payload = $Token.Split('.')[1].Replace('-', '+').Replace('_', '/')
$Payload = $Payload.PadRight([int][Math]::Ceiling($Payload.Length / 4) * 4, '=')
$Claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Payload)) | ConvertFrom-Json
$Claims.roles
$Token = $null
```

### 最も多い原因: permission が application ではなく delegated になっている

Microsoft Graph の `DeviceManagementApps.ReadWrite.All` には 2 つの形式があり、ポータルではどちらも同じ
名前で表示されます。ここで機能するのは一方だけです。

| ポータルの permission type | Token claim | Relaypublisher で使えるか |
|---|---|---|
| 委任済み (Delegated) | `scp`(user token のみ) | 使えない |
| アプリケーション (Application) | `roles`(app-only token) | 使える |

Relaypublisher は service principal として client credentials flow で認証するため、得られるのは app-only
token です。Delegated permission はこの token に一切現れないので、ポータル上で「付与済み」「管理者の同意
済み」と表示されていても Graph は 403 を返します。Entra 管理センターで app registration の
**API のアクセス許可** ブレードを開き、`DeviceManagementApps.ReadWrite.All` の **種類** 列が
**アプリケーション** であることを確認してください。**委任済み** になっている場合は、
**アプリケーションの許可** から同じ permission を追加し直して管理者の同意を与えます。委任済みの entry は
その後削除して構いません。

`ReadWrite.All` は read を含むため、`DeviceManagementApps.Read.All` を併せて追加する必要はありません。

### Permission を変更した後

同意は発行済みの token には反映されません。サインアウトしてから再度サインインし、新しい token を取得して
から rerun します。

```bash
az account clear
az login --service-principal --username <application-client-id> --tenant <tenant-id> --certificate <certificate-path>
```

Relaypublisher は 1 回の run の間 token を in-process で cache するため、実行中の process が新しい
permission を拾うことを期待せず、コマンドを実行し直してください。

### `roles` が正しいのに 403 が続く場合

上の `roles` 確認より前の話として、Relaypublisher は新規に token を取得するたびに、それを取得した
identity をログしています。

```
info: IntuneLobPublisher.Core.Publishing.GraphAuthenticationHandler[0] Acquired Graph token for identity
appid=<guid> idtyp=<type> roles=<permission names>.
```

`appid` を意図したアプリ登録の application (client) ID と比較してください。値が異なれば別の identity が
token を取得しています。`idtyp=app` は app-only(client credentials)token であることを確認できます。
それ以外の値(例えば `idtyp=user`、またはこの claim 自体が無い)は service principal ではなく user
identity が使われたことを意味します。これが `az rest` に頼らず誤った identity を確認する最も速い方法です。

Identity が想定外だった場合、または Relaypublisher とは独立に permission 自体が機能するか確認したい
場合:

```bash
az rest --method get --url 'https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$select=id,displayName&$top=1'
```

これが成功するのに `publish` は 403 のままなら、permission は正しく、2 つの呼び出しが同じ identity を
使っていません。`DefaultAzureCredential` は複数の credential を順に試し、Azure CLI login が選ばれる
保証はないため、開発機ではサインイン済みの Visual Studio・VS Code・broker の identity を先に拾うことが
あります。その identity は通常同じ tenant にあるので `--expected-tenant` では検出できません。これは
その場しのぎの回避策ではなく、正式に文書化されサポートされている設定です
([05-operation_ja.md](05-operation_ja.md) §3、[00-overview.md](00-overview.md) §6.19)。トラブル
シューティング時だけでなく、毎回の run で credential chain を固定してください。

```bash
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
```

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
```

403 の前に `AZURE_TOKEN_CREDENTIALS is not set` で始まる warning が `publish` から出ていた場合、chain が
固定されていません。その warning に対処してから rerun し、それ以上 permission の調査を続けないでください。

- Tenant に有効な Intune license があるか確認します。Intune の Microsoft Graph API は license を必要と
  し、license がない tenant は permission に関係なく 403 を返します。
- Tenant で `/beta/` endpoint が利用できるか確認します。App 一覧取得は `macOSPkgApp` が漏れないように
  beta を使っています。関連する pkg 固有の failure は section 6a を参照してください。
- Support case を開くときは error message 中の `client-request-id` と `request-id` を報告します。
  Relaypublisher は両方を message に含めます。これらは correlation id であり secret ではありません。

### dry-run でも Graph 権限が必要な理由

`publish --dry-run` は、何が変わるかを判断する前に既存の Intune app を解決します。Dry-run の出力は app が
新規作成されるか更新されるかを示し、published version と比較するため、解決が必須だからです。この解決は
dry-run の分岐より前で行われるので、`--dry-run` にも実際の publish と同じ Graph read 権限が必要です。
権限を付与せずに pipeline を試すために dry-run を使うことはできません。

## 3. Downgrade が skip された

Relaypublisher は既定で、manifest の `PackageVersion` が Intune management metadata に保存された version より低い場合、publish を skip します。

典型的な原因:

- `plan` が古い manifest を選択した。
- Release branch に古い package version が含まれている。
- Operator が意図的に以前の package に rollback しようとしている。

選択された manifest を確認します。

```powershell
Get-Content manifest-list.json
```

```bash
cat manifest-list.json
```

意図的な rollback でない場合は、manifest version を更新するか、`plan` で使った base ref を修正します。

意図的な rollback の場合は、`--allow-downgrade` を付けて以前の package を明示的に publish します。

```powershell
relaypublisher publish --manifest <manifest-path> --package-dir ./out `
  --expected-tenant <tenant-id> --allow-downgrade
```

```bash
relaypublisher publish --manifest <manifest-path> --package-dir ./out \
  --expected-tenant <tenant-id> --allow-downgrade
```

Rollback 後は Intune で app を確認し、assignments が意図した manifest state と一致していることを確認します。

## 3a. バージョンアップしても既存 app が更新されない

既存 app の `PackageVersion` を上げた後の症状(doc/05-operation_ja.md §4c):

| 症状 | 考えられる原因 | 対処 |
|---|---|---|
| 既存 app が更新されず、Intune に別 app が増えた | `DisplayName`・`PackageIdentifier`・`Platform`・`Architecture` をバージョンと一緒に変更してしまい、identity 解決(doc/00-overview.md §6.1)が既存 app と一致しなくなった | 元の identity フィールドに戻して正しい app が更新されるよう再 publish し、余分に増えた app は Intune 管理センターで手動削除する(doc/00-overview.md §6.11 — リタイアは本ツールのスコープ外) |
| run が `skipped (downgrade)` と報告した | manifest の version が Intune 側 metadata に保存された version より低い | 上記 §3 を参照 |
| `Invalid operation: app's PublishingState is not 'Published'` で失敗した | Intune が app を処理中だった、または committed content version がまだ activate されていない状態で metadata / category を更新しようとした | 現在の CLI で同じ publish を再実行する。`processing` は `published` になるまで待機し、`notPublished` は `inputHash` が一致していても content を upload する。polling が timeout した場合は Intune の処理完了後に再実行する。app は削除・再作成しない |
| `publish` は成功したが content が変わらない | `inputHash` が保存値と一致し、content upload が skip された(doc/00-overview.md §6.7) | manifest または入力ファイルが実際に変わっているか確認する。`inputHash` が変わっていなければ再アップロードを skip するのは仕様どおり |
| Windows file detection が validation で落ちる、または常に失敗する | `Path` を repository path として扱った、未対応の operation / operator を使った、または version 値が不正 | `exists` / `version` だけを使用する。対象端末の drive-rooted / root-relative / UNC / environment-variable path を設定し、`version` の `ComparisonValue` は 1～4 part の数値にする |
| macOS: publish 後もデバイス側の検出バージョンが変わらない | `Detection.IncludedApps[].BundleVersion` を新リリースに合わせて更新していない | manifest を修正して再 publish する |
| ログに旧バージョンの `superseded by version X` が出る | 解決された set に同一 identity の複数バージョンが含まれる場合の仕様(doc/00-overview.md §6.8) | 対処不要 — 最高バージョンのみが publish される |

## 4. GitHub release token がない

`githubRelease` source が `Auth.Type: token` を使う場合、Relaypublisher は `Auth.SecretName` に指定された environment variable から token を読みます。

Variable が missing または empty の場合:

- Manifest が意図した `SecretName` を使っていることを確認します。
- CI job が secret を同じ名前の environment variable に map していることを確認します。
- Pull request job で private release assets を download する前提になっていないことを確認します。

例:

```yaml
Auth:
  Type: token
  SecretName: GH_RELEASE_PAT
```

CI environment は package job に `GH_RELEASE_PAT` を公開する必要があります。

## 5. Azure Blob source を download できない

`azureBlob` source では、Relaypublisher は static secret ではなく workload identity を使います。

確認項目:

- Package job で OIDC が有効になっている。
- CI identity に必要な storage scope の storage reader role が付与されている。
- Login step で `AZURE_CLIENT_ID`、`AZURE_TENANT_ID`、`AZURE_SUBSCRIPTION_ID` が利用できる。
- Manifest の `AccountName`、`Container`、`BlobName` が意図した package object を指している。

Identity または storage permission を修正してから rerun します。Storage account key や signed package URI を manifest、log、artifact に置かないでください。

## 6. Package metadata がない

`publish` は `package` が書き出した `package-metadata.json` を使います。

Publish が package metadata missing を報告した場合:

- Package job が成功していることを確認します。
- Publish job が `intunewin-packages` artifact を `--package-dir` に渡した path と同じ場所に download していることを確認します。
- `package` と `publish` が同じ `manifest-list.json` を使っていることを確認します。
- Package metadata を手動編集せず、`package` を rerun します。

## 6a. macOS 固有の失敗

- **`UnsupportedMacOsVersionException`("no known macOS minimum-operating-system mapping")**:
  `Requirements.MinimumOSVersion` が `MacOsMinimumOperatingSystemTable` の認識する値(`10.13`〜`13.0`、または
  `AppType: pkg` のみ有効な `14`/`14.0`/`15`/`15.0`/`26`/`26.0`)のいずれでもない。この mapping は `publish`(および
  `--dry-run`)時にのみ実行され `package` では行われないため、publish 前に manifest のバージョン文字列を修正する。
- **`Resource not found for the segment 'contentVersions'` (HTTP 400)**: 古い CLI が app ID 直後の
  OData 型キャストを付けずに content endpoint を呼び出している。Release 構成で CLI を再ビルドし、同じ
  package artifact を使って publish を再実行する。修正版は `microsoft.graph.macOSPkgApp`（pkg）、
  `microsoft.graph.macOSLobApp`（lob）、`microsoft.graph.win32LobApp`（Windows）の型付き URLを
  content version 作成から files/commit まで一貫して使用する。このエラーが content version 作成時に
  発生した場合、app を削除・再作成する必要はない。
- **`UnsupportedMacOsVersionException`("AppType 'pkg'" に言及)**: manifest が `AppType: lob` かつ
  `Requirements.MinimumOSVersion` に macOS 14 以降を指定している。`macOSLobApp` は Graph v1.0 のままで、
  macOS 13 より先の minimum-OS フラグが無い。`MinimumOSVersion` を下げるか、`AppType: pkg`(Graph beta、
  14/15 に対応)に切り替える。これは Graph API バージョンの制約であり manifest schema のルールではないため
  `validate` では検出されない。`package` も `MinimumOSVersion` を Graph の値へ mapping することは無いため
  検出できず、`publish` 時(および、Graph へ書き込む前にこの種のエラーを表面化させる `publish --dry-run`)に
  のみ表面化する。
- **`Detection.IncludedApps` が欠落または空**: macOS のすべての app entry は `IncludedApps` を 1 件以上
  (`BundleId` + `BundleVersion`)必要とする。これは `publish` ではなく `validate` で fail する。
- **PKG で `commitFileFailed` になる(または content upload が `commitFileSuccess` に到達しない)**:
  旧 `PkgContentPreparer` は AES-256-CBC の ciphertext だけを upload していた。Intune が要求する upload
  stream は `[MAC (32 バイト)][IV (16 バイト)][ciphertext]` で、`MAC = HMAC-SHA256(macKey, IV || ciphertext)`
  とする。`sizeEncrypted` にも 48 バイトの header と ciphertext の両方を含める(doc/00-overview.md §6.13 参照)。
  そのため SAS URI への upload は成功しても、commit 中に Graph が content を拒否することがある。現在の source
  から Release 構成の CLI を再ビルドし、既存の package artifact を使って同じ `publish` を再実行する。PKG の
  暗号化は `publish` 中に行われるため `package` の再実行は不要である。commit 失敗では app の有効な content は
  変更されないので、app を削除・再作成しない。アップグレード後も `commitFileFailed` が続く場合は、ログの
  Graph error と `client-request-id`/`request-id` を添えて issue を起票する。
- **`The mobile app content cannot be updated before the first content version is committed` (HTTP 400)**:
  以前の初回 upload が app を `notPublished`、content version を未 commit のまま残し、古い CLI が 2 件目の version を
  作成しようとしている。Release CLI を再ビルドして同じ publish を再実行する。修正版は既存 version と file を列挙し、
  file が 0 件なら最初の file を作成する。stale file がある場合は、対応する終端失敗 state で現在の package と
  名前・サイズが一致する未 commit file が総数 1 件のときだけ renew して再利用する。一致する file が無い、または複数なら、
  stale な失敗 file が残る version を Intune が activate できないため、追加 file を作成せず停止する。app、content version、
  file は自動削除しない。複数 version、複数の一致 file、または曖昧な commit state も明確なエラーで停止する。
- **`v14_0`/`v15_0` が `'microsoft.graph.macOSMinimumOperatingSystem'` に存在しないという 400
  (`GraphRequestException`、修正済み)**: 旧バージョンはすべての macOS app payload に `v14_0`/`v15_0` を
  (`false` であっても)常に含めていたが、Graph v1.0 の `macOSMinimumOperatingSystem` にはこれらのプロパティ
  自体が存在しない(beta のみに存在する)。このため `Requirements.MinimumOSVersion` の値に関わらず、
  `AppType: lob` の create/update がすべて失敗していた。`MacOsMinimumOperatingSystemPayload` は現在、
  v1.0 向けの場合はこれらのフィールド(および新規追加した beta 専用の `v26_0`)を null のままにし、
  リクエストボディから省略する(`false` として送信しない)。
- **macOS `AppType: pkg` entry に特有の 403/404(`GraphRequestException`)**: pkg app の作成・更新・
  content upload はすべて Graph **beta** 経由で行われる(`macOSPkgApp` は v1.0 に存在しない)。service
  principal の Graph 権限(section 2a)とテナントの beta API 可用性を確認する。Windows や
  `AppType: lob`(v1.0 のまま)の publish には影響しないため、pkg entry だけが失敗し batch は継続する。
  App 一覧取得の 403 が run 全体を止めるのとは異なる。
- **デバイス側エラー `2016214710`("The preinstall script provided by the admin failed")**:
  `Scripts.PreInstall` のスクリプトがデバイス上で非 0 終了した。スクリプトが前提条件を待っている場合の想定内
  挙動のこともあり、Intune は次回 device check-in で再試行する。継続して失敗する場合はスクリプトのロジックと
  終了コードを確認する — Relaypublisher はスクリプトの実行時挙動を検知できず、内容が正しくアップロードされた
  ことしか保証しない。`Scripts.PostInstall` の失敗はこの形では一切報告されない。終了コードに関わらず app は
  "success" のまま表示される(doc/01-manifest-schema.md §5.4.2)。
- **`ManifestLoadException`(`Scripts.PreInstall` / `Scripts.PostInstall` が "does not exist")**:
  publish 時に `--repo-root` からスクリプトのパスが解決できない。`Icon` の存在確認
  (doc/01-manifest-schema.md §5.4.1)と同じ仕組みをスクリプトにも適用したもの。`validate` は Graph 呼び出し前に
  これを検出するため、`publish` でのみ表面化する場合は 2 つのコマンド間で repository root や作業ディレクトリが
  異なっている可能性が高い。

## 6b. Intune app category の失敗

- **`CategorySyncException`("does not exist in the tenant")**: `Categories` の名前に一致する
  `mobileAppCategory` が tenant にない。`validate` は tenant に接続しないため、これは `publish`
  (`--dry-run` を含む)の preflight でのみ検出される。category は Intune 管理センターで作成する
  (本 tool は意図的に category を作らない)か、manifest の綴りを直す。照合は大小文字のみ無視するため、
  前後の空白がある名前は別名になる(そもそも `validate` が空白付きの名前を拒否する)。preflight はその app の
  最初の write より前に走るので、この失敗時点で app は作成も更新もされていない。失敗するのはその manifest entry
  だけで、batch の残りは継続し、再実行で収束する。
- **`CategorySyncException`("matches N tenant categories")**: 大小文字だけが異なる category が tenant に複数
  存在する。manifest の名前ではどれか 1 つを一意に指せない。Intune 管理センターで重複を改名または削除する。
- **`$ref` 失敗を包んだ `CategorySyncException`**: add(`POST .../categories/$ref`)または remove
  (`DELETE .../categories/{id}/$ref`)が失敗した。重複 add と不在 remove は既に成功として扱われるため、これは
  実際の失敗である。log の Graph error と `client-request-id` / `request-id` を確認する。次回実行は app の現在の
  relationship から plan を再計算するため、途中まで適用された状態からでも収束する。
- **tenant category 一覧取得での 403**: `Categories` を宣言したすべての entry が同じ一覧を通るため identity-wide
  として扱い、`GraphAccessDeniedException` で batch 全体を停止する(app 一覧の 403 と同じ)。2a に従って対処する。
  category relationship に `DeviceManagementApps.ReadWrite.All` を超える権限は不要。
- **category だけを変更したのに content が再 upload された**: 仕様どおり。`inputHash` は manifest 全体を対象と
  する(doc/00-overview.md §6.7)ため、`Categories` の変更で hash が変わり、再package と再 upload が発生する。
- **`Categories` を使い始めてから毎回 content が再 upload される**: 同じ repository に対して異なるバージョンの
  CLI を使っている。古い CLI は未知の `Categories` field を無視して古い `manifestHash` を計算するため、新旧を
  交互に実行すると `inputHash` が振動する。CI とローカルの CLI をバージョンで揃える。
- **result file の `categoryOutcome` が null**: その entry では category 処理に到達しなかった(skip、dry-run、
  preflight 前の失敗)。category が解除されたという意味ではない。app 解決後に失敗した場合でも `appId` は null の
  ままになるが、これは許容された result file の形。

## 6c. Windows の作成が `SetupFilePath` で失敗する

- **Graph 400 `The Win32LobApp must have a valid value for the SetupFilePath property.`**: このリリースで
  修正済み。以前のビルドは `win32LobApp` の create/update payload(`Win32LobAppPayloadMapper`)に
  `setupFilePath`(と `fileName`)を一切含めていなかったため、新規 Windows アプリの最初の書き込みが
  Graph に拒否されていた -「`0 published, ... 1 failed`」となり app 自体は作成されない(コンテンツ
  アップロードより前に失敗するため、テナント側に不完全な app が残ることもない)。
  `setupFilePath`/`fileName` のマッピングを含むバージョン(doc/adr.md の 2026-08-25 のエントリ参照)に
  CLI をアップグレードして `publish` を再実行すること。値はすでに必須項目である
  `Package.IntuneWin.SetupFile` から取得するため、manifest やパッケージの変更は不要。

## 7. Safe rerun rules

- `validate`、`plan`、`package --stage-only` は rerun して安全です。
- `package` は rerun して安全で、同じ input なら同じ deterministic `inputHash` を再現するべきです。
- `publish --dry-run` は rerun して安全です。
- 実 publish は収束するよう設計されていますが、content activation step は tool では undo できません。Rollback は以前の manifest version を `--allow-downgrade` 付きで publish して行います。
- category の `$ref` add/remove は冪等です。重複 add と不在 remove はどちらも成功として扱われるため、途中で中断した category 同期は次回実行で収束します。
