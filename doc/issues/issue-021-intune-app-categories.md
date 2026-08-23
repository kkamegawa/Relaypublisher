# Intune アプリカテゴリ対応 (GitHub #99)

## Goal

manifest の app entry に指定したカテゴリを、Microsoft Graph の Intune app category relationship として
Intune app に反映する。カテゴリは Company Portal 上でアプリを整理する tenant の共有リソースであり、
Relaypublisher はカテゴリそのもののライフサイクルではなく、app との関連だけを管理する。

正本は GitHub issue #99 と `doc/00-overview.md` §6.20。本ファイルは実装単位の作業メモとして、それらに追随する。

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
| `Categories` 省略 | 既存の app-category relationship を変更しない。category 関連の Graph read / write を一切行わない |
| `Categories: []` | 既存の app-category relationship をすべて解除する |
| 1 件以上 | 指定されたカテゴリの集合に完全同期する |

カテゴリ名は tenant 内の `mobileAppCategory.displayName` を正本とする。tenant 固有の category ID は manifest に
保存しない。未指定カテゴリの自動作成、カテゴリ名の変更、tenant-wide category の削除は対象外とする。

### Hash 互換性

`AppManifest.Categories` は **nullable な `List<string>?`(初期値なし)** とする。`Assignments` のような
非 nullable の `List<T> = []` にはしない。

- `InputHashCalculator` の canonical JSON は null property を落とすため、`Categories` を宣言していない既存
  manifest の `manifestHash` / `inputHash` はこの変更の前後で **byte 単位で不変**。pinned hash の test で固定する。
- 非 nullable にすると常に `"categories":[]` が出力され、repository 内の全 manifest の `inputHash` が変わって、
  アップグレード後の初回実行で全 app が再package / 再upload される(macOS PKG は最大 8 GB)。
- `ManifestLoader` は `IgnoreUnmatchedProperties()` のため、`Categories` を宣言した manifest を**古い CLI** で
  処理すると古い `manifestHash` が計算される。新旧 CLI を交互に実行すると `inputHash` が振動して毎回 upload が
  発生する。**`Categories` を使い始めたら CI と手元の CLI バージョンを揃える**ことをドキュメントで要求する。
- `SchemaVersion` は `"1.0"` のまま(additive optional field)。
- `inputHash` は manifest 全体を対象とする現行契約を変更しない。そのためカテゴリだけを変更した manifest では、
  content が同一でも hash 不一致により再package / 再upload が発生し得る。

### Validation

ローカル validation では Graph に接続せず、次を検証する。

- `Categories` の各要素は空でなく、空白のみでもない。
- 各要素は前後に空白を持たない(trim も Unicode 正規化もしない)。
- 同じ app entry 内で、大小文字を無視した (`OrdinalIgnoreCase`) 重複を禁止する。
- 件数上限・文字数上限・使用可能文字の制限は設けない。

`validate` は tenant に接続しないため、**存在しないカテゴリ名は validate では検出できない**。検出は publish /
dry-run の Graph preflight で行う。この境界をドキュメントに明記する。

publish / dry-run の preflight では、tenant のカテゴリ一覧をページング取得し、要求名を `OrdinalIgnoreCase` の
完全一致で ID に解決する。0 件または複数件の一致は、その app の最初の Graph write 前に失敗させる。

### Graph operations

カテゴリは `mobileApp` の scalar property ではなく `categories` navigation relationship として扱う。

- tenant category 一覧: `GET /deviceAppManagement/mobileAppCategories`
- app category 一覧: `GET /deviceAppManagement/mobileApps/{mobileAppId}/categories`
- 既存カテゴリの関連付け: `POST /deviceAppManagement/mobileApps/{mobileAppId}/categories/$ref`
- 関連付け解除: `DELETE /deviceAppManagement/mobileApps/{mobileAppId}/categories/{mobileAppCategoryId}/$ref`

両方の一覧取得で `@odata.nextLink` に従う。関連解除では `mobileAppCategories/{id}` を DELETE せず、必ず app 側の
`$ref` を DELETE する。

関連付け body は次の形とする。

```json
{
  "@odata.id": "<scheme>://<authority>/<version>/deviceAppManagement/mobileAppCategories/{categoryId}"
}
```

