# Overview / Requirements

## 1. 初期要件

winget のような仕組みで、Intune 管理の LOB アプリケーションを Git に push すると Intune にアプリ登録する。

GitHub へ YAML 定義を commit したら、Intune へアプリを upload する。

条件:

- winget の schema を流用する。
- 組織内向けのため、以下は不要。
  - license
  - history
  - release notes
- public に認証なしで download できるものは repository に binary を持たない。
- public download 可能なものは YAML に download URL を書くだけにする。
- 組織内 binary は以下から取得する。
  - Azure Blob
  - private GitHub Release
- Azure Blob は Azure 認証で download する。
- private GitHub Release は GitHub PAT などの認証で download する。
- Intune に存在しない app であれば新規追加。
- Intune に存在すれば更新。
- macOS / Windows Arm64 / Windows x64 を別 app として管理する。

---


## 2. 追加要件

追加で確定した要件:

1. 小規模運用なので assignment も YAML で管理する。
   - 対象 group は GUID 指定。
2. `.intunewin` は自動生成する。
   - `.intunewin` は script + 実行 app で構成されることが多い。
   - script は repository 内に置く。
   - binary は別の public URL に置くケースを考慮する。
   - script は platform / architecture、たとえば x64 / Arm64 で異なる場合がある。
3. 既存 app 更新時は同じ Intune app ID を更新する。
   - assignment を維持する。
4. Intune app の display name に version は含めない。
5. GitHub hosted runner を主対象にする。
   - Azure Pipelines も検討する。
6. 実装言語は .NET とする。
   - レビュー容易性を重視。

---


## 3. 基本方針

### 3.1 ゴール

GitHub repository または Azure Repos に Intune LOB app 定義 YAML を commit すると、CI が起動し、定義に従って app package を取得・検証・必要に応じて package 化し、Intune に新規登録または更新する。

### 3.2 管理方針

- winget manifest の考え方を流用する。
- 組織内向けなので license / history / public release notes は省略する。
- Intune 上では以下を別 app として管理する。
  - Windows x64
  - Windows Arm64
  - macOS x64
  - macOS Arm64
  - 必要なら macOS universal
- 同一 app 判定は `PackageIdentifier + Platform + Architecture` を基本とする。
- Intune に存在しなければ新規追加。
- Intune に存在すれば更新。
- public に認証なしで取得可能な binary は Git に置かない。
- private / internal なものは source provider 経由で取得する。

---


## 4. Repository 構成案

```text
repo/
  manifests/
    Contoso/
      Contoso.Tool/
        1.2.3/
          Contoso.Tool.yaml
  scripts/
    windows/
      x64/
      arm64/
      common/
  src/
    IntuneLobPublisher.Cli/
    IntuneLobPublisher.Core/
    IntuneLobPublisher.Intune/
    IntuneLobPublisher.Azure/
    IntuneLobPublisher.GitHub/
  tests/
    IntuneLobPublisher.Core.Tests/
    IntuneLobPublisher.Intune.Tests/
    IntuneLobPublisher.IntegrationTests/
  samples/
    manifests/
    scripts/
  docs/
    manifest-schema.md
    operation.md
    troubleshooting.md
  workflows/
    github-actions/
      ci.yml
      publish-intune-apps.yml
      release-nuget-tool.yml
    azure-pipelines/
      azure-pipelines.yml
      release-nuget-tool.yml
  .gitignore
  LICENSE
  SECURITY.md
```

`workflows/` 配下は参照用サンプルであり、この repository で自動的に有効になる workflow ではない。
GitHub Actions の publish / CI sample は対象 repository の `.github/workflows/` に、Azure Pipelines の
publish sample は対象 repository の root にコピーしてから、`doc/05-operation.md` の workflow setup
checklist に従って secret、variable、environment、service connection を設定する。

---


## 6. 重要な設計判断

### 6.1 App identity

Intune 側に独自の package identifier field はないため、検索 key を明示的に決める。

推奨:

```text
PackageIdentifier + Platform + Architecture
```

Intune app の `notes` に management metadata を保存する。

```json
{
  "managedBy": "intune-lob-manifest",
  "packageIdentifier": "Contoso.Tool",
  "packageVersion": "1.2.3",
  "platform": "windows",
  "architecture": "x64",
  "manifestPath": "manifests/Contoso/Contoso.Tool/1.2.3/Contoso.Tool.yaml",
  "manifestHash": "...",
  "inputHash": "...",
  "sourceCommit": "..."
}
```

