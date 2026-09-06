# ADR(Architecture Decision Record)

`doc/task.md`・`doc/plan.md` に記載のない仕様変更を、日付・該当 task・変更理由とともに箇条書きで記録します。仕様を変更する必要がある場合は、必ずこのファイルを確認し、変更理由が以前の修正と矛盾しないか確認してください。矛盾する可能性がある場合はユーザーに承認を求めます。

このファイルが 200 行を超えた場合は phase 単位で分割します。

## 2026-09-06: publish の SAS 認証 403 回復・result file 一本化・manifest エントリ単位の Graph セッション (Issue #150)

- **決定**: `AzureStorageBlockBlobUploader` の `StageBlockAsync` / `CommitBlockListAsync` が Azure Storage
  から 403 `AuthenticationFailed` を受けた場合、SAS の残り有効期限を見て「期限に余裕があれば同一 SAS で
  document 化された伝播時間(最大 30 秒)を上回るまで再送し、それでも回復しなければ `renewUpload` で
  SAS を取り直したうえで再度待機する」経路(issue #150 の Phase A / Phase B)に入る。renewal 回数は
  403 回復専用に少数(既定 1 回)へ制限し、既存の期限接近時の予防的 renewal とはカウンタを共有しない。
  この回復全体は 1 回の stage/commit 呼び出しごとに 1 つの deadline(既定 5 分)で区切り、呼び出し元の
  `CancellationToken` にリンクした専用の `CancellationTokenSource` で実際にキャンセルする。回復に使い
  切った場合は `ContentUploadRejectedException`(新規)に変換してそのエントリだけを failed とし、
  呼び出し元の cancellation はそのまま `OperationCanceledException` として区別する。
  - **理由**: 複数 manifest を含む `manifest-list.json` の `publish` が 2 件目のパッケージで
    `Azure.RequestFailedException`(403 `AuthenticationFailed` /
    `AuthenticationErrorDetail: SAS identifier cannot be found for specified signed identifier`)で
    落ちる事象が本番の Azure Pipelines で発生した。**原因は未確定**である。ログに SAS の取得時刻・
    有効期限が残っておらず、期限切れは確認できない。最有力仮説は Intune が発行する `azureStorageUri`
    が stored access policy(signed identifier)に紐づく service SAS であり、ポリシーの作成・更新の
    反映に最大 30 秒程度かかりうるという Microsoft のドキュメント
    (<https://learn.microsoft.com/rest/api/storageservices/define-stored-access-policy>)だが、
    Intune 側がポリシーを失効・ローテートさせている可能性も同じエラーで説明できるため排除できない。
    どちらの仮説でも必要な対処(同一 SAS での待機・再送と `renewUpload` によるやり直し)は同じであり、
    この修正で追加した診断ログ(段階・試行回数・経過時間・残り有効期限・renewal 回数・Storage の
    `x-ms-request-id`)を伴う次回の実行結果で確定させる。
  - **影響**: `ContentUploadOptions` に `SasActivationRetryDelay` / `SasActivationSameSasWindow` /
    `SasActivationMaxRenewals` / `SasActivationTimeout` を追加。`MobileAppContentUploadOrchestrator` の
    `IsRecoverableUncommittedUploadState` に `azureStorageUriRequestSuccess` /
    `azureStorageUriRenewalSuccess` を追加し、blob 送信だけが中断した未 commit file も
    `renewUpload` して再送する対象にした(既存の file 数・metadata 一致条件は変更しない)。この復旧は
    6.9 の排他実行が実際に効いていることに依存する(6.10 に明記)。`PublishCommand.PublishEntriesAsync`
    の result file 出力を単一の exit point(`finally`、`CancellationToken.None`)に統合し、想定外の
    例外もエントリを記録してから中断するようにした — 従来は `Azure.RequestFailedException` が CLI の
    全 catch をすり抜けてプロセスが即死し、result file が一切書かれず 6.10 の「1 件の失敗はバッチを
    止めない」規約も効いていなかった。
  - **今後の注意**: 上記のとおり本当の原因は未確定であり、伝播遅延は最有力仮説にすぎない。次回の実行で
    診断ログから原因が確定したら、この節を更新すること。`renewUpload` が新しい signed identifier を
    発行するかどうかも Learn からは断定できないため、renewal を主ではなく待機の次善策として少数回に
    限定してある。

- **決定**: `publish` の Graph セッション(`HttpClient`・認証ハンドラ・トークンキャッシュ)を manifest
  エントリごとに新規作成し、そのエントリの処理完了後に破棄する(`IPublishSession` /
  `PublishComposition`)。`DefaultAzureCredential` 自体は実行全体で共有し、セッションだけを作り直す。
  - **理由**: ユーザーからの直接の指示(「publish 内の REST API 実行処理は yaml ごとに open→publish→close
    の処理とする」)。**ただし、これは上記 403 の対策ではない**。SAS は現在も file ごとに取得して
    Azure Storage SDK 独自のパイプラインに渡しており、Graph の `HttpClient` を作り直しても直接の効果は
    ない。指示された構造変更として、効果と代償を明記したうえで実施した。
  - **影響**: `GraphAuthenticationHandler` のトークンキャッシュがセッションごとになるため、
    `--expected-tenant` の照合とトークン ID のログ出力(`Acquired Graph token for identity ...`)が
    実行全体で 1 回ではなく manifest エントリごとに走るようになる(6.12)。資格情報を共有する理由は、
    `DefaultAzureCredential` がインスタンスごとにチェーンを再解決するため、`AZURE_TOKEN_CREDENTIALS`
    未設定時(6.19)にエントリ間で解決先が変わりうる ID ドリフトを自ら作らないため。
  - **今後の注意**: エントリごとに接続プールとトークンキャッシュを失う代償があることを踏まえ、将来
    パフォーマンス上の懸念が出た場合はこの ADR を確認してから戻すこと(戻す場合は 6.19 の ID
    ドリフトへの対策を別途用意する)。

## 2026-09-05: Windows file-system detection (Issue #141)

- **決定**: Windows `Detection.Type` は既存の `script` に加えて `file` を受け付け、`exists` と `version` の
  `win32LobAppFileSystemRule` だけを生成する。
  - **理由**: file version による Intune 検出では PowerShell detection script を repository に追加・管理する必要が
    ない。一方、Graph の `modifiedDate`、`createdDate`、`sizeInMB` は comparison value の形式を別途定義・検証する
    必要があるため、この変更には含めない。
  - **影響**: `exists` の manifest は `Operator` / `ComparisonValue` を持たず、mapper が Graph の
    `operator: notConfigured` を生成する。`version` は 6 つの比較演算子と 1～4 part の数値 comparison value を
    必須とする。Graph の unset sentinel `notConfigured` は manifest 入力として許可しない。
- **決定**: file detection の `Path` / `FileOrFolderName` は target-device value として validation し、
  `PathSafety` を使わない。
  - **理由**: drive-rooted、root-relative、UNC、environment-variable-rooted の path は Intune が管理対象端末上で
    評価する正当な値であり、repository root の下に解決する `PathSafety` に渡すと誤って拒否されるため。
  - **影響**: file detection は detection script を読み取りも staging もしない。script 用と file 用の fields は
    相互排他にし、macOS は既存の `IncludedApps` 検出を維持する。
- **決定**: Graph の `rules` は System.Text.Json polymorphic payload とし、derived rule に手書きの
  `@odata.type` property を持たせない。
  - **理由**: base-typed collection では derived property が失われる。polymorphic discriminator と同名 property
    を併用すると serialization failure になるため。
  - **影響**: script rule は `Win32LobAppPowerShellScriptRulePayload` に改名する。既存 script manifest の
    canonical hash を維持するため、file 用の全 fields は nullable・初期値なしで定義する。file criteria の変更は
    manifest hash / inputHash を変更するが、script body は引き続き hash に含めない。

## 2026-09-02: manifest 作成スクリプトのソース契約 (Issue #140)

- **決定**: `tools/yamlcreate.ps1` の `publicHttp` は `Auth.Type: none` に限定する。
  - **理由**: PR #139 の設計にあった `token` の選択肢は、既存の `PublicHttpSourceProvider` が認証付き取得を拒否する契約と矛盾していたため。新たな認証方式は追加せず、生成内容を既存の取得処理に合わせる。
- **決定**: GitHub Release の自動ハッシュ取得は public / private ともアセット ID の REST API を使い、ソースの認証は YAML キー順に依存せず読み取る。
  - **理由**: private release で利用できないブラウザー向け URL と、`Auth` が `Sha256` より前にある場合の認証情報の欠落を解消するため。
- **決定**: 表示する URL の認証情報・クエリ・フラグメントは除去し、保存する manifest では元の値を保持する。
  - **理由**: ダウンロード先の署名をログへ出さず、取得に必要な URL は維持するため。

## 2026-08-30: nuget.org Trusted Publishing (OIDC) への移行 (Issue #131)

- **決定**: `nuget.org` への自動 publish は GitHub Actions の `.github/workflows/release-publish.yml` に一本化し、
  `NuGet/login` v1.2.0 (`8d196754b4036150537f80ac539e15c2f1028841`) で Trusted Publishing を利用する。
  - **理由**: 長期有効な NuGet API key を repository/environment secret に保持せず、GitHub の OIDC token から
    publish 直前に発行される短期 API key へ移行するため。
  - **影響**: publish job は `id-token: write` と `NUGET_USER` を必要とする。action output は同じ job の
    push にだけ渡し、secret や artifact として保存しない。既存の GitHub Packages / Azure Artifacts の push は変更しない。
- **決定**: Trusted Publishing policy の Repository Owner=`kkamegawa`、Repository=`Relaypublisher`、
  Workflow File=`release-publish.yml` (basename only)、Environment=`release` を正とする。
  - **理由**: NuGet が発行元 workflow と environment を限定できるようにし、実ファイル名(hyphen)との不一致を防ぐため。
- **決定**: Azure Pipelines から `nuget.org` へ publish する設計・参照サンプルを削除し、Azure Pipelines は
  Intune publish と Azure Artifacts の用途に限定する。
  - **理由**: 本リポジトリの NuGet.org Trusted Publishing の自動化経路を一つにし、長期 API key を使う別経路を残さないため。

## 2026-08-21: macOS PKG アップロード HTTP 400 の根本修正(doc/task.md 同日エントリ参照)

- **決定**: macOS `.pkg` の content upload でアップロードするバイト列を、`ciphertext` のみから
  `[mac (32B)][iv (16B)][ciphertext]` に変更する。
  - **理由**: `doc/00-overview.md` §6.13 は従来「HMAC は IV ‖ ciphertext に対して計算し、アップロードする
    のは ciphertext のみ」と記述していたが、これは誤りだった。Intune はアップロードするバイト列自体に
    `[mac][iv]` ヘッダを要求する。Windows がこれまで問題なく動いていたのは、IntuneWinAppUtil が生成する
    `.intunewin` の content entry が最初からこのレイアウトを持っており、`IntuneWinContentExtractor` が
    それを無加工でストリームしていたためであって、"ciphertext のみで良い" という設計判断が正しかった
    わけではない。Microsoft 公式のリファレンス実装(`microsoftgraph/powershell-intune-samples` の
    `LOB_Application/Application_LOB_Add.ps1`、`EncryptFileWithIV` 関数)で確認済み。
  - **影響**: `sizeEncrypted` としてサーバへ報告する値は、この 48 バイトヘッダを含むファイル全体の長さに
    なる(`EncryptedContentSize` は `FileInfo.Length` を返すため実装上は自動的に正しくなる)。
  - **今後の注意**: この暗号化フォーマット自体は Microsoft の公開仕様書が存在しない(コミュニティ由来 +
    リファレンス実装からの逆算)。将来 Graph 側の挙動が変わった場合は、まず公式サンプル
    (`microsoftgraph/powershell-intune-samples`)の該当関数を再確認してから実装を変更すること。

- **決定**: `committedContentVersion` PATCH が Graph から 400(`PublishingState is not 'Published'`)を
  返した場合の「失敗後にリトライ」方式を廃止し、「書き込み前に `publishingState` が `processing` から
  抜けるのを待つ」方式(`WaitWhilePublishingStateProcessingAsync`)に置き換える。
  - **理由**: 旧方式は 400 を catch してから `publishingState == "published"` になるまでポーリングして
    いたが、新規アプリ(まだ一度も content version をコミットしていない)は `notPublished` のまま
    無期限に留まり、`published` へ遷移させるのはまさにこの PATCH 自身である。そのため旧方式は
    構造的にデッドロックし、`PublishingStateTimeout`(既定 10 分)経過後に本来の 400 を
    `ContentUploadTimedOutException` にすり替えて隠していた。
  - **影響**: `committedContentVersion` PATCH 自体には事前ガードを付けない(新規アプリの
    `notPublished` → 初回コミットのケースをデッドロックさせないため)。事前ガードは
    (a) スキップパスの notes PATCH と (b) コンテンツアップロード前のフル PATCH
    (`IPlatformAppPublisher.UpdateAppAsync`)にのみ適用する。
  - **今後の注意**: `publishingState` 関連の Graph 400 に対して再度リトライを追加したくなった場合、
    「その PATCH 自身が待っている状態を発生させるものではないか」を必ず確認すること
    (今回の bug の再発防止)。

- **決定**: `MacOsMinimumOperatingSystemPayload.V14_0` / `V15_0`(および新規追加した `V26_0`)を
  `bool` から `bool?` に変更し、v1.0(`AppType: lob`)向けの場合は `null` のままにしてリクエストボディ
  から省略する。
  - **理由**: Graph v1.0 の `macOSMinimumOperatingSystem` にはこれらのプロパティ自体が存在しない
    (beta のみに存在)。非 open type のプロパティを送ると Graph は 400 を返すため、
    `Requirements.MinimumOSVersion` の値に関わらず `AppType: lob` の create/update が常に失敗していた。
  - **影響**: `IPlatformAppPublisher.UpdateAppAsync` のシグネチャに `ContentUploadOptions options` を
    追加し(上記の publishingState ガードに必要なため)、`PublishOrchestrator` から渡すように変更した。

- **決定**: `GraphMobileAppContentClient` の OData 型キャストセグメント(URL パス要素)を
  `Uri.EscapeDataString` でエンコードするのではなく、既知 3 種
  (`microsoft.graph.win32LobApp` / `macOSPkgApp` / `macOSLobApp`)への許可リストで検証する方式に変更した。
  - **理由**: 型キャストセグメントは URL のルート要素であり、データ値ではない。パーセントエンコードは
    現状の入力に対しては no-op だが、将来未知の値が渡された場合にサイレントにルートを壊す
    (Graph が 400/404 を返す)よりも、この時点で明示的に失敗させる方が診断しやすい。

- **決定**: `relaypublisher` パッケージの配布先を `nuget.org` 単独から、GitHub Packages(このリポジトリ)/
  Azure Artifacts / nuget.org の 3 feed に拡張する。
  - **理由**: 到達できる feed が利用者ごとに異なる。一般利用者は nuget.org、このリポジトリを直接使う利用者は
    GitHub Packages、社内 CI や閉じたネットワークは Azure Artifacts が現実的な経路になる。
  - **影響**: `doc/issues/issue-019` の「nuget.org へのリリース運用」というスコープを 3 feed に更新した。
    Azure Artifacts の feed URL は実 URL を書けない(AGENTS.md 禁止事項)ため secret
    `AZURE_ARTIFACTS_FEED_URL` から渡す。認証は PAT ではなく OIDC (workload identity federation) +
    artifacts-credprovider を使う。
  - **今後の注意**: 3 feed とも `--skip-duplicate` を付ける。片方だけ push 済みの状態から再実行しても
    冪等に完了させるため。

- **決定**: NuGet feed への push の trigger を `push: tags` から `release: published` に変更し、
  release workflow を `release-draft.yml` と `release-publish.yml` の 2 本に分割する。
  - **理由**: NuGet feed は一度 push した version を削除できない(unlist しかできない)。
    「tag を打った瞬間に公開が確定する」構成だと、誤った tag からの publish を取り消せない。
    draft release を人がレビューして publish する操作を最後の関門に置くことで、tag の打ち直しは
    draft release を消すだけでやり直せるようにする。
  - **影響**: `release-publish.yml` は再ビルドせず `gh release download` で release に添付された `.nupkg` を
    そのまま push する。レビューした bits と publish する bits を一致させるため。
    publishing secrets は repository ではなく `release` environment にスコープする。
  - **今後の注意**: `release-draft.yml` は tag が main から到達可能であることを
    `git merge-base --is-ancestor` で検証する。main 以外の履歴から release を作らせないため。

- **決定**: このリポジトリ自身の CI/CD workflow を `workflows/github-actions/`(参照サンプル)から
  `.github/workflows/`(実 workflow)に移す。`workflows/github-actions/ci.yml` と
  `release-nuget-tool.yml` は削除する。
  - **理由**: リポジトリを public 化するため、自身の CI を実際に動かす必要がある。この 2 つは
    Relaypublisher 自身のビルド/リリースであって利用者向けサンプルではないので、実 workflow 化すると
    重複する。`workflows/github-actions/publish-intune-apps.yml` と `workflows/azure-pipelines/` は
    利用者向けサンプルなので残す。
  - **影響**: public 化後は fork からの PR が走るため、`ci.yml` は secrets を一切参照しない設計にした
    (`pull_request_target` も使わない)。`doc/00-overview.md` のリポジトリ構成図と
    `doc/05-operation.md` §6 の checklist を、利用者向けと Relaypublisher 自身向けに分けて記述し直した。

## 2026-08-25: win32LobApp payload に `setupFilePath` / `fileName` を追加

- **決定**: `Win32LobAppPayloadMapper` が Graph へ送る `win32LobApp` payload に `setupFilePath`
  (manifest `Package.IntuneWin.SetupFile`、バックスラッシュ区切りに正規化)と `fileName`
  (`.intunewin` ファイル名。`IntuneWinPackager` と同じ命名規則を共有ヘルパーに切り出して使う)を追加する。
  - **理由**: production への publish が `POST /v1.0/deviceAppManagement/mobileApps` で
    `400 The Win32LobApp must have a valid value for the SetupFilePath property.` により失敗した。
    `doc/issues/issue-003-intune-graph-win32.md` の "Create / update mapping" 節がそもそも
    `setupFilePath` に触れておらず、実装(`Win32LobAppPayload.cs` / `Win32LobAppPayloadMapper.cs`)にも
    該当プロパティが存在しなかった。`fileName`(`mobileLobApp` 継承の必須プロパティ)も同様に欠落して
    いたため、`setupFilePath` を直しても次の 400 で再度失敗する可能性があり、同時に追加した。
  - **影響**: `400` は create の最初の書き込みで発生していたため、テナント側に不完全なアプリは残って
    いない。既存の app は存在しないので update 側の後方互換は考慮不要。
  - **今後の注意**: `win32LobApp` の必須プロパティを追加・変更する場合は、必ず Microsoft Learn の
    [win32LobApp resource type](https://learn.microsoft.com/graph/api/resources/intune-apps-win32lobapp?view=graph-rest-1.0)
    で必須/オプションを裏取りしてから `issue-003` を更新し、その後に実装すること(今回の bug の再発防止)。
