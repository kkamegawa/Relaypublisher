# WindowsReleaseTable への 25H2/26H1 追加と保守メモ (GitHub #106)

## Goal

`WindowsReleaseTable` に不足している Windows 11 25H2 のビルド番号マッピングを追加し、今後の年次
Feature Update で同じ不足が発生しないよう、保守用の参照先コメントを残す。

正本は GitHub issue #106。#103 のレビューで見つかった。

## 現状の実装

- `WindowsReleaseTable.BuildToRelease`([src/IntuneLobPublisher.Core/Publishing/WindowsReleaseTable.cs:13](../../src/IntuneLobPublisher.Core/Publishing/WindowsReleaseTable.cs))
  は `["10.0.26100"] = "Windows11_24H2"` で止まっている。
- 未知のビルド番号は `UnsupportedWindowsBuildException` で fail-fast する設計のため、サイレントに
  壊れることはないが、コード変更とリリースをしないと新しい Windows バージョンを指定できない。

## 確定仕様

- Microsoft Learn(<https://learn.microsoft.com/windows/release-health/windows11-release-information>、
  <https://learn.microsoft.com/windows/release-health/supported-versions-windows-client>)によれば、
  Windows 11 25H2(ビルド 26200)は 2025-09-30 から一般提供されており、現在のテーブルに欠落している。
  `["10.0.26200"] = "Windows11_25H2"` を追加する。命名は既存エントリすべてが従っている
  `Windows11_<マーケティングバージョン>` パターンをそのまま踏襲するため、確度は高い。
- Windows 11, version 26H1(ビルド 28000、2026-02-10 GA)は
  「新しいデバイス向けに限定提供され、既存デバイスへの Feature Update としては提供されない」
  特殊リリース(<https://learn.microsoft.com/windows/whats-new/windows-11-version-26h1>)。
  Intune の `minimumSupportedWindowsRelease` が自由文字列としてこのビルドに `Windows11_26H1` という
  慣習値を割り当てているかは **本稿執筆時点で未確認**(Microsoft Learn の win32LobApp サンプルは
  `Windows11_23H2` までしか例示していない)。実装時に、実テナントの Win32 アプリ作成 UI が持つ
  ドロップダウン、または更新された Graph ドキュメントで値を確認してから追加する。未確認のまま
  推測で追加しない(#103 の発端そのものが「推測で埋めた必須プロパティ」だったため、同じ轍を踏まない)。
- `WindowsReleaseTable` の先頭に、今後ビルド番号を追加する際の一次情報として上記 2 つの Learn URL を
  指す短い保守コメントを追加する。

## テスト

- `Map("10.0.26200")` が `"Windows11_25H2"` を返す。
- 既存の「未知ビルドで `UnsupportedWindowsBuildException`」テストは引き続き成功する。

## Non-goals

- fail-fast 設計(近似値へのフォールバックなど)は変更しない。
- 過去の Windows 10 ビルドの欠落を網羅的に埋める作業はスコープ外。今回不足しているテーブル末尾のみ対応する。
