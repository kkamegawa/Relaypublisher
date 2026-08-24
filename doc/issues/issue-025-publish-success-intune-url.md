# publish 成功時に Intune アクセス URL を標準出力に表示 (GitHub #107)

## Goal

Windows / macOS のアプリエントリが publish に成功したら、その Intune 管理センター上のアプリ詳細 URL を
標準出力(既存のロガーで Information レベル)に表示する。オペレーターや CI ログから、管理センターの
検索欄でアプリを探さなくても結果を直接開けるようにする。

正本は GitHub issue #107。#103 の対応と合わせて依頼された新機能。

## 現状の実装

- `PublishOrchestrator.PublishAsync`([src/IntuneLobPublisher.Core/Publishing/PublishOrchestrator.cs:171](../../src/IntuneLobPublisher.Core/Publishing/PublishOrchestrator.cs))
  は `PublishOutcome.Published` のとき `AppId` を含む `PublishResult` を返す。
- CLI 側(`PublishComposition` / `PublishCommand`)は既にアプリごとの結果をログ出力している。

## 確定仕様

- **重要な未確定事項**: Intune 管理センターのアプリ詳細ディープリンク形式は Microsoft Learn に
  ドキュメント化されていない(Graph API ではなく、管理センター SPA 内部のルーティング規約であるため)。
  コミュニティ由来の情報(Microsoft Q&A / Microsoft Community Hub のディスカッション)では旧
  Azure ポータル形式 `https://endpoint.microsoft.com/#blade/Microsoft_Intune_Apps/SettingsMenu/2/appId/{appId}`
  が確認できるが、リブランド後の現行コンソール(`https://intune.microsoft.com`)がどの URL 形式を
  使うかは **未検証**。誤った URL を出すのは何も出さないより悪いため、**実装前に実テナントで
  実際のアプリ詳細画面を開き、アドレスバーの URL を確認してから正式なテンプレートを決定すること**。
- URL の組み立ては `appId`(必要なら app 種別も)を受け取り URL 文字列を返す、小さく独立した
  ビルダー(例: `IntuneAppUrlBuilder`)に切り出す。未検証なのは「正確な URL テンプレート」という
  1 点だけなので、それをここに閉じ込めておけば、後から publish フロー本体に触れずに修正できる。
- CLI のアプリごとの成功報告経路にこのビルダーを組み込み、エントリの outcome が
  `PublishOutcome.Published` のときだけ URL を出力する(既存ロガー、Information レベル、他の
  per-app 結果と同じ扱い)。
- skip / dry-run / failed のエントリには出力しない。「実際に起きたことだけを表示する」という
  CLI の既存方針(`N published, N skipped...` サマリ行)に合わせる。

## テスト

- `IntuneAppUrlBuilder` の単体テスト(appId を与えたときの URL 形状)。
- `PublishComposition` レベルのテストで、`Published` のエントリだけに URL 行が出力されることを確認する。

## Non-goals

- URL をブラウザで自動的に開く機能は含まない。
- skip / failed エントリへの URL 出力は行わない。
