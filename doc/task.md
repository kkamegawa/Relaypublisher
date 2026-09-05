# 作業記録

このファイルは、作業終了時にセッションごとの作業内容を記録するログです。各エントリは実施した plan と、参照した issue / Work Item へのリンクを含みます。

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
