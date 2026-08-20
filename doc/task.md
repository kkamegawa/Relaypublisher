# 作業記録

このファイルは、作業終了時にセッションごとの作業内容を記録するログです。各エントリは実施した plan と、参照した issue / Work Item へのリンクを含みます。

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
