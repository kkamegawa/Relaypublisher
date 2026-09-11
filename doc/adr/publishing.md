# ADR: publish と Graph

Intune / Microsoft Graph への publish、content upload、payload mapping に関する設計判断を記録します。

`doc/task.md`・`doc/plan.md` に記載のない仕様変更を、日付・該当 task・変更理由とともに箇条書きで記録します。
仕様を変更する必要がある場合は、必ず該当領域のファイルを確認し、変更理由が以前の修正と矛盾しないか確認してください。
矛盾する可能性がある場合はユーザーに承認を求めます。

## 2026-09-10: Intune app 関連の Graph 呼び出しを beta に統一する (Issue #161)

- **決定**: Windows `win32LobApp` の Graph 呼び出し(create/update/content upload/notes・
  committedContentVersion patch)を、macOS `AppType: pkg`(`macOSPkgApp`)と同じく Graph **beta**
  経由にする。`CategoryApiVersion.UseBeta` も Windows で `true` を返すようにする。
  - **理由**: 既存の Windows app 2 件(x64/arm64)への再 publish が、content upload 成功後の
    `win32LobApp` 全体 PATCH で `400 NoPropertyForSelectedVersion` により失敗した。Microsoft Learn で
    v1.0 / beta の `win32LobApp` resource type を照合した結果、`Win32LobAppPayloadMapper` が常に送る
    `displayVersion`、および manifest 指定時に送る `roleScopeTagIds` は **どちらも v1.0 の win32LobApp
    には存在せず、beta のみに存在する**ことを確認した。つまり `RoleScopeTagIds` 無しの manifest でも
    `displayVersion` により、既存 Windows app の更新は v1.0 のままでは常に失敗する構造的なバグだった。
  - **影響範囲**: content upload・notes / committedContentVersion patch・publishingState 待機も、
    アプリ本体と同じ API バージョンで呼ぶ必要があるため、`WindowsAppPublisher` の `useBeta` 引数も
    合わせて `true` にした。失敗した既存 app は content が既に commit 済みのため、削除・再作成は不要
    (doc/06-troubleshooting.md 6e 節)。
  - **今後の注意**: macOS `AppType: lob`(`macOSLobApp`)にも同種の `roleScopeTagIds` 未対応、および
    v1.0 の `macOSMinimumOperatingSystem` が macOS 14 以降のフラグを持たないという既知の制限があり、
    別 Issue (#163) で同様に beta へ統一する予定。将来的に Intune app 関連の Graph 呼び出しがすべて
    beta に揃った時点で、`useBeta` 引数と `/v1.0/` ⇔ `/beta/` の per-call 切り替え機構自体を削除する
    (Issue #164)。この決定は 2026-08-21 の「v1.0 では v14_0/v15_0 を省略する」エントリ、および
    2026-08-25 の win32LobApp v1.0 Learn リンクを置き換えるものであり、「対象 API に存在しない
    プロパティを送らない」という同じ原則に基づくため矛盾しない。
- **決定**(Issue #163): `MacOsAppPayloadMapper.ResolveTarget` を変更し、`AppType: lob`
  (`macOSLobApp`)も `AppType: pkg` と同じく Graph beta を使うようにする。`MacOsMinimumOperatingSystemTable`
  からは `IsBetaOnly` 判定と `useBeta` 引数を削除し、`v14_0`/`v15_0`/`v26_0` を含む全バージョンを
  `AppType` に関わらず使用できるようにする。`UnsupportedMacOsVersionException` から
  `requiresBetaOnlyFlag` 分岐を削除する。
  - **理由**: 上記 win32LobApp と同じ調査で、`macOSLobApp` にも `roleScopeTagIds` が v1.0 に存在しない
    という同種のバグが確認できた。あわせて v1.0 の `macOSMinimumOperatingSystem` が macOS 14 以降の
    フラグを持たないため、`AppType: lob` は `Requirements.MinimumOSVersion` に macOS 14 以降を指定
    できないという既知の制限も、同じ「lob は beta を使わない」という前提から生じていた。lob を beta に
    揃えることで両方を同時に解消する。
  - **影響範囲**: `tools/yamlcreate.ps1` の `$MacOsVersions` から beta 専用フラグと `AppType: lob` での
    除外処理を削除し、対話式スクリプトでも `lob` から macOS 14 以降を選べるようにした。既存の macOS 13
    以前を指定した `lob` manifest の挙動(YAML・publish の入出力)は変わらない。
  - **今後の注意**: これでアプリ本体・content upload・category の Graph 呼び出し(win32LobApp・
    macOSPkgApp・macOSLobApp・`CategoryApiVersion`)はすべて beta に揃った。filter なしの
    assignment create/update/delete(`AssignmentGraphClient`)は本 Issue の対象外のためまだ v1.0 の
    ままで、Issue #164 でこれも含めて `useBeta` 引数と `/v1.0/` ⇔ `/beta/` の per-call 切り替え機構
    (`GraphMacOsAppClient` / `GraphWin32LobAppClient` / `GraphMobileAppContentClient` /
    `CategoryGraphClient` / `AssignmentGraphClient` / `GraphIntuneAppDirectory` の `VersionSegment` 等)
    自体を削除し、`GraphClientOptions.BaseAddress` を beta 既定にする。

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

## 2026-08-21: macOS PKG アップロード HTTP 400 の根本修正(doc/task.md 同日エントリ参照)

同日の CI / リリース配布に関する決定は [ci-release.md](ci-release.md) に記録しています。

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
