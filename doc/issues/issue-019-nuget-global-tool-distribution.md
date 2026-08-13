# release: publish Relaypublisher as NuGet global tool

## Goal

`relaypublisher` を NuGet global tool として build / publish 可能にし、`nuget.org` へのリリース運用を設計正本に追加する。

## スコープ

- `IntuneLobPublisher.Cli.csproj` を global tool pack 対応にする
  - `PackAsTool: true`
  - `PackageId: relaypublisher`
  - `ToolCommandName: relaypublisher`
- バージョンは Git tag `vX.Y.Z` を唯一の正本とし、CI で `-p:Version=X.Y.Z` を注入する
- GitHub Actions CI workflow を追加する(`workflows/github-actions/ci.yml`)
  - main ブランチへの pull request で `dotnet build` / `dotnet test` を実行する
  - `dotnet pack -p:Version=0.0.0-ci` で global tool として pack できることを検証する
- GitHub Actions release workflow を追加・更新する(`workflows/github-actions/release-nuget-tool.yml`)
  - trigger: `v*` tag push
  - build/test 後に pack
  - pack 後に `gh release create --draft` で GitHub draft release を作成し `.nupkg` を添付する
  - `dotnet nuget push --skip-duplicate` で nuget.org に publish する
- 運用ドキュメントに install / update / rollback 手順を追加する

## 対象外

- Homebrew tap 設計・実装
- Windows Installer / PKG など NuGet 以外の配布チャネル

## 見積もり

- `csproj` + ドキュメント更新中心(約 80–140 行)