`@odata.id` は `GraphClientOptions.BaseAddress` の **scheme + authority のみ**と、その request と同じ version
segment から組み立てる。`BaseAddress` は `/v1.0/` で終わり request path は `/v1.0/…` または `/beta/…` の絶対
パスなので、`BaseAddress` に相対結合すると beta request に v1.0 の参照を載せてしまう。host も version も
ハードコードしない。path に埋め込む ID は必ずエスケープする。

API version は既存 publisher と同じルールにする。

- Windows `win32LobApp`: v1.0
- macOS `macOSLobApp`: v1.0
- macOS `macOSPkgApp`: beta

既存の `GraphRetryHandler`、ページング、tenant guard、request-id logging、secret masking を共用する。
アプリカテゴリ操作に必要な application permission は既存の `DeviceManagementApps.ReadWrite.All` のままとする
(実装時に Microsoft Learn で再確認する)。

### `$ref` の冪等性

`GraphRetryHandler` は 429/503 で request body を buffer して再送する(POST を含む)。したがって次を明示する。

- POST `$ref` の応答が「関連付けは既に存在する」ことを**具体的に**示す場合のみ成功として扱う。
- DELETE `$ref` の 404 は成功として扱う。
- 判定できない 4xx は失敗のままとする。400 / 409 を一律に握り潰さない。

category 専用 Learn ページには既存カテゴリの `$ref` request example がなく、重複時の実サービスのエラー形状も
文書化されていない。そのため POST の重複判定は小さなヘルパー(`CategoryRefResponseClassifier`)に隔離し、
実テナントで形状が判明したらそこだけを修正できるようにする。実テナントの disposable app / category による E2E を
完了条件に含める。

### 失敗分類

- 名前の不存在・曖昧一致・`$ref` の失敗は `CategorySyncException : PublisherException` とし、その manifest entry
  だけを失敗させて batch は継続する。
