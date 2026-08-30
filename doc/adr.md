# ADR(Architecture Decision Record)

`doc/task.md`・`doc/plan.md` に記載のない仕様変更を、日付・該当 task・変更理由とともに箇条書きで記録します。仕様を変更する必要がある場合は、必ずこのファイルを確認し、変更理由が以前の修正と矛盾しないか確認してください。矛盾する可能性がある場合はユーザーに承認を求めます。

このファイルが 200 行を超えた場合は phase 単位で分割します。

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
