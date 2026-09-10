# 作業記録

このファイルは、作業終了時にセッションごとの作業内容を記録するログです。各エントリは実施した plan と、参照した issue / Work Item へのリンクを含みます。

## 2026-09-06: manifest 作成スクリプトを Windows file detection に追従させる

**ブランチ**: `feature/yamlcreate-manifest-tool`

**対応 Issue / PR**: [#140](https://github.com/kkamegawa/Relaypublisher/issues/140) / [#139](https://github.com/kkamegawa/Relaypublisher/pull/139)(仕様変更元: [#141](https://github.com/kkamegawa/Relaypublisher/issues/141))

### 実施内容

PR #139 は `Detection` が script 固定だった頃の schema に対して書かれており、Issue #141 で入った
`Detection.Type` の discriminator に追従していなかった。決定事項は [adr/manifest-tooling.md](adr/manifest-tooling.md) の同日エントリに記録した。

1. `origin/main` を merge した(rebase・force push はしない)。衝突は `doc/adr.md` と `doc/task.md` の 2 ファイルのみで、
   どちらもヘッダー直下への新 section 追加だったため、両者を日付降順で残して解決した。
2. `tools/yamlcreate.ps1` に `$DetectionTypes` / `$FileSystemOperationTypes` / `$FileSystemOperators` と、
   `ManifestValues` 由来の `$FileSystemVersionPattern` / `$TargetDevicePathRootPattern` /
   `$TargetDeviceInvalidChars` を追加した。`notConfigured` は Graph の unset sentinel なので選択肢に含めない。
3. `Test-TargetDeviceText` / `Test-TargetDevicePath` / `Test-TargetDeviceLeafName` と、再入力ループ付きの
   `Read-TargetDevicePath` / `Read-TargetDeviceLeafName` / `Read-FileSystemVersion` を追加した。C# の
   `HasInvalidFileSystemText` は invalid で true を返すため、本スクリプトの `Test-*` 規約に合わせて反転してある。
4. `ConvertTo-YamlScalar` / `Add-YamlPair` に `-SingleQuote` を追加し、`Path` をシングルクォートで出力するようにした。
5. Windows Detection の対話を `Type` の選択から始め、`script` は従来どおり、`file` は
   `Path` / `FileOrFolderName` / `OperationType`(+ `version` のときだけ `Operator` / `ComparisonValue`)/
   `Check32BitOn64System` を出力するようにした。「script 検出のみ対応」の注記は削除した。
6. `$VersionBearingKeys` に `ComparisonValue` を追加し、comment-based help の更新対象フィールドを直した。
7. [08-yamlcreate.md](08-yamlcreate.md) / [08-yamlcreate_ja.md](08-yamlcreate_ja.md) の §1 / §4.3 / §8.1 / §8.2 / §9 / §10 を
   日英同時に更新した(283 行で一致、見出し位置も一致)。
8. 回帰テストを 11 → 16 ケースに増やした。あわせて、応答されない必須プロンプトが無限ループでスイートを止めず
   ケースの失敗になるよう、Windows 用レスポンダに同一プロンプトの反復ガードを入れた。
9. 上記の追記で `doc/adr.md` が 231 行になり、ヘッダーが定める 200 行の分割閾値を越えたため、ADR を領域別に分割した。
   `doc/02-dotnet-architecture.md` の Phase 1〜10 は初期実装のフェーズで、いずれも完了済みかつ現行の ADR
   (保守判断)に対応しないため、「phase 単位」ではなく領域単位に分けている。
   [adr/publishing.md](adr/publishing.md)(publish / Graph)、
   [adr/manifest-tooling.md](adr/manifest-tooling.md)(manifest schema / `tools/yamlcreate.ps1`)、
   [adr/ci-release.md](adr/ci-release.md)(CI / 配布 feed)の 3 ファイルに分け、`doc/adr.md` は全エントリを
   日付降順で並べた索引として残した。2026-08-21 のエントリは 1 回の作業で publish 側と CI 側の両方を決めていたため、
   領域ごとに 2 エントリへ分け、互いの所在を本文に明記した。`doc/06-troubleshooting.md` / `_ja`、
   `doc/issues/issue-019` / `issue-150` の参照先も新しいファイルに更新した(過去の作業記録の記述は履歴なので変更しない)。

### 検証結果

- `pwsh -NoProfile -File tests/Tools/YamlCreate.Tests.ps1`: 16 ケース成功。
- `YAMLCREATE_TEST_TOOL_PATH` で変更前の `tools/yamlcreate.ps1` を指した実行では、追加した 5 ケースのうち 4 ケースが失敗する
  ことを確認した。残る 1 ケース(`New Windows script detection is unchanged by the Type discriminator`)は変更していない
  経路の回帰ガードなので、変更前後どちらでも成功するのが正しい。
- `dotnet build IntuneLobPublisher.slnx --configuration Release` / `dotnet test ... --no-build`: 後述の実行結果を参照。
- `git diff --check`: 成功。

Intune への実 publish は未実施。

## 2026-09-06: Fail publish when result-file output fails (Issue #150 review follow-up)

- Issue: [#150](https://github.com/kkamegawa/Relaypublisher/issues/150)
- Pull request: [#151](https://github.com/kkamegawa/Relaypublisher/pull/151)
- Plan: restore the nonzero exit status for result-file output failures while preserving
  existing publish failures and caller cancellation, then verify and push the focused fix.
- `PublishEntriesAsync` now tracks result-file write failures independently from per-entry
  publish failures. A successful batch cannot return success when its requested result file
  could not be saved. Existing abort and cancellation behavior is preserved.
- Added four regression tests in `PublishCommandBatchAbortTests`, covering output failures
  after success, batch abort, per-app failure, and caller cancellation. Existing-directory
  output targets make the failures deterministic without relying on OS permission changes.
- Validation:
  - `dotnet test tests/IntuneLobPublisher.Core.Tests/IntuneLobPublisher.Core.Tests.csproj -c Release --filter FullyQualifiedName~PublishCommandBatchAbortTests`: 15 passed, 0 failed, 0 skipped.
  - `dotnet build IntuneLobPublisher.slnx -c Release --no-restore --no-incremental`: 0 warnings, 0 errors.
  - `dotnet test IntuneLobPublisher.slnx -c Release --no-build --no-restore`: 723 passed, 0 failed, 38 skipped.
  - `git diff --check`: passed.
- The initial sandboxed test invocation was blocked by MSBuild IPC permissions; the
  successful validation above ran with the required local process permissions.

## 2026-09-06: publish の SAS 認証 403 回復・result file 一本化・manifest エントリ単位の Graph セッション (Issue #150)

**ブランチ**: `fix/150-sas-activation-retry-and-per-entry-session`

**対応 Issue / PR**:

- Issue: [#150](https://github.com/kkamegawa/Relaypublisher/issues/150)
- Pull request: [#151](https://github.com/kkamegawa/Relaypublisher/pull/151)

### 実施内容

1. 本番の Azure Pipelines で、複数 manifest を含む `manifest-list.json` の `publish` が 2 件目のパッケージで
   `Azure.RequestFailedException`(403 `AuthenticationFailed` /
   `AuthenticationErrorDetail: SAS identifier cannot be found for specified signed identifier`)により
   落ちる事象の報告を受け、実際のログを基に調査した。原因は未確定(stored access policy の伝播遅延が
   最有力仮説)だが、必要な対処は仮説によらず同じであることを確認し、実装を進めた。
2. `AzureStorageBlockBlobUploader` に SAS 認証 403 の回復処理を追加した。SAS の残り有効期限で経路を分岐し、
   期限に余裕があれば同一 SAS で document 化された伝播時間を上回るまで再送し、それでも回復しなければ
   `renewUpload` で SAS を取り直したうえで再度待機する。renewal 回数は 403 回復専用に少数へ制限し、
   予防的 renewal とはカウンタを共有しない。回復全体は 1 回の stage/commit 呼び出しごとに 1 つの
   deadline で区切り、呼び出し元の `CancellationToken` にリンクした専用の `CancellationTokenSource` で
   実際にキャンセルし、呼び出し元のキャンセルとは区別する。
3. 再送のたびにブロック本文を同じバイト列から作り直すようにし(既存の `MemoryStream` 再利用による
   空/欠損送信のリスクを修正)、回復できない場合は新規 `ContentUploadRejectedException` に変換した
   (`Status`/`ErrorCode`/`AuthenticationErrorDetail`/`x-ms-request-id` のみを保持し、元の例外・SAS を
   含む情報は一切保持しない)。
4. `MobileAppContentUploadOrchestrator.IsRecoverableUncommittedUploadState` に
   `azureStorageUriRequestSuccess` / `azureStorageUriRenewalSuccess` を追加し、blob 送信だけが中断した
   未 commit file も既存の file 数・metadata 一致条件のまま再送対象にした。
5. `PublishCommand.PublishEntriesAsync` の result file 出力を単一の exit point(`finally`、
   `CancellationToken.None`)に統合し、想定外の例外もエントリを記録してから中断するようにした。
6. ユーザーの指示により、`publish` の Graph セッション(`HttpClient`・認証・トークンキャッシュ)を
   manifest エントリごとに新規作成・破棄する構造に変更した(`IPublishSession` / `PublishComposition`)。
   資格情報(`DefaultAzureCredential`)自体は実行全体で共有する。これは 403 の対策ではなく、指示された
   構造変更であることを設計判断として明記した。
7. `doc/00-overview.md`(6.10 / 6.12 / 6.16)、`doc/02-dotnet-architecture.md`、
   `doc/06-troubleshooting.md` / `_ja`、`doc/05-operation.md` / `_ja`、`doc/adr.md` を更新し、
   `doc/issues/issue-150-sas-activation-retry-and-per-entry-session.md` を追加した。

### 検証結果

```
dotnet build IntuneLobPublisher.slnx
→ ビルドに成功しました。0 エラー。

dotnet test tests/IntuneLobPublisher.Core.Tests/IntuneLobPublisher.Core.Tests.csproj --no-build
→ 成功! 失敗: 0、合格: 719、スキップ: 38(環境依存でスキップされる既存テスト。今回の変更とは無関係)、合計: 757
```

追加した主なテスト: 同一 SAS での再送成功、再送本文のバイト単位一致、同一 SAS window 超過後の
renewal、renewal 上限超過時のライブロック回避、期限接近/期限切れ時の即時 renewal、待機中に期限を
跨ぐ場合の切り替え、非認証エラー(404)の非リトライ、commit 側での 403 リトライ、呼び出し元
キャンセルと回復 deadline の区別、例外メッセージ・`ToString()`・logger 出力への SAS 非漏洩、
3 件バッチ(成功→失敗→成功)での result file と終了コード、中断状態からの復旧。

### 保留事項

- PR [#151](https://github.com/kkamegawa/Relaypublisher/pull/151) は Ready for review。CI の結果は
  マージ前に確認すること。
- Wiki(`plan/2026-09-06/`)への計画・Intune 知見の登録は別途実施する。
- 実機(Intune テナントへの実 publish)での検証は未実施。今回落ちた
  `Microsoft.GlobalSecureAccess` windows-arm64 / windows-x64 の再実行による確認が必要。手順は
  `doc/06-troubleshooting.md` §6d および `doc/issues/issue-150-sas-activation-retry-and-per-entry-session.md`
  の Verification 節を参照。
- 診断ログを伴う実機での次回実行結果をもって、`doc/adr/publishing.md` の「原因未確定」を確定情報に更新すること。

## 2026-09-05: Windows file-system detection (Issue #141)

**ブランチ**: `feature/141-windows-file-detection`

**対応 Issue / PR**:

- 親 Issue: [#141](https://github.com/kkamegawa/Relaypublisher/issues/141)
- Manifest / validation: [#142](https://github.com/kkamegawa/Relaypublisher/issues/142)
- Microsoft Graph mapping: [#143](https://github.com/kkamegawa/Relaypublisher/issues/143)
- Documentation / release: [#144](https://github.com/kkamegawa/Relaypublisher/issues/144)
- Pull request: [#145](https://github.com/kkamegawa/Relaypublisher/pull/145)

### 実施内容

1. Windows の `Detection.Type: file` を追加し、`exists` と `version` の validation、target-device path / leaf name
   validation、script/file fields の相互排他、macOS の file fields 拒否を実装した。
2. Graph v1.0 `win32LobAppFileSystemRule` を追加した。rules collection は System.Text.Json の polymorphic contract に
   変更し、PowerShell rule は discriminator と衝突しない `Win32LobAppPowerShellScriptRulePayload` とした。
3. Windows publisher は `Type: file` の場合に detection script を read せず、preflight / create / update のすべてで
   file rule を mapping する。script detection の repository-relative path と欠落時の failure は維持した。
4. existing script manifest hash の固定値、file criteria による hash 変化、validation、YAML load、payload JSON、
   publisher、staging の regression test を追加した。
5. 正本、日英の operation / troubleshooting / local E2E docs、README、sample catalog を更新し、file detection sample
   を追加した。

### 検証結果

- `dotnet test IntuneLobPublisher.slnx --configuration Release`: 733 passed、0 failed、0 skipped。
- `dotnet pack src\IntuneLobPublisher.Cli\IntuneLobPublisher.Cli.csproj --configuration Release
  -p:ContinuousIntegrationBuild=true -p:Version=1.1.0`: `relaypublisher.1.1.0.nupkg` を生成。
- `dotnet run ... validate --repo-root samples --manifest manifests\contoso-tool-windows-file-detection.yaml`:
  1 manifest が valid。
- `dotnet list IntuneLobPublisher.slnx package --vulnerable --include-transitive`: 脆弱な package なし。
- `git diff --check`: 成功。

### 保留事項

- PR #145 は Ready for review に更新済み。Ubuntu / Windows build-test、NuGet pack、3 RID の single-file
  publish、CodeQL、静的解析、NuGet submit が成功。
- #145 の merge 後、別途承認を得て `v1.1.0` tag、draft release、3 feed への publish を実施する。
- `intuneapps` の Global Secure Access manifest 更新、Azure Pipelines dry-run、本番 Intune publish は別 repository /
  別承認のままとする。

### 2026-09-05 追記: Issue #144 の release 準備を完了

**対応 Issue**: [#144](https://github.com/kkamegawa/Relaypublisher/issues/144)

#145 と、その後の CS8631 warning 修正 [#146](https://github.com/kkamegawa/Relaypublisher/pull/146) が main に merge
されたので、#144 の残作業である release 検証と draft release 生成を実施した。documentation / sample / ADR の作業は
#145 で完了済みのため、本追記では package と release の検証結果のみを記録する。

1. **CS8631 warning の解消**: `ManifestValidator` の `RuleFor(a => a.Detection)` / `RuleFor(a => a.Requirements)` は
   nullable property を返すため `IValidator<T?>` が要求され、`AbstractValidator<T>` の validator と型引数の
   nullability が一致しなかった。null 許容解除を validator instance から property 式へ移した(#146)。
   `!` は expression tree に現れないため、報告される property 名と `NotNull()` の runtime 検証は変わらない。
2. **`v1.1.0` tag の作成**: main の `33bed3ef` に annotated tag を付与し、既存の `release-draft.yml` を起動した。
3. **draft release の検証**: run 33953565705 が成功。draft `v1.1.0` は `targetCommitish: main`、`isDraft: true` で、
   `relaypublisher.1.1.0.nupkg`、win-x64 / win-arm64 / osx-arm64 の zip、`SHA256SUMS.txt` の 5 asset を持つ。

**検証結果**

```
dotnet build IntuneLobPublisher.slnx --configuration Release
  → 成功、warning 0

dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
  → 合格 735 / 失敗 0 / スキップ 0

dotnet pack src\IntuneLobPublisher.Cli\IntuneLobPublisher.Cli.csproj --configuration Release
  -p:ContinuousIntegrationBuild=true -p:Version=1.1.0 --output .\artifacts\nuget
  → relaypublisher.1.1.0.nupkg

dotnet list IntuneLobPublisher.slnx package --vulnerable --include-transitive
  → Cli / Core / Core.Tests のいずれにも脆弱な package なし
```

- nuspec: id `relaypublisher`、version `1.1.0`、MIT expression、README 同梱、`DotnetTool` packageType、
  repository commit `33bed3ef`。`tools/net10.0/any/` に CLI / Core assemblies と `DotnetToolSettings.xml` を含む。
- draft release から download した `.nupkg` の SHA-256 は `SHA256SUMS.txt` と一致
  (`8a5b9f1f5bedb9aa67bddbf16a0103219ce8cc02d887ac1a78409b63f8f15d0f`)。
- **release asset の package に file detection 実装が含まれることを確認**: download した `.nupkg` を tool-path に
  install し、`--version` が `1.1.0+33bed3ef...` を返すこと、`contoso-tool-windows-file-detection.yaml` が valid と
  判定されること、`Operator: notConfigured` に改変した manifest が exit code 1 で reject されることを確認した。
- sample manifest 4 件が valid。`apple-container-macos-arm64.yaml` は仕様どおり reference-only として reject される
  ため対象外。

**残る承認境界**

- draft release の publish は人によるレビュー gate であり、本作業では実施しない。publish により
  `release-publish.yml` が起動し、レビュー済みの同一 package が nuget.org / GitHub Packages / Azure Artifacts の
  3 feed へ push される。publish 後に 3 feed への到達と package の同一性を確認して #144 / #141 を close する。
- `intuneapps` の Global Secure Access manifest 更新、Azure Pipelines dry-run、本番 Intune publish は引き続き別
  repository / 別承認とする。

## 2026-09-02: manifest 作成スクリプトのレビュー修正

**ブランチ**: `feature/yamlcreate-manifest-tool`

**対応 Issue / PR**: [#140](https://github.com/kkamegawa/Relaypublisher/issues/140) / [#139](https://github.com/kkamegawa/Relaypublisher/pull/139)

### 実施内容

ユーザーが承認したレビュー指摘 8 件について、設計の整合、実装修正、回帰検証の順で対応した。

1. `.pkg` / `.exe` / `.tar.gz` などの拡張子直前の旧バージョンを更新し、より長いバージョンの一部分は置換しないようにした。
2. New のプレビュー、Update の差分・残存行で URL の認証情報・クエリ・フラグメントを除去し、保存する YAML の値は維持した。取得エラーにも生の応答を表示しない。
3. `azureBlob` の単一の認証選択肢を配列として保持し、StrictMode で停止しないようにした。
4. `publicHttp` の認証選択肢を既存 provider の契約に合わせ、`none` に限定した。
5. GitHub Release のハッシュ取得にアセット ID の REST API と `Accept: application/octet-stream` を使い、public / private 両方に対応した。
6. `Auth` と `Sha256` の順序によらずソースごとの認証情報を読み取り、複数ソース間で混在しないようにした。
7. CSV のグループ・フィルター選択で、エクスポーターの `GroupName` / `GroupId` と `FilterName` / `FilterId` を受け付けるようにした。
8. ヘッダーのみの CSV を候補 0 件として扱い、手入力へ戻れるようにした。

[08-yamlcreate.md](08-yamlcreate.md) と [adr.md](adr.md) を更新し、オフラインの PowerShell 回帰テストを追加した。
CI の Windows / Linux 両ジョブで実行する。サブエージェントによる本体差分とテストの独立レビューで追加指摘はなかった。

### 検証結果

- `pwsh -NoProfile -File tests/Tools/YamlCreate.Tests.ps1`: 9 ケース成功(この後の Copilot レビュー対応で 2 ケース追加し、最終的に 11 ケースになった。「2026-09-02 追記」参照)。修正前のスクリプトを一時ディレクトリに展開して実行した場合は 9 ケースとも失敗することも確認した。
- `dotnet build IntuneLobPublisher.slnx --configuration Release`: 成功、警告 0、エラー 0。
- `dotnet test IntuneLobPublisher.slnx --configuration Release --no-build`: 693 件成功、失敗 0、スキップ 0。
- CI YAML の構文確認、`git diff --check`: 成功。

private GitHub Release の実ダウンロードと Intune への実 publish は未実施。HTTP リクエストの URL・ヘッダー・ハッシュ計算はテスト用の応答で検証した。

### 2026-09-02 追記: Copilot レビュー対応

PR #139 の追加コメント 2 件を、同じ Issue #140 のレビュー修正として対応した。

- `Get-YamlScalarValue` がエスケープされた引用符で値を切り詰める問題を修正した。単一行の引用符付き値を切り出し、単一引用符の `''` と二重引用符の YAML エスケープを復元して Source / Auth へ渡す。
- `PackageVersion` の行全体を置換する問題を修正した。ソースのバージョン関連フィールドと `Sha256` も値の範囲だけを編集し、引用符・空白・行末コメントを保持する。コメント中の旧バージョンは残存警告の対象になる。
- [08-yamlcreate.md](08-yamlcreate.md) に既存のコメント保持仕様の具体的な動作を明記し、回帰テストを追加した。本体差分の独立レビューで追加指摘はなかった。

`dotnet build IntuneLobPublisher.slnx --configuration Release` は成功(既存の CS8631 警告 2 件、エラー 0)、
`dotnet test IntuneLobPublisher.slnx --configuration Release --no-build` は 693 件成功(失敗・スキップ 0)。
`pwsh -NoProfile -File tests/Tools/YamlCreate.Tests.ps1` は 11 ケース成功。追加した 2 ケースは今回の修正前には失敗することも確認した。

## 2026-08-30: NuGet.org Trusted Publishing (OIDC) への移行

**ブランチ**: `feature/131-nuget-trusted-publishing`

**対応 Issue / PR**:

- 親 Issue: [#131](https://github.com/kkamegawa/Relaypublisher/issues/131)
- Workflow sub-issue: [#132](https://github.com/kkamegawa/Relaypublisher/issues/132)
- Documentation sub-issue: [#133](https://github.com/kkamegawa/Relaypublisher/issues/133)
- Pull request: [#135](https://github.com/kkamegawa/Relaypublisher/pull/135)

### 実施内容

1. `release-publish.yml` の nuget.org 認証を、保存型の長期 `NUGET_API_KEY` から GitHub OIDC +
   `NuGet/login` v1.2.0 に変更した。Action は commit SHA
   `8d196754b4036150537f80ac539e15c2f1028841` に固定し、push 直前に取得する一時 API key を
   `NUGET_TEMP_API_KEY` として正確な package path の `dotnet nuget push` にだけ渡す。
2. GitHub の `release` Environment に `NUGET_USER` を追加した。Azure Artifacts 用の4 secrets、
   Environment protection rules、GitHub OIDC subject 設定は変更していない。保存型の
   `NUGET_API_KEY` secret は作成していない。
3. `doc/00-overview.md`、`doc/03-ci-github-actions.md`、`doc/05-operation.md` / `_ja.md`、
   `doc/adr.md` を Trusted Publishing の契約に更新した。NuGet policy の owner / repository の
   numeric ID、workflow file、environment を日英で受入値として記録した。
4. Trusted Publishing の公式対象外である Azure Pipelines の nuget.org release sample
   (`workflows/azure-pipelines/release-nuget-tool.yml`)と `doc/04-ci-azure-pipelines.md` の該当節を削除した。
   Intune publish 用 Azure Pipelines sample は維持した。
5. 親 Issue #131 と sub-issue #132 / #133 を作成し、最新 `origin/main` から独立した branch と
   1つの PR #135 にまとめた。明示承認後、日英 Wiki plan と Home / Relaypublisher index を push した。

### 検証結果

- すべての workflow / sample YAML の parse に成功。
- active workflow のすべての `uses:` が40桁 commit SHAに固定されていることを確認。
- `secrets.NUGET_API_KEY` が0件、Azure Pipelines nuget.org release sampleファイルが存在しないことを確認。
- `doc/05-operation.md` / `_ja.md` の対象節は、見出し2、checklist 10、表8行、merge後確認3項目で一致。
- `git diff --check` 成功。
- `dotnet build IntuneLobPublisher.slnx --configuration Release` 成功。既存の `CS8631` warning 2件、error 0件。
- `dotnet test IntuneLobPublisher.slnx --configuration Release --no-build` 成功。
  656 passed、Windows専用37 skipped、0 failed。
- PR #135 の Ubuntu / Windows build-test、NuGet pack、3 RID single-file app job がすべて成功。
- Wiki の英語・日本語ページ、Home / Relaypublisher index、相互言語リンクをログイン済みブラウザーで確認。

### Merge 後の保留事項

- 新しい version の draft release publish、3 feed の実確認、fresh OIDC交換と `--skip-duplicate` の
  冪等性確認は、merge後に別途明示承認を得て実施する。
- 長期 nuget.org API key が残っている場合の revoke は、実 publish 成功後に別途承認を得て実施する。

## 2026-08-24: GitHub Actions CI/CD の設計と実装 (public 化前提)

**ブランチ**: `feature/add-github-actions-ci`

**対応 Issue**: なし(リモートの GitHub MCP が未認証のため issue を起票できず)。設計正本としては
[issue-019](issues/issue-019-nuget-global-tool-distribution.md) のスコープを更新して対応した。

**背景**: このリポジトリは `.github/workflows/` に実 workflow を 1 本も持たず、CI らしきものは
`workflows/github-actions/` の「コピーして使う参照サンプル」だけだった。将来 public 化する前提で、
リポジトリ自身の CI/CD を実装する必要があった。

### 確定した要件(ユーザー回答済み)

| 項目 | 決定 |
|---|---|
| main への PR | build / test を実行し、NuGet package と single-file self-contained app を成果物として生成 |
| main 到達可能な `v*` tag push | draft release を作成し成果物を添付。release の publish は手動 |
| feed への push タイミング | draft release を人が publish した時点 (`release: published`) |
| publish 先 feed | GitHub Packages / Azure Artifacts / nuget.org の 3 つ |
| Azure Artifacts 認証 | OIDC (workload identity federation) + artifacts-credprovider。feed URL は secret |
| single-file RID | `win-x64` / `win-arm64` / `osx-arm64` |

### 実施内容(承認済み plan に基づく)

1. **workflow 3 本の作成**: `ci.yml`(PR/main の build・test・pack・single-file publish、secrets 不使用)、
   `release-draft.yml`(`v*` tag → main 到達性検証 → pack/publish → draft release 作成)、
   `release-publish.yml`(`release: published` → release 資産の `.nupkg` を 3 feed へ push)。
   YAML の構文と job 構造は検証済み。
   `uses:` は **すべて最新リリースの commit SHA でピン留め**した(`actions/checkout` v7.0.1 /
   `actions/setup-dotnet` v6.0.0 / `actions/upload-artifact` v7.0.1 / `azure/login` v3.0.1)。
   当初 plan では `actions/*` を major tag のままにしていたが、public リポジトリの supply chain 上
   tag は付け替え可能でありピン留めにならないため、ユーザー指摘を受けて全件 SHA 固定に変更した。
   あわせて `release-draft.yml` の `git fetch --no-tags --depth=0 origin main` を修正した
   (`--depth=0` は git が受け付けない。`fetch-depth: 0` で既に full clone のため `--depth` は不要)。
2. **参照サンプルの整理**: `workflows/github-actions/ci.yml` と `release-nuget-tool.yml` を削除した。
   Relaypublisher 自身のビルド/リリースであり、実 workflow 化すると重複するため。
   利用者向けの `publish-intune-apps.yml` と `workflows/azure-pipelines/` は残した。
3. **ドキュメント更新**: [03-ci-github-actions.md](03-ci-github-actions.md) の §11b / §12a を実 CI の設計に
   書き換え、冒頭に「Relaypublisher 自身の CI/CD」と「利用者向けサンプル」の区別表を追加。
   [00-overview.md](00-overview.md) のリポジトリ構成図を更新。
   [05-operation.md](05-operation.md) / [05-operation_ja.md](05-operation_ja.md) の §0 に 3 feed からの
   install 手順を、§6 に「Relaypublisher release pipeline」checklist を追加。
   [README.md](../README.md) / [README_ja.md](../README_ja.md) に workflow 節を追加。
   [issue-019](issues/issue-019-nuget-global-tool-distribution.md) のスコープを更新。
4. **設計判断の記録**: [adr.md](adr.md) に 3 feed 化・`release: published` gating・
   `.github/workflows/` への移動の 3 件を記録。

### 2026-08-24 追記: PR #102 のレビュー指摘対応

[PR #102](https://github.com/kkamegawa/Relaypublisher/pull/102) の Copilot レビューで挙がった
workflow のセキュリティ / release 整合性の指摘 7 件に対応した。

1. **publish 済み release への upload を禁止** (`release-draft.yml`): 既存 release があるとき
   `isDraft` を確認し、draft でなければ fail させる。publish 済み release に tag を打ち直して資産を
   差し替えても `release: published` は再発火しないため、release の添付物と feed に push 済みの
   package が食い違ったまま公開され続ける事故を防ぐ。
2. **`persist-credentials: false` を全 checkout に付与** (3 本すべて): 既定の `true` は job token を
   `.git/config` に書き込む。とくに `contents: write` を持つ `draft-release` job では、その後に走る
   `dotnet pack` / `dotnet publish`(tag 時点のビルドコードと NuGet 依存関係)が token を読み出せる。
3. **`git merge-base` を GitHub API 比較に置き換え**: `persist-credentials: true` を必要としないよう、
   main 到達性の検証を `gh api repos/{owner}/{repo}/compare/main...<sha>` の `status` で行う
   (`behind` / `identical` のみ通す)。あわせて read-only の `guard` job に切り出し、ビルドコードを
   実行しない状態で provenance を確定させてから write 権限を持つ job を動かす構成にした。
4. **`dotnet nuget push` のワイルドカードを廃止** (`release-publish.yml`): 3 feed とも
   `relaypublisher.<version>.nupkg` の実パスを指定する。`gh release download` も同様に
   `--pattern` を実ファイル名に固定した。release に別の `.nupkg` が添付されていた場合の巻き込み
   publish を防ぐ。
5. **`curl | sh` による credential provider install を廃止**: 可変リダイレクト(`aka.ms`)越しの
   スクリプトを publishing secrets と OIDC token を持つ job で実行しないため、署名済み NuGet package
   `Microsoft.Artifacts.CredentialProvider.NuGet.Tool` を `--version 2.0.4` 固定で
   `dotnet tool install` する方式に変更した。
6. **`release: published` の provenance ガードを追加**: この event は repository 全体で発火し、
   `release-draft.yml` が作った draft にも `v*` tag にも限定されない。read-only の `guard` job で
   (a) tag 形式、(b) tag commit の main 到達性、(c) 添付 `.nupkg` が `relaypublisher.<version>.nupkg`
   ちょうど 1 個であること、の 3 段を検証してから publish job を起動する。
7. **GitHub Packages の install 手順を修正** ([05-operation.md](05-operation.md) /
   [05-operation_ja.md](05-operation_ja.md)): `--add-source` は feed URL を渡すだけで認証しない。
   GitHub Packages は package が public でも匿名リクエストに 401 を返すため、
   `dotnet nuget add source --username --password --store-password-in-clear-text` で認証情報つきの
   source を先に登録する手順に書き換えた(bash / PowerShell 7 両方)。平文保存のリスクと
   source 削除方法も明記。Azure Artifacts 側も credential provider install と `--interactive` を
   含む実際に通る手順に修正した。

設計正本側は [03-ci-github-actions.md](03-ci-github-actions.md) §11a に
`persist-credentials: false` 必須・`curl | sh` 禁止・push のワイルドカード禁止を共通方針として追記し、
§12a の `release-draft.yml` / `release-publish.yml` の設計ポイントを上記に合わせて更新した。

**未検証**: `Microsoft.Artifacts.CredentialProvider.NuGet.Tool` を `dotnet tool install` した場合に
plugin discovery が期待どおり働き、`VSS_NUGET_ACCESSTOKEN` / `VSS_NUGET_URI_PREFIXES` を読むかは
実環境で未確認。Microsoft Learn の推奨手順ではあるが、初回の実リリースで確認が必要。

### 検証結果

```
dotnet pack src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj -c Release -p:Version=0.0.0-ci.1
→ relaypublisher.0.0.0-ci.1.nupkg を生成。release-publish.yml の version 整合チェックが期待する
  ファイル名 `relaypublisher.<version>.nupkg` と一致することを確認。

dotnet publish -r {win-x64|win-arm64|osx-arm64} --self-contained true -p:PublishSingleFile=true
→ 3 RID とも単一実行ファイルを生成(80–89 MB)。
  artifacts/single-file/win-x64/relaypublisher.exe --help の起動を確認。

dotnet build IntuneLobPublisher.slnx -c Release  (WSL / Ubuntu / .NET SDK 10.0.111)
→ ビルドに成功しました。0 エラー、2 警告(既存の CS8631)。
```

### 未確定事項

- **Linux での `dotnet test` はローカル未検証。** WSL 上で VSTest の testhost が
  vstest.console に接続できず(`failed to connect to testhost process`、WSL の systemd user session 起動
  失敗が原因と思われる)、テスト実行そのものができなかった。ビルドは通っている。コード側は
  `PathSafety.IsSafeRelativePath` がドライブレター前置とセパレータを明示的に検査しており
  ([PathSafety.cs](../src/IntuneLobPublisher.Core/Staging/PathSafety.cs))、`IntuneWinPackagerTests` は
  `[OSCondition(OperatingSystems.Windows)]` で除外されているため、Linux 固有の失敗は想定していないが、
  **CI の ubuntu leg が実質的な初回検証**になる。最初の PR で赤くなったらそこで対処する。
- `release-publish.yml` は secrets 投入と Azure 側の事前セットアップ(managed identity /
  federated credential / Azure DevOps Contributors 追加)が済むまで実行できない。初回の実リリースが初検証。
- workflow ファイルの配置はユーザーが手動で行った(`.github/workflows/**` がエージェントの
  書き込み deny 対象のため)。

## 2026-08-21: macOS PKG アップロード HTTP 400 の根本修正

**ブランチ**: `fix/97-contentversions-compile-error`

**対応 Issue**: なし。当初 [#97](https://github.com/kkamegawa/Relaypublisher/issues/97)(`GraphMobileAppContentClient.ToGraphTypeSegment` の CS1503 コンパイルエラー)を対象として `/code-review` を実行したが、ユーザーから「issue97ではありませんでした。このブランチです。」と訂正があり、実際の対象は本ブランチが抱える macOS `.pkg` アップロード時の HTTP 400 問題(コンパイルエラーそのものではなく、その先にある Graph API アップロード仕様との不整合)であることが判明した。この作業に対応する GitHub issue は起票していない。

**背景**: 本ブランチは `GraphMobileAppContentClient` に OData 型キャストセグメント(`/microsoft.graph.macOSPkgApp/contentVersions` 等)を追加し、アプリ PATCH に「publishingState が published でない」400 に対するリトライを追加していたが、それでも macOS `.pkg` のアップロードが HTTP 400 で失敗する状態だった。Microsoft Learn および公式リファレンス実装(`microsoftgraph/powershell-intune-samples` の `LOB_Application/Application_LOB_Add.ps1`)で Graph API のアップロード仕様を確認し、コードレビューで根本原因を特定した上で修正した。詳細な設計判断は [adr.md](adr.md) を参照。

### 実施内容(承認済み plan に基づく)

1. **PKG 暗号化ペイロードのレイアウト修正**(根本原因): `PkgContentPreparer` がアップロードするバイト列を ciphertext のみから `[mac (32B)][iv (16B)][ciphertext]` に変更([PkgContentPreparer.cs](../src/IntuneLobPublisher.Core/Publishing/PkgContentPreparer.cs))。`.intunewin` の content entry と同一レイアウトであることを確認済み。
2. **publishingState リトライの是正**: `committedContentVersion` PATCH 失敗後に "published" を待つ自己矛盾したリトライ(新規アプリは "published" に到達できず必ずタイムアウトする)を撤去し、Graph 書き込みの**前**に "processing" が晴れるのを待つ `WaitWhilePublishingStateProcessingAsync` に置き換えた([MobileAppContentUploadOrchestrator.cs](../src/IntuneLobPublisher.Core/Publishing/MobileAppContentUploadOrchestrator.cs))。同じガードをコンテンツアップロード前のフル PATCH([WindowsAppPublisher.cs](../src/IntuneLobPublisher.Core/Publishing/WindowsAppPublisher.cs) / [MacOsAppPublisher.cs](../src/IntuneLobPublisher.Core/Publishing/MacOsAppPublisher.cs))にも適用。
3. **v1.0 に存在しないプロパティの送信停止**: `v14_0`/`v15_0` を `bool?` 化し、`AppType: lob`(v1.0)では省略するよう修正。`AppType: lob` の create/update が常に 400 になっていた別バグを修正([MacOsAppPayload.cs](../src/IntuneLobPublisher.Core/Publishing/MacOsAppPayload.cs))。あわせて beta 専用の `v26_0` マッピングを追加([MacOsMinimumOperatingSystemTable.cs](../src/IntuneLobPublisher.Core/Publishing/MacOsMinimumOperatingSystemTable.cs))。
4. **周辺の堅牢化**:
   - `AzureStorageBlockBlobUploader` の最終 `CommitBlockListAsync` 直前にも SAS 期限チェックを追加([AzureStorageBlockBlobUploader.cs](../src/IntuneLobPublisher.Core/Publishing/AzureStorageBlockBlobUploader.cs))。
   - OData 型キャストセグメントを `Uri.EscapeDataString` ではなく既知 3 種の許可リストで検証するよう変更([GraphMobileAppContentClient.cs](../src/IntuneLobPublisher.Core/Publishing/GraphMobileAppContentClient.cs))。
   - `GraphRequestException` に生の `error.message` を保持する `GraphErrorMessage` を追加([PublisherExceptions.cs](../src/IntuneLobPublisher.Core/Exceptions/PublisherExceptions.cs) / [GraphErrorReader.cs](../src/IntuneLobPublisher.Core/Publishing/GraphErrorReader.cs))。
5. **ドキュメント更新**: [00-overview.md](00-overview.md) §6.13 に暗号化レイアウトの仕様を追記、[06-troubleshooting.md](06-troubleshooting.md) / [06-troubleshooting_ja.md](06-troubleshooting_ja.md) の該当項目を実際の根本原因で更新。`samples/manifests/apple-container-macos-arm64.yaml` の古いコメント(v26_0 未対応)を修正。

### 検証結果

```
dotnet build IntuneLobPublisher.slnx --configuration Release
→ ビルドに成功しました。0 エラー。

dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
→ 成功! 失敗: 0、合格: 565、スキップ: 0、合計: 565
```

実機(Intune テナントへの実 publish)での検証は未実施。次回のローカル E2E([07-local-e2e.md](07-local-e2e.md))で `AppType: pkg` / `AppType: lob` 双方の実マニフェストによる確認が必要。

### 未確定事項

- `preInstallScript` / `postInstallScript` を `JsonIgnoreCondition.Never` で明示的に `null` 送信している create リクエストを beta の `macOSPkgApp` が受け付けるかは Learn からは判断できない。実機で 400 が続く場合はここを疑う。

### 2026-08-21 追記: ドキュメントの macOS 26 対応漏れを修正

Codex によるレビューで、上記の実装変更(`MacOsMinimumOperatingSystemTable` への `v26_0` マッピング追加)が
ドキュメントに反映されていない旨の指摘があった。以下のファイルが `14`/`15` のみを列挙しており `26` が
漏れていたため修正した。

- [00-overview.md](00-overview.md) §6.13 — サポートする `Requirements.MinimumOSVersion` の一覧を明記する
  段落を新設し、`26`(`26.0`)を追加。
- [01-manifest-schema.md](01-manifest-schema.md) §5.7 — 「macOS 14/15 のフラグは beta 専用」を
  「macOS 14/15/26」に修正。
- [06-troubleshooting.md](06-troubleshooting.md) / [06-troubleshooting_ja.md](06-troubleshooting_ja.md) —
  `UnsupportedMacOsVersionException` の説明にある既知バージョン列挙に `26`/`26.0` を追加。
- `samples/manifests/README.md` / `README_ja.md` — 「v1.0 に無いフラグ」の列挙に `v26_0` を追加。

## 2026-09-10: Windows win32LobApp を Graph beta に統一 (400 NoPropertyForSelectedVersion の修正)

**ブランチ**: `fix/162-win32-graph-beta`

**対応 Issue / PR**: [#162](https://github.com/kkamegawa/Relaypublisher/issues/162)(親: [#161](https://github.com/kkamegawa/Relaypublisher/issues/161)) / [#165](https://github.com/kkamegawa/Relaypublisher/pull/165)

### 実施内容

既存 Windows app 2 件(x64/arm64)への再 publish が、content upload 成功後の `win32LobApp` 全体 PATCH で
`400 NoPropertyForSelectedVersion` により失敗した。Microsoft Learn で v1.0 / beta の `win32LobApp` resource
type を照合し、`Win32LobAppPayloadMapper` が常に送る `displayVersion`(および manifest 指定時の
`roleScopeTagIds`)が v1.0 の `win32LobApp` には存在せず beta のみに存在することを確認した。

1. `GraphWin32LobAppClient` の create/update を絶対パス `/beta/deviceAppManagement/mobileApps[/{id}]` に変更。
2. `WindowsAppPublisher` の `useBeta` 引数(processing-state 待機・content upload)を `true` に変更。
3. `CategoryApiVersion.UseBeta` を windows でも `true` を返すよう変更。
4. `GraphErrorReader` / `GraphRetryHandler` のエラーメッセージ・ログに HTTP メソッドを追加。
5. `doc/00-overview.md`、`doc/06-troubleshooting.md` / `_ja`(6e 節を新設)、`doc/adr/publishing.md`
   (2026-09-10 エントリ)、`doc/adr.md` を更新。
6. 回帰テストを追加(`GraphWin32LobAppClientTests` の displayVersion/roleScopeTagIds シリアライズ確認、
   `WindowsAppPublisherTests`・`CategoryServiceTests`・新規 `CategoryApiVersionTests` の beta=True 確認、
   `GraphErrorReaderTests`・`GraphRetryHandlerTests` のメソッド表示確認)。既存の Windows/beta=False 前提
   テストを beta=True に更新。

`dotnet build` + `dotnet test` は 733 件成功・0 件失敗(既存の skip 38 件は無関係)。

障害が発生した既存 2 app は content が commit 済みのため削除・再作成は不要。この修正版で再実行すれば
content は input hash 一致で skip され、失敗していたメタデータ PATCH・category 同期・assignment 同期が
完了する見込み。

残タスクは親 Issue #161 の子 Issue として継続: macOS lob の beta 化 (#163)、v1.0/beta 切り替え機構の削除 (#164)。

## 2026-09-10: macOS lob (macOSLobApp) を Graph beta に統一、macOS 14+ 制限を撤廃

**ブランチ**: `fix/163-macos-lob-graph-beta`(`fix/162-win32-graph-beta` 上に stack)

**対応 Issue / PR**: [#163](https://github.com/kkamegawa/Relaypublisher/issues/163)(親: [#161](https://github.com/kkamegawa/Relaypublisher/issues/161)) / PR は #165 に stack して作成予定

### 実施内容

#162(Windows win32LobApp の beta 化)と同じ調査から、`macOSLobApp`(`AppType: lob`)にも同種の
`roleScopeTagIds` 未対応(v1.0 に存在しない)というバグがあることを確認した。あわせて v1.0 の
`macOSMinimumOperatingSystem` が macOS 14 以降のフラグを持たないため、`AppType: lob` は
`Requirements.MinimumOSVersion` に macOS 14 以降を指定できないという既知の制限があった。

1. `MacOsAppPayloadMapper.ResolveTarget` で lob も `UseBeta: true` を返すよう変更。
2. `MacOsMinimumOperatingSystemTable` から `IsBetaOnly` 判定と `useBeta` 引数を削除し、全バージョンを
   `AppType` に関わらず使用可能にした。
3. `UnsupportedMacOsVersionException` から `requiresBetaOnlyFlag` 分岐を削除。
4. `tools/yamlcreate.ps1` の `$MacOsVersions` から beta 専用フラグと lob での除外処理を削除。
5. `doc/00-overview.md`、`doc/01-manifest-schema.md`、`doc/05-operation.md` / `_ja`、
   `doc/06-troubleshooting.md` / `_ja`(6f 節を新設)、`doc/08-yamlcreate.md` / `_ja`、
   `doc/adr/publishing.md`(2026-09-10 エントリに追記)を更新。
6. 回帰テストを追加・更新(`MacOsMinimumOperatingSystemTableTests` を beta 前提に書き換え、
   `MacOsAppPayloadMapperTests` に macOS 14 lob の positive テストを追加、`MacOsAppPublisherTests` に
   pkg/lob 両方が beta を使うことを確認するテストを追加)。`tests/Tools/YamlCreate.Tests.ps1` は Pester ではなく
   `Invoke-Case`/`Assert-*` による独立した PowerShell harness で、`pwsh -NoProfile -File
   tests/Tools/YamlCreate.Tests.ps1` で直接実行し 19/19 成功を確認した(新設した `AppType: lob` macOS
   14/15/26 提示ケースを含む)。

`dotnet build` + `dotnet test` は 741 件成功・0 件失敗(既存の skip 38 件は無関係。レビュー対応で追加した
`MacOsMinimumOperatingSystemTableTests` の "12.0"/"15.0"/"26" ケース分、735 から増加)。

既存の macOS 13 以前を指定した lob manifest の挙動は変わらない。macOS 14 以降を指定した既存アプリで
`RoleScopeTagIds` が原因の障害が発生していた場合も、#162 と同じ復旧手順(削除・再作成不要で再実行)が
適用できる。

残タスクは親 Issue #161 の子 Issue として継続: v1.0/beta 切り替え機構の削除 (#164)。
