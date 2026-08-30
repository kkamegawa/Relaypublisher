# 作業記録

このファイルは、作業終了時にセッションごとの作業内容を記録するログです。各エントリは実施した plan と、参照した issue / Work Item へのリンクを含みます。

## 2026-08-30: macOS Architecture 移行時の履歴 content 再 publish を防止

**対応 Issue / PR**: [#122](https://github.com/kkamegawa/Relaypublisher/issues/122) /
[#126](https://github.com/kkamegawa/Relaypublisher/pull/126)

PR #126 の review で、旧 version folder の明示 architecture entry と、省略形の新 `universal` entry が
同じ `DisplayName` を持つ場合、identity 単位の最高 version 選択を両方が通過し、処理順によって旧 entry が
DisplayName fallback で同じ Intune app を再 adopt して downgrade guard を回避できる問題が見つかった。

ユーザー確認済みの collapse 方針に基づき、次を実施した。

- `doc/00-overview.md` §6.8、`doc/issues/issue-023-macos-optional-architecture.md`、
  `doc/05-operation.md` / `_ja`、`doc/adr-phase-2.md` に migration collision の選択仕様を反映。
- `PublishCommand.SelectHighestVersions` の identity 単位選択後に、同じ
  `PackageIdentifier + Platform + DisplayName`、異なる実効 architecture、かつ `universal` を含む macOS
  entry を最高 `PackageVersion` の 1 件へ collapse。同一 version では移行先の `universal` を優先。
- `universal` を含まない x64/arm64、および異なる `DisplayName` は別 entry のまま維持する回帰 test を追加。

### 検証結果

```
dotnet format IntuneLobPublisher.slnx --verify-no-changes --no-restore
→ 変更なし。

dotnet build IntuneLobPublisher.slnx --configuration Release --no-restore -m:1
→ ビルドに成功しました。0 warning、0 error。

dotnet test IntuneLobPublisher.slnx --configuration Release --no-build --no-restore -m:1
→ 成功。失敗: 0、合格: 788、スキップ: 37、合計: 825。

git diff --check
→ 問題なし。
```

## 2026-08-29: macOS manifest の Architecture を任意化

**対応 Issue**: [#122](https://github.com/kkamegawa/Relaypublisher/issues/122)(サブ issue:
[#123](https://github.com/kkamegawa/Relaypublisher/issues/123)、
[#124](https://github.com/kkamegawa/Relaypublisher/issues/124)、
[#125](https://github.com/kkamegawa/Relaypublisher/issues/125))

**設計**: [English implementation plan](https://github.com/kkamegawa/Relaypublisher/wiki/plan/Relaypublisher/issue-122-macos-optional-architecture) /
[Japanese implementation plan](https://github.com/kkamegawa/Relaypublisher/wiki/plan/Relaypublisher/issue-122-macos-optional-architecture_ja)

`feature/122-macos-optional-architecture`(base: `feature/116-ci-e2e-integration`)上で、3 つのサブ issue を
非 stacked の通常コミットとして実装した。

- **#123 — 実効値の解決と validation**: 新規 `AppArchitecture.Resolve(AppManifest)` が
  「`Platform: macos` かつ `Architecture` が `null`」を `"universal"` に解決する唯一の箇所。
  `ManifestValues.Architectures` を `WindowsArchitectures`(`x64`/`arm64`、必須)と
  `MacOsArchitectures`(`x64`/`arm64`/`universal`、任意)に分離し、`ManifestValidator` の `Architecture`
  rule を platform 条件付きに変更。`Requirements.Architecture` は `Platform: macos` で禁止(従来は
  任意)にし、既存の一致 rule は `Platform: windows` に明示的に限定。`ManifestSetValidator` の
  repository-wide 一意性 lint を実効値ベースに変更(実効値が同じ省略形 2 件・省略形と `universal` 明示の
  組み合わせも重複として検出)。pinned hash test で、既存 macOS manifest(`Architecture` 明示)の
  `manifestHash`/`inputHash` が不変であること、省略形と `universal` 明示の hash が異なることを固定。
- **#124 — staging / packaging / publish への伝播**: `PublishOrchestrator`・`MacOsStagingService`・
  `MacOsPackager`(staging 結果と manifest entry の突合を実効値ベースに修正)・`PublishCommand.cs` の
  各 call site を resolver 経由に変更。**P1 で見つかった欠陥の修正を含む**: `PublishResultOutput.FromResult`/
  `FromFailure` が raw の `Architecture` を `Require` していたため、macOS 省略形 entry は Graph publish
  成功後に結果 JSON 生成で例外になり CLI が failure と誤報告していた。`PublishCommand.cs` の failure 経路の
  `?? ""` fallback も、notes には `universal` を書きながら結果 JSON には `"architecture": ""` を出す
  不整合があったため合わせて修正。
- **#125 — ドキュメント**: `doc/issues/issue-023-macos-optional-architecture.md` を新設し、
  `doc/01-manifest-schema.md`(§5.3.1 新設、§5.7 に Graph プロパティ非対応の注記)、
  `doc/00-overview.md`(§3.2、§6.1)、`doc/02-dotnet-architecture.md`(`Architecture` の
  `required string` 表記を nullable に修正)、`doc/05-operation.md`/`_ja`(§4b.2 新設、既存 app からの
  移行手順)、`doc/06-troubleshooting.md`/`_ja`(identity drift の行を追加)、
  `samples/manifests/README.md`/`_ja`(sample は明示形のまま、の節を追加)、`doc/adr.md` を更新した。
  sample manifest(`samples/manifests/**/*.yaml`)自体は変更していない。

### 検証結果

```
dotnet build IntuneLobPublisher.slnx --configuration Release
→ ビルドに成功しました。0 エラー。

dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
→ 成功! 失敗: 0、合格: 783、スキップ: 37、合計: 820
```

実機(Intune テナントへの実 publish / `publish --dry-run`)での検証は未実施。次回のローカル E2E
([07-local-e2e.md](07-local-e2e.md))で、`Architecture` を省略した macOS manifest による確認が必要。

## 2026-08-26: macOS PKG primary bundle — Layer 3 (publish preflight / CLI force gate / CI pin)

**対応 Issue / PR**: [#116](https://github.com/kkamegawa/Relaypublisher/issues/116)(親: [#112](https://github.com/kkamegawa/Relaypublisher/issues/112))

Layer 1(#114 / PR #117)・Layer 2(#115 / PR #118)に続く最終層。2 本の stacked PR で実装した。

- **PR A — `feature/116-publish-preflight-force`**(base: `feature/115-macos-pkg-inspector`):
  - `publish` に全 entry・zero-Graph-write の preflight(`PublishPreflight`)を追加。macOS entry は
    `PackageMetadataReader.ReadAndVerifyAsync` で source 再ハッシュ・XAR 再検査・CLI version pin 照合まで行い、
    windows entry は既存どおり存在・identity 照合のみ(`intuneWinSha256` は非決定的なため hash gate に使わない)。
  - `package` / `publish` に `--force` を追加。semantic warning は TTY で `[y/N]`、非対話では `--force` が
    無ければ fail。`package` は未確認 warning のある entry の `package-metadata.json` を削除して fail-closed。
  - tenant 検証を `GraphAuthenticationHandler.EnsureTenantVerifiedAsync` として明示化し、Graph mutation 前の
    preflight ステップにした(従来は最初の Graph GET の副作用だった)。
  - `PublishResultEntry` に追加のみの `warningCodes` / `forceAcknowledged` field を追加。
  - `MacOsPkgInspectionPolicy` に未実装だった `NoBundlesDetected` warning を追加。
  - Release build: 0 warnings, 0 errors。789 tests passed(既存 762 + 新規 27)。`git diff --check` passed。
- **PR B — `feature/116-ci-e2e-integration`**(base: PR A の branch):
  - `workflows/github-actions/publish-intune-apps.yml` / `workflows/azure-pipelines/azure-pipelines.yml`
    (参照サンプル)を doc/03・doc/04 の目標 YAML と一致させ、`RELAYPUBLISHER_VERSION` pin、
    `forceWarnings` input/parameter、`production-force` protected environment 分岐を追加。
  - `doc/adr.md` / `doc/05〜07` を Layer 3 の実装内容に合わせて更新。
  - **未反映**: `.github/workflows/ci.yml` への `macos-pkg-fixtures` job 追加と、新規
    `.github/workflows/intune-e2e.yml`(protected manual E2E)は、このセッションのツール権限で
    `.github/` への書き込みがブロックされていたため、YAML 本文をユーザーへ直接送付するに留めた。
    リポジトリへの反映(コピー・commit・`intune-e2e` environment の作成・disposable tenant 設定)は
    ユーザー側の別途対応が必要。

## 2026-08-26: macOS PKG detection primary bundle — final design and implementation split

**対応 Issue / PR**: [#112](https://github.com/kkamegawa/Relaypublisher/issues/112) / [#113](https://github.com/kkamegawa/Relaypublisher/pull/113)

このエントリは、設計レビュー後に確定した実装契約を記録します。`validate` は manifest schema と静的な
repository check のみを行い、source download や PKG 内容検査は行いません。`package` は source byte 列の
SHA-256 を検証してから XAR を検査し、bundle ID/version、selected primary、manifest identity、source SHA、
CLI version を report に保存します。`publish` は package report を信頼するだけにせず、staging 済み `.pkg`
を再 hash・再検査し、選択された全 entry の preflight が終わるまで Graph write を開始しません。

semantic warning は TTY では `[y/N]`、非対話環境では `--force` が無い限り fail とします。`--force` は
semantic difference の確認だけを行い、曖昧な primary、破損/XAR parse error、未対応 archive、SHA mismatch、
stale/tampered artifact、metadata/report 不整合、tenant/Graph safety error は回避できません。warning の拒否
または hard error は batch の Graph write を 0 件にします。

`AppType: lob` では `BundleVersion` を Graph `buildNumber`、`BundleBuildVersion` を `versionNumber` に対応させ、
selected primary を top-level bundle field と `childApps[0]` に反映します。

### Stacked implementation PRs

1. **Layer 1 — manifest contract and payload mapping**: nullable `PrimaryBundleId`、LOB の `BundleBuildVersion`、
   static validation、canonical hash compatibility、primary selection/reordering、pkg/lob mapping と unit test。
2. **Layer 2 — package inspection and operator acknowledgement**: secure XAR/XML inspector、source SHA-first
   package flow、inspection report、semantic warning/hard-error 分類、TTY `[y/N]`、non-TTY failure、`--force`、
   deterministic XAR fixture と CLI integration test。
3. **Layer 3 — publish preflight and operational verification**: artifact rehash/reinspection、Graph write 前の
   all-entry preflight、stale/tamper detection、CI の exact CLI pin、protected manual E2E、Graph read-back、device
   detection、idempotency、primary change、rejected-preflight no-write、cleanup。

各 layer は前段の branch/PR を親とする stacked PR とし、後段は前段の report/schema 契約を利用します。完了条件は
deterministic test、CI build/test、protected manual E2E のすべてを満たすことです。

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
