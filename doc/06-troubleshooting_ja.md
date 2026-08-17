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

- Relaypublisher とは独立に、permission 自体が機能するか確認します。

  ```bash
  az rest --method get --url 'https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$select=id,displayName&$top=1'
  ```

  これが成功するのに `publish` は 403 のままなら、permission は正しく、2 つの呼び出しが同じ identity を
  使っていません。`DefaultAzureCredential` は複数の credential を順に試し、Azure CLI login は最初では
  ないため、開発機ではサインイン済みの Visual Studio・VS Code・broker の identity を先に拾うことが
  あります。その identity は通常同じ tenant にあるので `--expected-tenant` では検出できません。Run
  単位で credential を固定します。

  ```bash
  AZURE_TOKEN_CREDENTIALS=AzureCliCredential
  ```

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
| `publish` は成功したが content が変わらない | `inputHash` が保存値と一致し、content upload が skip された(doc/00-overview.md §6.7) | manifest または入力ファイルが実際に変わっているか確認する。`inputHash` が変わっていなければ再アップロードを skip するのは仕様どおり |
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
  `AppType: pkg` のみ有効な `14`/`14.0`/`15`/`15.0`)のいずれでもない。この mapping は `publish`(および
  `--dry-run`)時にのみ実行され `package` では行われないため、publish 前に manifest のバージョン文字列を修正する。
- **`UnsupportedMacOsVersionException`("AppType 'pkg'" に言及)**: manifest が `AppType: lob` かつ
  `Requirements.MinimumOSVersion` に macOS 14 以降を指定している。`macOSLobApp` は Graph v1.0 のままで、
  macOS 13 より先の minimum-OS フラグが無い。`MinimumOSVersion` を下げるか、`AppType: pkg`(Graph beta、
  14/15 に対応)に切り替える。これは Graph API バージョンの制約であり manifest schema のルールではないため
  `validate` では検出されない。`package` も `MinimumOSVersion` を Graph の値へ mapping することは無いため
  検出できず、`publish` 時(および、Graph へ書き込む前にこの種のエラーを表面化させる `publish --dry-run`)に
  のみ表面化する。
- **`Detection.IncludedApps` が欠落または空**: macOS のすべての app entry は `IncludedApps` を 1 件以上
  (`BundleId` + `BundleVersion`)必要とする。これは `publish` ではなく `validate` で fail する。
- **PKG の content upload が `commitFileSuccess` に到達しない**: `PkgContentPreparer` の
  AES-256-CBC + HMAC-SHA256 暗号化形式は Microsoft の公開仕様が無く(doc/00-overview.md §6.13 参照)、
  `IntuneWinContentExtractor` が `.intunewin` の content 解析にすでに依拠しているコミュニティ由来のスキームを
  踏襲し、公開されている `fileEncryptionInfo.mac` の仕様と突き合わせて導出したものである。macOS entry でのみ
  (Windows entry は影響を受けない)commit が繰り返し失敗する場合は、闇雲に retry せず、ログの Graph エラーと
  `client-request-id`/`request-id` を添えて issue を起票する。
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

## 7. Safe rerun rules

- `validate`、`plan`、`package --stage-only` は rerun して安全です。
- `package` は rerun して安全で、同じ input なら同じ deterministic `inputHash` を再現するべきです。
- `publish --dry-run` は rerun して安全です。
- 実 publish は収束するよう設計されていますが、content activation step は tool では undo できません。Rollback は以前の manifest version を `--allow-downgrade` 付きで publish して行います。
