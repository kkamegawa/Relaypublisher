# release: publish Relaypublisher as NuGet global tool

## Goal

`relaypublisher` を NuGet global tool として build / publish 可能にし、リリース運用を設計正本に追加する。

## スコープ

- `IntuneLobPublisher.Cli.csproj` を global tool pack 対応にする
  - `PackAsTool: true`
  - `PackageId: relaypublisher`
  - `ToolCommandName: relaypublisher`
- バージョンは Git tag `vX.Y.Z` を唯一の正本とし、CI で `-p:Version=X.Y.Z` を注入する
- GitHub Actions CI workflow を追加する(`.github/workflows/ci.yml`)
  - main ブランチへの pull request で `dotnet build` / `dotnet test` を実行する(ubuntu / windows matrix)
  - `dotnet pack -p:Version=0.0.0-ci.<run_number>` で global tool として pack できることを検証する
  - `win-x64` / `win-arm64` / `osx-arm64` の self-contained single-file app を生成し artifact 化する
  - secrets を一切参照しない(public 化後に fork からの PR を通すため)
- GitHub Actions release workflow を 2 本追加する
  - `.github/workflows/release-draft.yml`
    - trigger: `v*` tag push。tag が main から到達可能であることを検証する
    - build/test 後に pack し、3 RID の single-file app と `SHA256SUMS.txt` を生成する
    - `gh release create --draft` で GitHub draft release を作成し、上記資産を添付する
  - `.github/workflows/release-publish.yml`
    - trigger: `release: [published]`(draft release を人が publish した時点)
    - release に添付された `.nupkg` を download し、再ビルドせずに feed へ push する
    - push 先は GitHub Packages / Azure Artifacts / nuget.org の 3 feed。すべて `--skip-duplicate`
    - Azure Artifacts は OIDC (workload identity federation) + artifacts-credprovider で認証し、
      feed URL は secret (`AZURE_ARTIFACTS_FEED_URL`) から渡す
- 運用ドキュメントに install / update / rollback 手順と、3 feed それぞれの install 手順を追加する

## 対象外

- Homebrew tap 設計・実装
- Windows Installer / PKG など NuGet 以外の配布チャネル
- single-file app の署名・notarization
- `linux-x64` の single-file app

## 更新履歴

- 2026-08-24: 配布先を nuget.org 単独から 3 feed に拡張し、feed push の trigger を `push: tags` から
  `release: published` に変更した。workflow の置き場所を `workflows/github-actions/`(参照サンプル)から
  `.github/workflows/`(実 CI)に移した。理由は `doc/adr.md` を参照。

## 見積もり

- `csproj` + ドキュメント更新中心(約 80–140 行)

