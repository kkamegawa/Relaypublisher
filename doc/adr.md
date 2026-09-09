# ADR(Architecture Decision Record)

`doc/task.md`・`doc/plan.md` に記載のない仕様変更を、日付・該当 task・変更理由とともに箇条書きで記録します。仕様を変更する必要がある場合は、必ずこのファイルを確認し、変更理由が以前の修正と矛盾しないか確認してください。矛盾する可能性がある場合はユーザーに承認を求めます。

エントリは領域ごとのファイルに記録し、このファイルは索引として全エントリを日付降順で一覧します。1 ファイルが 200 行を超えた場合は、その領域をさらに分割します。

| ファイル | 対象領域 |
|---|---|
| [adr/publishing.md](adr/publishing.md) | Intune / Microsoft Graph への publish、content upload、payload mapping |
| [adr/manifest-tooling.md](adr/manifest-tooling.md) | YAML manifest の schema と検証、`tools/yamlcreate.ps1` |
| [adr/ci-release.md](adr/ci-release.md) | このリポジトリ自身の CI/CD workflow、配布 feed とリリース手順 |

新しいエントリは、該当する領域のファイルの先頭(日付降順)に追加し、この索引にも 1 行追加してください。どの領域にも当てはまらない場合は、既存ファイルに押し込まずに新しい領域ファイルを作り、上の表に行を足します。

## エントリ一覧(日付降順)

| 日付 | エントリ | 領域 |
|---|---|---|
| 2026-09-06 | [manifest 作成スクリプトの Windows file detection 対応 (Issue #140)](adr/manifest-tooling.md#2026-09-06-manifest-作成スクリプトの-windows-file-detection-対応-issue-140) | manifest-tooling |
| 2026-09-06 | [publish の SAS 認証 403 回復・result file 一本化・manifest エントリ単位の Graph セッション (Issue #150)](adr/publishing.md#2026-09-06-publish-の-sas-認証-403-回復・result-file-一本化・manifest-エントリ単位の-graph-セッション-issue-150) | publishing |
| 2026-09-05 | [Windows file-system detection (Issue #141)](adr/manifest-tooling.md#2026-09-05-windows-file-system-detection-issue-141) | manifest-tooling |
| 2026-09-02 | [manifest 作成スクリプトのソース契約 (Issue #140)](adr/manifest-tooling.md#2026-09-02-manifest-作成スクリプトのソース契約-issue-140) | manifest-tooling |
| 2026-08-30 | [nuget.org Trusted Publishing (OIDC) への移行 (Issue #131)](adr/ci-release.md#2026-08-30-nugetorg-trusted-publishing-oidc-への移行-issue-131) | ci-release |
| 2026-08-25 | [win32LobApp payload に `setupFilePath` / `fileName` を追加](adr/publishing.md#2026-08-25-win32lobapp-payload-に-setupfilepath--filename-を追加) | publishing |
| 2026-08-21 | [macOS PKG アップロード HTTP 400 の根本修正](adr/publishing.md#2026-08-21-macos-pkg-アップロード-http-400-の根本修正doctaskmd-同日エントリ参照) | publishing |
| 2026-08-21 | [配布 feed とリリース workflow の再構成](adr/ci-release.md#2026-08-21-配布-feed-とリリース-workflow-の再構成doctaskmd-同日エントリ参照) | ci-release |

2026-08-21 は 1 回の作業で publish 側と CI / 配布側の両方の決定を行ったため、領域ごとに 2 エントリに分けています。
