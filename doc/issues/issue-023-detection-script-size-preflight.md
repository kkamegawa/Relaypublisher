# 検出スクリプトサイズの preflight 検証 (GitHub #105)

## Goal

Windows の検出スクリプト(`Detection.ScriptFile`)が `win32LobAppPowerShellScriptRule.scriptContent` に
対して大きすぎる場合、Graph への書き込みが起きる前に(`validate` / `package` / `publish --dry-run` の
段階で)失敗させる。現状は Graph 側が create/update を拒否して初めて判明する。

正本は GitHub issue #105。#103 のレビューで見つかった。

## 現状の実装

- `Win32LobAppPayloadMapper`([src/IntuneLobPublisher.Core/Publishing/Win32LobAppPayloadMapper.cs:72](../../src/IntuneLobPublisher.Core/Publishing/Win32LobAppPayloadMapper.cs))
  はリポジトリから読んだ検出スクリプトをそのまま base64 エンコードして `scriptContent` に埋め込む。
  `validate` / `package` / `publish --dry-run` のどの段階にもサイズチェックは存在しない。

## 確定仕様(実装時に確定させる注意点を含む)

- Microsoft Learn は `win32LobAppPowerShellScriptRule.scriptContent` 自体の上限を明記していない。
  兄弟リソースである `win32LobAppInstallPowerShellScript` / `win32LobAppUninstallPowerShellScript`
  (`mobileAppContentScript.content`)は "a maximum size limit of 100KB" と明記されている
  (<https://learn.microsoft.com/graph/api/resources/intune-apps-mobileappcontentscript>)。
  同じ app リソースファミリーの PowerShell スクリプト系プロパティであることから、これを保守的な
  代用値として採用する。**ただし検出ルール自身の上限として公式確認されたものではない**ことを、
  コードコメントと `doc/01-manifest-schema.md` の両方に明記する。
- 既定の閾値: 100 KB(102,400 バイト、base64 化前の生スクリプトサイズ)。
- 配置は実装時に決定する(`ManifestAssetValidator` か `WindowsAppPublisher.EnsureMappableAsync` /
  `ReadDetectionScriptAsync` のいずれか)。`EnsureMappableAsync` は dry-run でも呼ばれる経路
  ([src/IntuneLobPublisher.Core/Publishing/PublishOrchestrator.cs:124](../../src/IntuneLobPublisher.Core/Publishing/PublishOrchestrator.cs))
  なので、そこに置けば dry-run でも自動的に検証される。
- 失敗時のメッセージにはファイル名・実サイズ・上限を含め、原因が一目で分かるようにする。

## テスト

- 閾値未満のスクリプトはそのまま通る(既存挙動に影響なし)。
- 閾値超過のスクリプトは Graph 呼び出し前に、ファイル名・サイズ・上限を含む明確なメッセージで失敗する。
- `publish --dry-run` でも同じ検証が働く。

## Non-goals

- base64 エンコード方式や検出ルールの形は変更しない。
- 超過したスクリプトの圧縮・切り詰めは行わない(あくまで fail-fast)。
