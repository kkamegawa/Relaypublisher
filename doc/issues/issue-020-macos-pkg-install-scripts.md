# macos: pre/post-install script サポート(GitHub #86)

## Goal

macOS `AppType: pkg` の app に pre-install / post-install script を設定できるようにする。

Graph `macOSPkgApp` は `preInstallScript` / `postInstallScript`(型 `macOSAppScript`、`scriptContent` は
base64 エンコードされた shell script)を持つが、`macOSLobApp` / `macOSDmgApp` には存在しない。
`doc/01-manifest-schema.md` §5.4 の比較表には「pre/post install script: lob=不可 / pkg=可」と既に記載が
あったが、manifest schema・validation・Graph mapping は未実装だった。

## スコープ

- manifest に app entry 直下の `Scripts.PreInstall` / `Scripts.PostInstall`(repository-relative path)を
  追加する。`AppType: pkg` 限定。
- `ManifestValidator` で `Platform: windows` / `AppType: lob` への `Scripts` 指定、path traversal、
  `.sh` 以外の拡張子を fail にする。
- `ManifestAssetValidator` でファイル不存在、15360 文字以上、UTF-8 BOM、shebang 欠如を fail にする。
- `MacOsAppPayload` / `MacOsAppPayloadMapper` に `preInstallScript` / `postInstallScript`
  (`macOSAppScript`)を追加する。`MacOsPkgAppPayload` にのみ持たせる。
- `ManifestAssetReader` でスクリプトを読み込み、CRLF/CR → LF 正規化してから base64 化する。
- スクリプト本文は決定的 inputHash に含めない(`Icon` / `Detection.ScriptFile` と同じ前例)。
- `PlanService.EnumerateReferencedFiles` に `scripts/**` を含め、changed detection の対象にする。
- `doc/00-overview.md` §6.13、`doc/01-manifest-schema.md` §5.4.2(新規)、`README.md`/`_ja`、
  `doc/05-operation.md`/`_ja`、`doc/06-troubleshooting.md`/`_ja` を更新する。
- サンプル: `samples/scripts/macos/powershell/{preinstall,postinstall}.sh` と、PowerShell 7.6.5 の macOS
  manifest への適用。
- MSTest を追加する(manifest validation、asset validation、payload mapping、Graph client、
  publisher の CRLF 正規化、PlanService の逆引き)。

## 対象外

- `macOSLobApp` / `macOSDmgApp` への script 対応(Graph 側にプロパティが存在しないため不可)
- スタンドアロンの shell script policy(`deviceShellScript`)の管理
- スクリプトの構文チェック(shellcheck 等)

## 見積もり

- schema + validation + payload mapping + ドキュメント + サンプル + テスト(約 700–900 行)