`inputHash` は manifest とパッケージ入力ファイル群から計算する決定的ハッシュ(6.7 参照)。
`.intunewin` 自体の SHA256 は暗号鍵がビルドごとにランダムなため identity / skip 判定には使わない。

照合順:

1. `notes` 内の management metadata
2. `DisplayName` fallback

照合時のルール:

- metadata / DisplayName いずれの照合でも**複数件一致した場合は fail** する(誤った app を上書きしない)。
- DisplayName fallback で一致した場合は、その app を「adopt」して notes に management metadata を書き戻す(手動編集や初回移行からの修復経路)。
- `notes` は Intune 管理センターで管理者が編集可能なフィールドであることを運用ドキュメントに明記する。手動編集で metadata が壊れても DisplayName fallback + adopt で復旧できる。
- 書き込む metadata JSON は `notes` の文字数上限に収まることを publish 前に検証する。
- `validate` コマンドは repository 全体で `PackageIdentifier + Platform + Architecture` および `DisplayName` の一意性を lint する(fallback 照合の前提を守るため)。

### 6.2 Display name

Version は含めない。

```text
Contoso Tool [Windows x64]
Contoso Tool [Windows Arm64]
Contoso Tool [macOS x64]
Contoso Tool [macOS Arm64]
```

理由:

- assignment を維持しやすい。
- app ID を維持しやすい。
- Intune app 一覧が増殖しにくい。

バージョンアップは常にこの同一 app への in-place 更新で行う。新規 app を作成して Intune の supersedence
関係(旧バージョンを新バージョンで置き換える機能)で繋ぐ運用は、本ツールのスコープ外とする(6.11 と同様、
必要なら Intune 管理センターで手動運用する)。具体的な更新手順は doc/05-operation.md §4c を参照。

### 6.3 Assignment sync

既定は `merge`。

```yaml
AssignmentSync: merge
```

明示的に YAML を正とする場合のみ `replace`。

```yaml
AssignmentSync: replace
```

事故防止のため、初期値は `merge` がよい。

### 6.4 Azure 認証

GitHub hosted runner の場合、Azure VM の managed identity は使えないため、GitHub Actions OIDC + Microsoft Entra ID workload identity federation を使う。

Azure Pipelines の場合も、Azure Resource Manager service connection with workload identity federation を使う。

### 6.5 Graph API 権限と app 登録要件

Intune を操作する service principal には以下が必要。

- Microsoft Entra ID に app 登録を作成し、**application permission `DeviceManagementApps.ReadWrite.All`** を付与して admin consent を実施する。
- GitHub Actions 用に federated credential を追加する。subject claim は少なくとも production environment に限定する。

```text
repo:<owner>/<repo>:environment:production
```

- Azure Pipelines 用には workload identity federation の service connection に対応する federated credential を追加する。
- `azure/login` は Azure CLI session を確立するだけであり、Graph token は `DefaultAzureCredential` 経由で `https://graph.microsoft.com/.default` scope で取得する。`DefaultAzureCredential` の credential chain 解決順は環境に依存し、`AzureCliCredential` が選ばれる保証はない(6.19 参照)。`AZURE_TOKEN_CREDENTIALS` で明示的に固定することを推奨する。
- `subscription-id` は Intune 操作自体には不要だが `azure/login` の入力として必要。

### 6.6 Changed detection (`--changed`) の定義

ここでの `--changed` は changed detection の設計上の呼称であり、現在の CLI に独立した
`--changed` option があることを意味しない。CLI では `plan --base-ref` がこの判定を実行する。

「changed」とは **git diff で変更された manifest ファイル**を指し、比較基準は次のとおり。

| Trigger | 比較基準 |
|---|---|
| pull_request | PR の merge-base(`github.event.pull_request.base.sha`) |
| push (main) | `github.event.before`(直前の push 先端) |
| workflow_dispatch | 基準なし。明示的な manifest 一覧指定、または全件を対象とする |

注意点:

- `actions/checkout` は既定 `fetch-depth: 1` のため diff 基準が取れない。**`fetch-depth: 0` を必須**とする。
- `github.event.before` が zero SHA になるケース(ブランチ新規作成、force push 直後)は全件 fallback とする。
- manifest 以外(`scripts/**` など)の変更は、その script を参照する manifest を逆引きして対象に含める。
- validate / package / publish の各 CI job が独立に changed を再計算すると job 間で対象集合がズレる。**最初の job(validate)で確定した manifest 一覧を `manifest-list.json` として artifact 化し、後続 job はそれを入力にする。**

