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

manifest schema に optional field を追加するときの hash 互換性(#99):

- 新しい optional field は **nullable + 初期値なし**で定義する。canonical JSON は null property を落とすため、その
  field を宣言していない既存 manifest の `manifestHash` / `inputHash` は変わらない。非 nullable の空 collection
  (`= []`)にすると常に `"field":[]` が出力され、repository 内の**全** manifest の hash が変わって初回実行で
  全 app が再package / 再upload される(macOS PKG は最大 8 GB)。この不変条件は pinned hash の test で固定する。
- `ManifestLoader` は `IgnoreUnmatchedProperties()` のため、新 field を宣言した manifest を**古い CLI** で処理すると
  古い hash が計算される。新旧 CLI を交互に実行すると `inputHash` が振動して毎回 upload が発生するため、
  新 field(`Categories` など)を使い始めたら **CI と手元の CLI バージョンを揃える**こと。
- 逆に、field を宣言した manifest の hash は変わる。カテゴリだけを変更した manifest でも content 再package /
  再upload が発生し得る(6.20)。

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

- 新しい content version の作成・ファイルアップロード・commit までは、**既存クライアントには旧コンテンツが配信され続ける**。この区間での失敗は安全であり、再実行時は Graph の未完了 content state を確認して収束させる。
- `win32LobApp.committedContentVersion` を PATCH した時点で新コンテンツが有効になる。**この操作以降は戻せない**(戻すには旧バージョンの manifest を `--allow-downgrade` で再 publish する)。
- 既存 app の publish は `publishingState` を先に確認する。`processing` の場合は `published` になるまで待機し、`notPublished` の場合は保存済み `inputHash` が一致していても content を upload・activate する。未知の state は即時に fail する。待機が timeout した場合は失敗として報告し、app を削除・再作成せずに再実行で復旧する。
- `notPublished` の app は、最初の content version が未 commit のまま残っている可能性がある。content version が 0 件なら新規作成し、1 件ならその version を再利用する。再利用する version に未 commit file だけがある場合は、それらを削除して現在の package を新しい file として upload する。現在の `inputHash` と保存済み `inputHash` が一致し、単一の file が commit 済みなら、file を削除せず `committedContentVersion` の PATCH から再開する。content version が複数ある、commit 済み file と未 commit file が混在する、または commit 済み file と現在の input の対応を証明できない場合は、app / version / commit 済み file を自動削除せず fail する。
- content の commit と `committedContentVersion` PATCH、`publishingState = published` の確認を完了してから、既存 app のプロパティ PATCH、category relationship、assignment を適用する。Graph は `publishingState` が `published` でない app へのこれらの更新を拒否する。
- app 本体のプロパティ PATCH、category relationship の `$ref` add/remove、assignment 適用は個別に冪等であり、部分失敗しても再実行で収束する。category relationship は content の**後**に適用する(6.20)。content upload や assignment sync が失敗しても、次回実行時に Graph の現在値から plan を再計算して収束する。

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

`contentVersions` は `mobileLobApp` から継承されるため、content upload の URL では app ID の直後に
具体的な OData 型キャスト(`microsoft.graph.win32LobApp` / `microsoft.graph.macOSPkgApp` /
`microsoft.graph.macOSLobApp`)を含める。`/mobileApps/{id}/contentVersions` のようにキャストを省略すると、
Graph が `Resource not found for the segment 'contentVersions'`(HTTP 400)を返すことがある。create、files、
ファイル状態の取得、`renewUpload`、`commit` のすべてで同じ型付きルートを使用し、型セグメントは許可リストで検証する。

**macOS PKG content upload のバイト列と暗号化**: `PkgContentPreparer` は、Windows のような
`IntuneWinAppUtil` が無いため、staged `.pkg` を in-process で AES-256-CBC(PKCS7) 暗号化する。Graph の
SAS URI へ送るバイト列は、ciphertext だけではなく、先頭に **`[MAC (32 バイト)][IV (16 バイト)]`** を付けた
次のレイアウトにする。

```text
[MAC (32 bytes)][IV (16 bytes)][AES-256-CBC ciphertext]
```

ここで `MAC = HMAC-SHA256(macKey, IV || ciphertext)` であり、`IV || ciphertext` が HMAC の対象である。
暗号化 key、`macKey`、IV、MAC は `fileEncryptionInfo` として `commit` に渡すが、アップロードする content
stream にも MAC と IV の header が必要である。Graph の `sizeEncrypted` は ciphertext の長さではなく、
この **48 バイトの header を含む全体長**(`32 + 16 + ciphertext.Length`)を指定する。header が欠落したり
`sizeEncrypted` が ciphertext のみの長さだったりすると、SAS への upload 自体は成功しても Graph の
`commitFileFailed` になる(復旧方法は doc/06-troubleshooting.md §6a を参照)。

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

### 6.20 Intune app category(GitHub #99)

Intune の app category は tenant 共有の `mobileAppCategory` リソースであり、app 側からは scalar property ではなく
`categories` navigation relationship として見える。Relaypublisher は **category リソース自体のライフサイクル(作成 /
改名 / 削除)を管理せず、app との relationship だけを宣言的に同期する**。

- manifest の宣言は `Apps[]` 配下の `Categories`(doc/01-manifest-schema.md §5.8)。省略 / 空配列 / 1 件以上で
  意味が異なり、model は nullable(`List<string>?`)とする。省略時は category 関連の Graph read も write も行わない。
- 名前解決は tenant の category 一覧を `@odata.nextLink` に従ってページング取得し、`OrdinalIgnoreCase` の完全一致で
  行う。**0 件一致・複数件一致は、その app entry の最初の category write より前に fail** させる。
- Graph 呼び出しは次の 4 つ。関連解除では `mobileAppCategories/{id}` 自体を DELETE せず、必ず app 側の `$ref` を
  DELETE する。

  | 操作 | 呼び出し |
  |---|---|
  | tenant catalog 取得 | `GET /{version}/deviceAppManagement/mobileAppCategories` |
  | app の現在値取得 | `GET /{version}/deviceAppManagement/mobileApps/{appId}/categories` |
  | 関連付け | `POST /{version}/deviceAppManagement/mobileApps/{appId}/categories/$ref` |
  | 関連解除 | `DELETE /{version}/deviceAppManagement/mobileApps/{appId}/categories/{categoryId}/$ref` |

- API version は既存 client と同じ規則(Windows `win32LobApp` と macOS `macOSLobApp` は v1.0、macOS `macOSPkgApp` は
  beta)。`$ref` body の `@odata.id` は `GraphClientOptions.BaseAddress` の scheme + authority と、**その request と
  同じ version segment** から組み立てる。host も version もハードコードしない(`BaseAddress` は `/v1.0/` で終わるため、
  そこに相対結合すると beta request に v1.0 の参照を載せてしまう)。
- **処理順序**は次で固定する(6.10 のトランザクション境界に従う)。

  1. app resolution と downgrade guard
  2. category preflight(tenant 名前解決 + 既存 app なら現在の relationship 取得と plan 作成)
  3. app create(新規 app の場合のみ)
  4. content publish / activation(`publishingState` が `published` になるまで待機)
  5. app metadata update(既存 app の場合のみ)
  6. category relationship apply(add を先、remove を後)
  7. assignment plan / apply

  Graph は `publishingState` が `published` でない app の metadata や category relationship を拒否するため、content を
  Published 化してから metadata、category、assignment を適用する。新規 app では preflight で名前解決だけを済ませ
  (app ID がまだ無いので per-app GET は行わない)、作成後に content を activate してから解決済み ID で add を適用する。
  既存 app が `processing` の場合は同じ polling interval / timeout で `published` を待ち、`notPublished` の場合は
  hash 一致でも content を再 upload する。dry-run は read のみ行い、plan を表示して write は行わない。新規 app の plan
  では `(new app)` を app ID placeholder として使う。
- `$ref` の冪等性: `GraphRetryHandler` は 429/503 で request body ごと再送するため、POST が二重に届き得る。
  **「既に関連付け済み」を明示するレスポンスだけを成功として扱い**、DELETE の 404 も成功として扱う。判定できない
  4xx は失敗のままとする(400 / 409 を一律に握り潰さない)。Learn には既存 category の `$ref` request example が
  無いため、POST の重複判定は専用ヘルパー(`CategoryRefResponseClassifier`)に隔離し、実サービスの挙動が判明したら
  そこだけを直せるようにする。
- 失敗分類: 名前の不存在・曖昧一致・`$ref` 失敗は `CategorySyncException`(`PublisherException` 派生)とし、
  その manifest entry だけを失敗させて batch は継続する(6.10)。ただし **tenant category 一覧の 401/403 は
  identity-wide** なので `GraphAccessDeniedException` のままとし、CLI は batch を中断する(#94 と同じ扱い)。
- 必要な application permission は既存の `DeviceManagementApps.ReadWrite.All` のまま(6.5)。同時実行制御も既存の
  publish 直列化で足りる(6.9)。category ID や名前は management metadata の `notes` に保存しない。
- `inputHash` は manifest 全体を対象とする現行契約のまま(6.7)。したがってカテゴリだけを変更した manifest でも
  content 再package / 再upload が発生し得る。

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
