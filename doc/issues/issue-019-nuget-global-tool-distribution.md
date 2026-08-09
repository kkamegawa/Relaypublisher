# release: publish Relaypublisher as NuGet global tool

## Goal

`relaypublisher` を NuGet global tool として build / publish 可能にし、`nuget.org` へのリリース運用を設計正本に追加する。

## スコープ

- `IntuneLobPublisher.Cli.csproj` を global tool pack 対応にする
  - `PackAsTool: true`
  - `PackageId: relaypublisher`
  - `ToolCommandName: relaypublisher`
- バージョンは Git tag `vX.Y.Z` を唯一の正本とし、CI で `-p:Version=X.Y.Z` を注入する
- GitHub Actions / Azure Pipelines の release 設計を追加する
  - build/test 後に pack
  - `dotnet nuget push --skip-duplicate`
- 運用ドキュメントに install / update / rollback 手順を追加する

## 対象外

- Homebrew tap 設計・実装
- Windows Installer / PKG など NuGet 以外の配布チャネル

## 見積もり

- `csproj` + ドキュメント更新中心(約 80–140 行)