### 6.7 冪等性と決定的 input hash

`.intunewin` は IntuneWinAppUtil が毎回ランダムな暗号鍵で暗号化するため、同一入力でもファイルハッシュが毎回変わる。生成物のハッシュでは更新スキップ判定ができない。

代わりに **決定的 input hash** を定義する。

```text
inputHash = SHA256(
    manifest 正規化ハッシュ
  + 各入力ファイル(RepositoryFiles / ExternalFiles / SetupFile)の相対パスと SHA256 を
    パス順にソートして連結したもの
)
```

正規化と連結の contract は次のとおりとする。

- manifest は YAML の構文木を loader で model に変換した後、現在の実装が使用する `System.Text.Json` の既定 property order、camelCase の property name、null property の省略、空白なし、UTF-8 の canonical JSON として serialize する。property order は独立した相互運用 contract として固定せず、model または serializer の順序を変更する場合は hash 互換性への影響を確認する。YAML の formatting、property の記述順、comment の変更は manifest hash を変えない。
- manifest hash は canonical JSON の UTF-8 bytes に SHA256 を適用した lowercase hexadecimal とする。loader が無視する未知の YAML property も hash の入力には含めない。
- staging root からの各入力ファイルの相対 path は `/` 区切りに正規化する。各 file の entry は `relative-path`、LF、lowercase hexadecimal の file SHA256 の順に連結する。
- file entry は path を `StringComparer.Ordinal` で昇順に sort する。OS の path separator、locale、case-insensitive sort は使用しない。
- input hash の入力文字列は、manifest hash の後に各 file entry を LF で連結したものとし、末尾に余分な LF は付けない。その UTF-8 bytes に SHA256 を適用し、lowercase hexadecimal で保存する。
- 同一の model と staging input で再実行した場合は同一 hash になり、YAML の formatting / comment だけを変更した場合は hash が変わらないことを test で保証する。

- `inputHash` を notes metadata に保存する。
- publish 時、Intune 側 metadata の `inputHash` と一致すればコンテンツアップロードをスキップする(assignment 差分のみ適用)。
- 再実行(retry)しても同じ結果に収束することを acceptance criteria とする。

### 6.8 ダウングレード防止とバージョンフォルダのライフサイクル

manifest はバージョン別フォルダで管理するが、app identity はバージョンを含まないため以下を仕様とする。

- publish 時、Intune 側 metadata の `packageVersion` と manifest の `PackageVersion` を比較し、**バージョンが下がる場合は既定で skip + warning** とする。意図的なロールバックは `--allow-downgrade` で明示する。
- 同一 push で同じ `PackageIdentifier + Platform + Architecture` の複数バージョンが changed になった場合、**最高バージョンのみ**を処理し、他は skip としてログに出す。
- 旧バージョンの manifest フォルダは削除せず残してよい(履歴として機能する)。ただし changed にならない限り処理対象にはならない。
- 既存 app のバージョンアップは、新しいバージョンフォルダを追加する(既存フォルダを上書きしない)のが正規の手順である。運用手順は doc/05-operation.md §4c を参照。

### 6.9 同時実行制御

並走する CI run が同時に resolver を実行すると、双方が「存在しない」と判定して app を二重作成するレースがある。

- GitHub Actions では publish を含む workflow に `concurrency: { group: intune-publish, cancel-in-progress: false }` を設定して直列化する。
- Azure Pipelines では production environment に **Exclusive Lock** check を設定する。
- resolver が複数一致を検出した場合は fail する(6.1 参照)。

### 6.10 トランザクション境界と失敗時の扱い

Rollback 機能は実装しないが、Win32 コンテンツ更新のトランザクション境界を明文化する。

- 新しい content version の作成・ファイルアップロード・commit までは、**既存クライアントには旧コンテンツが配信され続ける**。この区間での失敗は安全であり、再実行すれば収束する。
- `win32LobApp.committedContentVersion` を PATCH した時点で新コンテンツが有効になる。**この操作以降は戻せない**(戻すには旧バージョンの manifest を `--allow-downgrade` で再 publish する)。
- app 本体のプロパティ PATCH と assignment 適用は個別に冪等であり、部分失敗しても再実行で収束する。

### 6.11 App 削除・リタイアのライフサイクル

manifest を削除しても Intune 側の app は削除しない。**削除・リタイアは本ツールのスコープ外**とし、Intune 管理センターで手動運用する。

将来の拡張余地として `deprecate` / `retire` コマンド(assignment を外す / app を削除する)の名前だけ予約しておく。

