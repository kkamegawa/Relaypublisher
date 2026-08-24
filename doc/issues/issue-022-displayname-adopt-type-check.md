# DisplayName-adopt フォールバックの app 種別チェック (GitHub #104)

## Goal

`IntuneAppResolver` の DisplayName フォールバック(`notes` にメタデータが無いアプリを displayName 完全一致で
"adopt" する経路)が、Graph 上の型を見ずに一致させている問題を修正する。同名の別種アプリ(例:
`winGetApp` や macOS アプリが Windows Win32 アプリと同じ `displayName` を持つ場合)を誤って adopt すると、
後続の `microsoft.graph.win32LobApp` 型キャスト URL で意味の分かりにくい 400 が出るだけで、実際に何が
起きたのかログから読み取れない。

破壊的な書き込みは発生しない(フェイルセーフ)が、原因調査が困難という点で修正対象とする。

正本は GitHub issue #104。#103(win32LobApp の `setupFilePath` 欠落修正)のレビューで見つかった。

## 現状の実装

- `GraphIntuneAppDirectory.ListAppsAsync`([src/IntuneLobPublisher.Core/Publishing/GraphIntuneAppDirectory.cs:19](../../src/IntuneLobPublisher.Core/Publishing/GraphIntuneAppDirectory.cs))
  は `$select=id,displayName,notes` のみを要求しており、型情報を持たない。
- `IntuneAppSummary`([src/IntuneLobPublisher.Core/Publishing/IIntuneAppDirectory.cs](../../src/IntuneLobPublisher.Core/Publishing/IIntuneAppDirectory.cs))
  は `(Id, DisplayName, Notes)` のみの record。
- `IntuneAppResolver.ResolveAsync`([src/IntuneLobPublisher.Core/Publishing/IntuneAppResolver.cs](../../src/IntuneLobPublisher.Core/Publishing/IntuneAppResolver.cs))
  は `notes` メタデータ一致が 0 件のとき、`displayName` の完全一致(`StringComparison.Ordinal`)を
  フォールバックとして使い、1 件だけ一致すれば型を確認せず adopt する。

## 確定仕様

- Graph の `mobileApps` はポリモーフィック コレクションであり、`@odata.type` は `$select` の指定に
  関わらず常に付与される(Microsoft Learn の他 Intune ポリモーフィック一覧エンドポイントの応答例で
  確認済み)。念のため `$select` にも明示的に `@odata.type` を追加する。
- `IntuneAppSummary` に `ODataType` を追加し、内部 DTO `MobileAppEntry` でもパースする。
- `IPlatformAppPublisher` に `ExpectedODataType(AppManifest app)` を追加する。
  - `WindowsAppPublisher`: `PublishContentAsync` で既に使っている `"#microsoft.graph.win32LobApp"` 定数を返す。
  - `MacOsAppPublisher`: 既存の `MacOsAppPayloadMapper.ResolveTarget(app).ODataType` をそのまま再利用する
    (`AppType: pkg` → `macOSPkgApp`、`AppType: lob` → `macOSLobApp` の判定は既にここにある)。
- `IntuneAppResolver.ResolveAsync` に期待する Graph 型を渡し、DisplayName フォールバックの候補を
  この型でフィルタしてから曖昧判定・adopt 判定を行う。型が不一致(または欠落)のアプリは候補集合から
  **除外**する(優先度を下げるだけではなく、無関係アプリとして "ambiguous match" のノイズにもしない)。
- `notes` メタデータ一致は対象外。`ManagementMetadata` が既に `Platform`/`Architecture` を保持しており、
  それ自体が権威ある識別子のため型チェックは不要。

## テスト

- `GraphIntuneAppDirectoryTests`: `@odata.type` が `IntuneAppSummary.ODataType` に正しくパースされる。
- `IntuneAppResolverTests`:
  - DisplayName が一致しても `ODataType` が不一致なら `NotFound` を返す(adopt しない)。
  - `ODataType` が一致(または `null` — 型情報を返せない directory 実装との後方互換)なら従来どおり adopt する。
  - 型不一致のアプリは "ambiguous match" のカウントに含まれない(型一致するアプリが 1 件だけなら
    adopt、0 件なら `NotFound` になる)。

## Non-goals

- `notes` メタデータによる解決ロジックは変更しない。
- 正しく解決された後の挙動(create/update/content upload)は変更しない。
