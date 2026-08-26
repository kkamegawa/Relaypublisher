# ADR(Architecture Decision Record)

`doc/task.md`・`doc/plan.md` に記載のない仕様変更を、日付・該当 task・変更理由とともに箇条書きで記録します。仕様を変更する必要がある場合は、必ずこのファイルを確認し、変更理由が以前の修正と矛盾しないか確認してください。矛盾する可能性がある場合はユーザーに承認を求めます。

このファイルが 200 行を超えた場合は phase 単位で分割します。

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

## 2026-08-26: macOS PKG の primary bundle 選定を「除外リスト」ではなく「明示選択 + 非列挙ガイダンス」にした理由(doc/task.md 同日エントリ参照)

- **決定**: 複数 bundle を同梱する macOS PKG(GitHub #112、Global Secure Access + Microsoft AutoUpdate が
  典型例)への対応を、`Detection.PrimaryBundleId` による明示選択(§6.21)と、「同梱 updater は
  `IncludedApps` に書かないことで除外する」という運用ガイダンスの組み合わせにした。`ExcludeBundleIds` の
  ような除外リストフィールドや、`com.microsoft.autoupdate*` を判定する組み込みの既知 updater リストは
  採らなかった。
  - **理由**: `IncludedApps` は手書きの宣言的リストであり、暗黙のフィルタを追加すると「manifest に書いた
    内容がそのまま Graph に送られる」という既存の設計原則(§6.7 の決定的 input hash、Categories の
    宣言的同期など)と矛盾する。除外したい bundle は最初から書かなければよく、追加の仕組みは不要。
    既知 updater リストは将来のバリエーション(他社製ソフトの updater 等)を網羅できずすぐに陳腐化する。
  - **今後の注意**: 将来 pkg introspection(xar TOC 検査)を実装した際、検査結果から「未知の bundle」を
    自動的に `IncludedApps` へ追記・除外するような自動化は行わないこと。あくまで警告 + 人間の確認
    (`--force` で上書き可)に留め、manifest の内容を上書きしない、という本決定の前提を維持する。

## 2026-08-26: #112 primary bundle 機能の実装契約と stacked PR の境界

- **決定**: `validate` は manifest schema と静的な repository 制約だけを検証し、source の download や
  PKG 内容の検査は行わない。`package` は source byte 列の SHA-256 を検証してから XAR を検査し、検出した
  bundle ID/version、selected primary、manifest identity、source SHA、CLI version を artifact report に保存する。
- **決定**: `publish` は package report を信頼するだけにせず、staging 済み macOS `.pkg` を再 hash・再検査し、
  manifest、metadata、report、CLI version の整合性を確認する。選択された全 entry の preflight が終わるまで
  Graph write を開始しない。warning 拒否、hard error、stale/tampered artifact のいずれも batch の Graph write
  を 0 件にする。
- **決定**: semantic warning は TTY では `[y/N]` の確認、非対話環境では `--force` が無い限り fail とする。
  `--force` は未列挙 bundle、package に存在しない declared primary、primary 未指定時の複数 bundleなどの
  semantic difference だけを確認する。曖昧な primary、破損/XAR parse error、未対応 archive、SHA mismatch、
  metadata/report 不整合、tenant/Graph safety error は hard error とし、`--force` で回避できない。
- **決定**: `AppType: lob` は `BundleVersion` を Graph `buildNumber`、`BundleBuildVersion` を Graph `versionNumber`
  に対応させ、selected primary を top-level bundle field と `childApps[0]` に反映する。`pkg`/`lob` の Graph
  payload を read-back する protected manual E2E と、managed macOS device の detection 確認を受入条件とする。

### Stacked implementation PRs

1. **Layer 1 - manifest contract and payload mapping**: nullable `PrimaryBundleId`、LOB の
   `BundleBuildVersion`、static validation、canonical hash compatibility、primary selection/reordering、pkg/lob
   mapping と unit tests。
2. **Layer 2 - package inspection and operator acknowledgement**: secure XAR/XML inspector、source SHA-first
   package flow、inspection report、semantic warning/hard-error classification、TTY `[y/N]`、non-TTY failure、
   `--force`、deterministic XAR fixture と CLI integration tests。
3. **Layer 3 - publish preflight and operational verification**: artifact rehash/reinspection、all-entry preflight
   before Graph writes、stale/tamper detection、exact CLI pinning in CI, protected manual E2E, Graph read-back,
   device detection, idempotency, primary change, rejected-preflight no-write, and cleanup.

各 layer は前段の branch/PR を親とする stacked PR とし、後段は前段が提供する report/schema 契約を変更せずに
利用する。実装完了条件は deterministic test、CI build/test、protected manual E2E の全てを満たすこととする。