### 6.12 テナントガード

誤ったテナントへの publish を防ぐため:

- CLI に `--expected-tenant <tenant-id>` オプションを設ける。取得した token の `tid` claim と照合し、不一致なら何も変更せず fail する。
- CI では environment ごとの変数(placeholder: `<tenant-id>`)から渡す。
- 現時点では single tenant 運用を前提とする。複数環境(検証→本番)は environment 単位の federated credential と protected environment で分離する。

### 6.13 macOS app type の選定

Intune の macOS PKG 配布には 2 種類あり、制約が異なる。

| | macOS LOB app (`macOSLobApp`) | macOS app (PKG) / unmanaged (`macOSPkgApp`) |
|---|---|---|
| 署名 | Developer ID Installer 署名必須 | 未署名可 |
| サイズ上限 | 2 GB | 8 GB |
| ロゴ | 必須(ないと一覧に表示されない) | 任意 |
| uninstall intent | 可 | 非対応(required / available のみ) |
| pre/post install script | 不可 | 可(Intune 管理エージェント必要) |

**既定は `macOSPkgApp`(unmanaged PKG)** とし、manifest の `AppType` で `lob` に切り替え可能とする。

検出は **`IncludedApps`(bundleId + version のリスト)** で行う。manifest に必須フィールドとして定義する(schema は `01-manifest-schema.md` 参照)。

validation ルール:

- `AppType: pkg` の app に `Intent: uninstall` の assignment があれば fail。
- `AppType: lob` の場合は Icon(ロゴ)を必須とし、2 GB 超の PKG を fail とする。

**Graph API バージョン**: `macOSPkgApp` は **beta 専用**(v1.0 に存在しない)。そのため `AppType: pkg` の app に
関するすべての Graph 呼び出し ― 作成・更新、content upload(contentVersions/files/commit)、notes /
committedContentVersion の patch、app resolution 用の一覧取得 ― は `/beta/` を経由する。`macOSLobApp` は
v1.0 に存在するため `AppType: lob` は `/v1.0/` のまま。両者は同一の CLI 実行内で混在しうるため、Graph 呼び出し
の実装(`GraphMacOsAppClient` / `GraphMobileAppContentClient` / `GraphIntuneAppDirectory`)は各呼び出しごとに
使用する API バージョンを判定する。副作用として、v1.0 の `macOSMinimumOperatingSystem` には macOS 14 以降の
フラグが無いため、`AppType: lob` で `Requirements.MinimumOSVersion` に macOS 14 以降を指定すると publish 時に
fail する(`AppType: pkg` への切り替えが必要)。

**サポートする `Requirements.MinimumOSVersion`**(`MacOsMinimumOperatingSystemTable` が保持するマッピング):
`10.13` / `10.14` / `10.15` / `11`(`11.0`)/ `12`(`12.0`)/ `13`(`13.0`)は `AppType: pkg` / `lob` の両方で
使用できる。`14`(`14.0`)/ `15`(`15.0`)/ `26`(`26.0`)は Graph beta 専用の `v14_0` / `v15_0` / `v26_0`
フラグを使うため `AppType: pkg` でのみ使用でき、`AppType: lob` で指定すると `UnsupportedMacOsVersionException`
で fail する。上記いずれのマッピングも持たないバージョン文字列も同様に fail する。