- tenant category 一覧の 403 は identity-wide なので既存の `GraphAccessDeniedException` 経路のままとし、CLI は
  batch を中断する(#94 と同じ扱い)。
- content activation 後の metadata update または category apply が失敗した場合、その entry は `failed` として報告され
  `PublishResultOutput.FromFailure` は `appId` を null のままにする。result file の形を安定させるため、この挙動は
  #99 で明示的に許容する。

## .NET architecture

`IntuneLobPublisher.Core/Publishing/Categories` に次の責務を追加する。

- category DTO: `IntuneAppCategory`(`Id` / `DisplayName`)
- `CategoryPlan`(`AppId` / `Requested` / `Entries`)と `CategoryPlanEntry`
- plan action: `Add`, `Keep`, `Remove`
- tenant catalog と app relationship の Graph client(`ICategoryGraphClient`)
- displayName 解決(`CategoryNameResolver`)、差分計算(`CategoryPlanner`)、`$ref` add/remove の service
  (`ICategoryService`)
- deterministic formatter(`CategoryPlanFormatter`)
- POST `$ref` の重複判定ヘルパー(`CategoryRefResponseClassifier`)

`Win32LobAppPayload` / `MacOsAppPayload` には `categories` を追加しない。relationship 操作は app create/update
payload から分離する。category の名前や ID は management metadata `notes` には保存しない。

`PublishOrchestrator` は次の順序で処理する。

1. app resolution と downgrade guard
2. category preflight(tenant 名前解決 + 既存 app なら現在の relationship 取得)と plan 作成 — **app write より前**
3. app create(新規 app の場合のみ)
4. content publish / activation(`publishingState` が `published` になるまで待機)
5. app metadata update(既存 app の場合のみ)
6. category plan apply(add を先、remove を後)
7. assignment plan/apply

Graph は `publishingState` が `published` でない app への metadata / category / assignment write を拒否するため、
content を Published 化してからこれらを適用する。新規 app では preflight で名前解決だけを行い(app ID がないので
per-app GET は行わない)、作成後に content を activate してから解決済み ID で add を適用する。既存 app が
`processing` の場合は既存の polling interval / timeout で `published` を待つ。`notPublished` の場合は保存済み
`inputHash` が一致していても content の完了状態を確認する。content version が 0 件なら作成し、1 件なら再利用する。
file が 0 件の単一 version には最初の file を作成する。未 commit file がある場合は、総数 1 件かつ対応する終端失敗
state で、現在の package と名前・サイズが一致するときだけ `renewUpload` して再利用する。一致しない、または複数 file
なら追加 file を作成せず fail する。同じ `inputHash` の単一 file が commit 済みなら app の
`committedContentVersion` PATCH から再開する。複数 version や commit 状態が混在・不明な場合は app / committed
content を削除せず fail する。未知の `publishingState` や timeout も明確な Graph エラーとしてその entry を失敗させる。

dry-run は Graph **read**(tenant / app の一覧取得)を行い、plan を表示して write は一切行わない。新規 app の plan
では `(new app)` を app ID placeholder として使う。

`IPublishOrchestrator.PublishAsync` は `Action<AssignmentPlan>?` 1 個ではなく、両方の plan callback を持つ
`PublishReport` を受け取る。`PublishResult` は `CategoryPlan?` を保持する。`PublishCommand` と
`PublishOrchestratorTests` が影響を受ける。

### Result file

`PublishResultEntry` に additive optional field `categoryOutcome` を 1 つだけ追加する。既存 field の名前・型・順序は
変更しない。値は次のとおりで、per-category の add/remove 詳細は console 出力と log にのみ出す。

| 値 | 意味 |
|---|---|
| `applied` | publish 完了。add / remove が 1 件以上あった |
| `unchanged` | publish 完了。`Categories` 指定はあるが差分なし |
| `not-requested` | publish 完了。manifest が `Categories` を省略した |
| null | category 処理に到達しなかった(skip、dry-run、preflight 前の失敗) |

カテゴリ同期の途中で失敗した場合は既存の batch 継続方針を維持し、次回実行時に Graph の現在値から plan を再計算
して収束させる。

## Tests and acceptance criteria

- manifest loader が省略、空配列、複数カテゴリを正しく区別する。
- 空文字、空白のみ、前後空白、case-insensitive duplicate が validation error になる。
- `Categories` を宣言していない manifest の `manifestHash` / `inputHash` が pinned 値と一致する。
- `Categories` を宣言した manifest の `inputHash` が変わる。
- tenant / app 両方の一覧で `@odata.nextLink` に従う。
- 名前解決の 0 件 / 1 件 / 複数件を検証する。
- v1.0 / beta の list、`$ref` add、`$ref` remove の URI と `@odata.id` の authority / version を検証する。
- 重複 add と不在 delete が冪等に成功として扱われ、判定できない 4xx は失敗のままになる。
- 429 の `Retry-After` で `$ref` body が verbatim に再送される。
- `Categories` 省略時は category Graph call がなく、空配列時は全 relationship が remove plan になる。
- 既存 relationship との差分が exact set として reconcile される。
- dry-run が Graph write を行わず、add/keep/remove を表示し、新規 app では `(new app)` を使う。
- 新規 app / 既存 app それぞれで orchestration 順序(preflight → create(新規のみ) → content activation →
  metadata update(既存のみ) → category apply → assignment)が守られる。
- `notPublished` の再実行で単一 content version を再利用する。file 0 件なら最初の file を作成し、対応する終端失敗 state の互換な未 commit file が総数 1 件なら renew/reuse する。不一致または複数 file は追加 file を作成せず fail する。単一 committed file の
  activation 再開と、複数 version / 混在 state の fail-safe も検証する。
- `CategorySyncException` は batch を継続させ、tenant listing の `GraphAccessDeniedException` は batch を中断する。
- result file の additive field と 4 つの outcome 値を検証する。
- 2 回目の実行が Keep に収束する。
- 実テナントで disposable category の関連付け・解除・再実行時の冪等性を確認する。
- Release 構成の `dotnet build` と `dotnet test` が成功する。

## Documentation and scope

設計正本は `doc/00-overview.md` §6.20、`doc/01-manifest-schema.md` §5.8、`doc/02-dotnet-architecture.md` §9.8 とし、
operation / troubleshooting / local E2E / README / sample manifest のカテゴリ説明を同期する。
`plan --base-ref` の changed detection(`PlanService.EnumerateReferencedFiles`)、GitHub Actions と Azure Pipelines
の job topology、`manifest-list.json`、Graph permission は変更しない。

対象外:

- tenant-wide category の create / update / delete
- category ID を manifest の移植用正本にすること
- content hash と management metadata hash の分離
- tenant 側でカテゴリ名が変更された場合の追随
