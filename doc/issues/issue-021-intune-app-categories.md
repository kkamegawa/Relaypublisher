# Intune アプリカテゴリ対応 (GitHub #99)

## Goal

manifest の app entry に指定したカテゴリを、Microsoft Graph の Intune app category relationship として
既存の Intune app に反映する。カテゴリは Company Portal 上でアプリを整理する tenant の共有リソースであり、
Relaypublisher はカテゴリそのもののライフサイクルではなく、app との関連だけを管理する。

## 確定仕様

### Manifest

`Categories` は `Apps[]` 配下に置く。platform / architecture ごとに Intune app が分かれるため、カテゴリも app
entry ごとに独立して指定する。

```yaml
Apps:
  - Platform: windows
    Architecture: x64
    Categories:
      - Business Apps
      - Productivity
```

意味論は次のとおりとする。

| Manifest | 動作 |
|---|---|
| `Categories` 省略 | 既存の app-category relationship を変更しない |
| `Categories: []` | 既存の app-category relationship をすべて解除する |
| 1 件以上 | 指定されたカテゴリの集合に完全同期する |

カテゴリ名は tenant 内の `mobileAppCategory.displayName` を正本とする。tenant 固有の category ID は manifest に
保存しない。未指定カテゴリの自動作成、カテゴリ名の変更、tenant-wide category の削除は対象外とする。

### Validation

ローカル validation では Graph に接続せず、次を検証する。

- `Categories` の各要素は空でなく、前後に空白を持たない。
- 同じ app entry 内で、大小文字を無視した重複を禁止する。
- `Categories` が省略された場合と空配列の場合を区別できる nullable model にする。

publish / dry-run の Graph preflight では、tenant のカテゴリ一覧をページング取得し、要求名を
`OrdinalIgnoreCase` の完全一致で ID に解決する。0 件または複数件の一致は、その app の最初の Graph write 前に
失敗させる。

### Graph operations

カテゴリは `mobileApp` の scalar property ではなく `categories` navigation relationship として扱う。

- tenant category 一覧: `GET /deviceAppManagement/mobileAppCategories`
- app category 一覧: `GET /deviceAppManagement/mobileApps/{mobileAppId}/categories`
- 既存カテゴリの関連付け: `POST /deviceAppManagement/mobileApps/{mobileAppId}/categories/$ref`
- 関連付け解除: `DELETE /deviceAppManagement/mobileApps/{mobileAppId}/categories/{mobileAppCategoryId}/$ref`

関連付け body は次の形とし、`@odata.id` は使用する Graph API version と category ID から生成する。

```json
{
  "@odata.id": "<graph-base-url>/v1.0/deviceAppManagement/mobileAppCategories/{categoryId}"
}
```

category 専用 Learn ページには既存カテゴリの `$ref` request example がないため、Graph の OData relationship
規約に従って実装し、実テナントの disposable app/category による E2E を完了条件に含める。関連解除では
`mobileAppCategories/{id}` を DELETE せず、必ず app 側の `$ref` を DELETE する。

API version は既存 publisher と同じルールにする。

- Windows `win32LobApp`: v1.0
- macOS `macOSLobApp`: v1.0
- macOS `macOSPkgApp`: beta

既存の `GraphRetryHandler`、ページング、tenant guard、request-id logging、secret masking を共用する。
アプリカテゴリ操作に必要な application permission は既存の `DeviceManagementApps.ReadWrite.All` のままとする。

## .NET architecture

`IntuneLobPublisher.Core/Publishing/Categories` に次の責務を追加する。

- category DTO: `id` / `displayName`
- `CategoryPlan` と `CategoryPlanEntry`
- plan action: `Add`, `Keep`, `Remove`
- tenant catalog と app relationship の Graph client
- displayName 解決、差分計算、`$ref` add/remove の service
- deterministic dry-run formatter

`Win32LobAppPayload` / `MacOsAppPayload` には `categories` を追加しない。relationship 操作は app create/update
payload から分離する。

`PublishOrchestrator` は次の順序で処理する。

1. app resolution と downgrade guard
2. Categories が指定されている app の Graph preflight と category plan 作成
3. app create/update
4. content publish
5. category plan apply (add を先、remove を後)
6. assignment plan/apply

dry-run は category plan を write 前に表示する。新規 app の plan では `(new app)` を app ID placeholder として
使う。既存 app では現在の relationship を取得して add/keep/remove を表示する。

カテゴリ同期の途中で失敗した場合は既存の batch 継続方針を維持し、次回実行時に Graph の現在値から plan を再計算
して収束させる。category の名前や ID は management metadata `notes` には保存しない。

`inputHash` は manifest 全体を対象とする現行契約を変更しない。そのためカテゴリだけを変更した manifest では、
content が同一でも hash 不一致により再package / 再upload が発生し得る。この挙動は inputHash の設計書と運用資料に
明記する。

## Tests and acceptance criteria

- manifest loader が省略、空配列、複数カテゴリを正しく区別する。
- 空文字、前後空白、case-insensitive duplicate が validation error になる。
- tenant category paging、単一一致、不存在、複数一致を検証する。
- v1.0 / beta の list、`$ref` add、`$ref` remove の URI と payload を検証する。
- `Categories` 省略時は category Graph call がなく、空配列時は全 relationship が remove plan になる。
- dry-run が Graph write を行わず、add/keep/remove を表示する。
- category preflight が app create/update より前に実行される。
- content publish 後、assignment sync 前に category apply が実行される。
- 429/503 の `Retry-After`、Graph error、部分失敗後の再実行を検証する。
- category-only manifest change が現行 inputHash を変更することを固定する。
- 実テナントで disposable category の関連付け・解除・再実行時の冪等性を確認する。
- Release 構成の `dotnet build` と `dotnet test` が成功する。

## Documentation and scope

設計正本は `doc/00-overview.md`、`doc/01-manifest-schema.md`、`doc/02-dotnet-architecture.md` とし、
operation / troubleshooting / local E2E / README / sample manifest のカテゴリ説明を同期する。
GitHub Actions と Azure Pipelines の job topology、`manifest-list.json`、Graph permission は変更しない。

対象外:

- tenant-wide category の create / update / delete
- category ID を manifest の移植用正本にすること
- content hash と management metadata hash の分離