**pre/post install script**(`AppType: pkg` 限定、issue #86): Graph `macOSPkgApp` は `preInstallScript` /
`postInstallScript`(型 `macOSAppScript`、プロパティは base64 エンコードされた `scriptContent` のみ)を持つが、
`macOSLobApp` / `macOSDmgApp` には存在しない。manifest 側は app entry 直下の `Scripts.PreInstall` /
`Scripts.PostInstall`(repository-relative path)で表現し、`Platform: windows` または `AppType: lob` への指定は
validation error とする。

- スクリプト本文は決定的 **inputHash には含めない**(`Icon` / `Detection.ScriptFile` と同じ前例)。app メタデータの
  更新(`UpdateAppAsync`)は publish のたび無条件に実行されるため、スクリプトのみの変更は最大 8 GB になり得る
  `.pkg` の再アップロードを伴わずに反映される。
- `plan --base-ref` の changed detection(§6.6 参照)は `scripts/**` の変更も対象 manifest の逆引きに含める
  (`PlanService.EnumerateReferencedFiles`)。
- 各スクリプトは 15360 文字未満、UTF-8(BOM 無し)、shebang(`#!`)で開始する必要がある(Intune の shell script
  前提条件)。改行コードは base64 化の直前に CRLF → LF へ正規化する。
- 運用前提として Intune management agent for macOS 2309.007 以降が必要。pre-install が非 0 終了で app は
  "failed" となり次回 check-in で再試行される。post-install の失敗は報告されない(app は "success" のまま)。

**content upload のバイト列レイアウト**(`PkgContentPreparer`): Windows は IntuneWinAppUtil が生成する
`.intunewin` の content entry をそのままアップロードするだけで済むが(`IntuneWinContentExtractor` が ZIP
entry を無加工でストリームする)、macOS には同等のツールが無いため `.pkg` の暗号化を本ツールが in-process で
行う。Intune がアップロード対象のバイト列に要求するレイアウトは **`[mac (32 バイト)][iv (16 バイト)][AES-256-CBC
ciphertext]`** であり、これは `.intunewin` の content entry がすでに持っている形式と同一である。Graph の
`fileEncryptionInfo` (https://learn.microsoft.com/graph/api/resources/intune-apps-fileencryptioninfo) は
`mac` / `iv` / 各種鍵を commit 呼び出しの別パラメータとしても要求するため、アップロードする ciphertext 自体には
このヘッダが不要に見えるが、実際には両方が必要になる。Microsoft の公式リファレンス実装
(`microsoftgraph/powershell-intune-samples` の `LOB_Application/Application_LOB_Add.ps1`、
`EncryptFileWithIV` 関数)で確認済み: 出力ファイルの先頭に `mac` + `iv` 分のバイトを予約してから ciphertext を
書き込み、書き終えた後にオフセット 0(mac)とオフセット 32(iv)へ書き戻す。Graph へ報告する `sizeEncrypted` は
この**ヘッダを含めたファイル全体の長さ**であり、ciphertext だけの長さではない。このヘッダが欠落していると
content file は `azureStorageUriRequestSuccess` までは進むが `commitFileSuccess` に到達しない
(doc/06-troubleshooting.md §6a 参照)。

### 6.14 Manifest schema のバージョニングとソース指定の統一

- winget の `ManifestVersion` に相当する **`SchemaVersion`** を top-level 必須フィールドとして最初から導入する。互換性のない変更は major を上げ、CLI は未知の major を fail とする。
- ソース指定(`ExternalFiles` の各項目と macOS の `Source`)は**同一の item shape** に統一する: `Type` + type 固有フィールド + 共通の `Auth` block。`AuthSecretName` のような type ごとの独自認証フィールドは廃止する。

### 6.15 IntuneWinAppUtil の供給チェーン保護

IntuneWinAppUtil.exe は全パッケージの中身に触れるため:

- 使用するバージョンは設定ファイルまたは CLI オプションで**固定(pin)できる**。指定がない場合は GitHub の公式リポジトリ(`microsoft/Microsoft-Win32-Content-Prep-Tool`)の**最新リリースを取得**する。
- バージョンを pin し、既知の SHA256 が設定されている場合は、ダウンロード後に**照合**し、不一致なら fail する。
- 最新リリース取得時は照合する既知ハッシュがないため、取得したバイナリの SHA256 を計算して package metadata に記録する(監査可能性の確保)。
- 使用したツールのバージョンとハッシュを package metadata JSON に記録する。

### 6.16 Graph throttling

Intune 系 Graph API は 429 が発生しやすい。すべての Graph 呼び出しで:

- `429` / `503` 時は `Retry-After` ヘッダーを尊重して retry する(上限回数付き exponential backoff)。
- 失敗時は `client-request-id` / `request-id` をログに出す。

### 6.17 配布形態(NuGet global tool)

本ツールの配布形態は **NuGet global tool** を正とする。

- NuGet package id は `relaypublisher` 固定。
- 実行コマンド名は `relaypublisher` 固定。
- バージョンは Git tag(`vX.Y.Z`)を唯一の正本とし、`dotnet pack -p:Version=<X.Y.Z>` で CI から注入する。
- `csproj` に固定バージョン文字列は置かない(ローカル検証用の fallback は許容)。
- `nuget.org` への publish は CI でのみ実行し、重複 version は `--skip-duplicate` で冪等に扱う。
- macOS 向けの初期配布導線も `dotnet tool install --global relaypublisher` を標準とする(Homebrew tap は別トラックで検討)。

### 6.18 テスト実行環境

Windows 専用の IntuneWin パッケージング境界を検証するテストは、Windows 上でのみ実行する。

- `IntuneWinPackagerTests`、`IntuneWinToolResolverTests`、`WindowsStagingServiceTests` には MSTest の `[OSCondition(OperatingSystems.Windows)]` を付与する。
- macOS / Linux では、通常の `dotnet test` の discovery/実行時にこれらのテストクラスがスキップされる。この仕組みはワークフロー固有のシェルフィルタや runner の環境変数に依存しない。
- IntuneWin ZIP の展開、パッケージメタデータの読み取り、Graph payload のマッピング、macOS publish などポータブルな挙動を検証するテストは、非 Windows プラットフォームでも引き続き実行される。
- この設計では専用の Windows CI job は追加しない。Windows 専用のテストカバレッジ自体を実行する必要がある場合のみ、Windows runner が必要になる。

### 6.19 Credential の決定性(`AZURE_TOKEN_CREDENTIALS`)

`DefaultAzureCredential` は複数の credential source(Environment、Workload Identity、Managed Identity、Visual Studio、VS Code、Azure CLI、Azure PowerShell、broker など)を固定順で試す。開発機で `az login --service-principal` していても、同時に Visual Studio / VS Code / broker のいずれかにサインイン済みだと、`DefaultAzureCredential` はそちらの identity を先に選ぶことがある。その identity は同じ tenant であることが多いため 6.12 のテナントガード(`--expected-tenant`)では検出できず、結果として得られる 403 は実際の権限不足と見分けが付かない。

対処方針:

- **CLI に `--credential` option は追加しない。** Azure.Identity 自身が `new DefaultAzureCredential()`(引数なし)でもネイティブに読む環境変数 `AZURE_TOKEN_CREDENTIALS`(1.15.0 以降)を正式な固定手段として文書化する。コード変更なしに `publish` の Graph credential と `package`/`plan` の Azure Blob credential(`AzureBlobDownloader`)の両方の解決を同時に支配できるため、独自オプションを追加する理由がない。
- `AZURE_TOKEN_CREDENTIALS` が未設定の場合、`publish` は実行の先頭で warning を出す。error にはしない — CI のように chain が実質的に決定的な環境では未設定でも問題は起きないため。
- Graph token を新規取得するたびに、その token の `appid` / `idtyp` / `roles` を Information レベルで記録する。いずれも secret ではない(GUID・token 種別・permission 名であり、`client-request-id`/`request-id` と同じ扱い)。access token 本体は決してログに出さない。これにより 403 の切り分けに `az rest` を使わずとも、log 上の `appid` を意図した app 登録の client ID と比較するだけで identity の食い違いが分かる。

これは推奨事項であり、AGENTS.md の設計上の不変条件には追加しない。

参照: [Credential chains in the Azure Identity library for .NET](https://learn.microsoft.com/dotnet/azure/sdk/authentication/credential-chains)、[Use deterministic credentials in production environments](https://learn.microsoft.com/dotnet/azure/sdk/authentication/best-practices#use-deterministic-credentials-in-production-environments)。

---


## 16. Final recommended implementation order

1. Manifest model / validation
2. Windows staging
3. publicHttp download + SHA256
4. IntuneWinAppUtil integration
5. Graph authentication
6. Intune app resolver
7. Win32 app create/update
8. Assignment merge
9. GitHub Release provider
10. Azure Blob provider
11. Azure Pipelines support
12. macOS support

---


## 17. Notes

.NET で実装する場合の推奨:

- .NET 10 / C#。テストフレームワークは MSTest。
- NuGet パッケージは Microsoft 製を優先する。Microsoft 製で要件を満たせない場合のみサードパーティを使う。
- Graph SDK は最初は使わず、`HttpClient` + `Azure.Identity`。
- IntuneWin 生成は Windows runner + `IntuneWinAppUtil.exe`。
- validation は JSON Schema より C# model + `FluentValidation`。
- app identity は `PackageIdentifier + Platform + Architecture`。
- display name は version なし。
- assignment sync は既定 `merge`。
- 更新スキップ判定は `.intunewin` のハッシュではなく決定的 input hash(6.7)。
- changed detection は validate job で確定し `manifest-list.json` を後続 job に渡す(6.6)。
- Arm64 は Graph の `allowedArchitectures` で表現する(v1.0 の `applicableArchitectures` enum に arm64 はない)。
- `DefaultAzureCredential` の credential chain 解決順は環境依存で保証されないため、`AZURE_TOKEN_CREDENTIALS` で明示的に固定する(6.19)。
