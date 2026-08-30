# .NET Architecture

## 7. .NET architecture

### 7.1 Solution structure

```text
IntuneLobPublisher.slnx
  src/
    IntuneLobPublisher.Cli/
      Program.cs

    IntuneLobPublisher.Core/
      Exceptions/
      Manifests/
      Validation/
      Planning/
      Staging/
      Packaging/
      Publishing/
        Assignments/
        Categories/
        ManagementMetadata.cs
      Sources/

  tests/
    IntuneLobPublisher.Core.Tests/
    IntuneLobPublisher.IntegrationTests/ (planned in #48)
```

The current implementation intentionally uses two projects. Graph/Intune operations are in
`Core/Publishing`, Azure Blob and GitHub Release providers are in `Core/Sources`, and the CLI
composition is in `IntuneLobPublisher.Cli`. Separate provider or integration-test projects are
future extensions, not current solution members.

### 7.2 Project responsibilities

#### `IntuneLobPublisher.Cli`

CLI entry point.

Commands:

```powershell
relaypublisher validate --manifest manifests/**/*.yaml
relaypublisher plan --manifest-root manifests --base-ref <sha> --output manifest-list.json
relaypublisher validate --manifest-list manifest-list.json
relaypublisher package --manifest-list manifest-list.json --output ./out
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out
relaypublisher publish --manifest .\manifests\Contoso.Tool.yaml
```

Manifest input options:

- `--manifest <path-or-pattern>` は `validate` / `package` / `publish` に直接渡す manifest path または glob である。単一 manifest の確認や、既に対象を決めているローカル操作で使用する。
- `plan --manifest-root <directory>` は manifest root を探索して対象一覧を作る。`--manifest <path>...` / `--manifests <path>...` を指定した場合は明示一覧が優先される。
- `--manifest-list <file>` は `plan --output` が生成した JSON を `validate` / `package` / `publish` に渡す。CI の job 間ではこの形式を使い、対象集合を再計算しない。

`plan --base-ref <sha>` の changed detection の定義(詳細は `00-overview.md` 6.6):

- `--base-ref <sha>` との git diff で変更された manifest を対象とする。
- `scripts/**` の変更は、その script を参照する manifest を逆引きして対象に含める。
- `--base-ref` が解決できない場合(zero SHA 等)は全件 fallback。
- `plan` が確定した対象一覧を `manifest-list.json` に出力し、`package` / `publish` はそれを入力とする。

publish の安全オプション:

- `--expected-tenant <tenant-id>`: token の `tid` claim と照合し、不一致なら fail(誤テナント防止)。
- `--allow-downgrade`: Intune 側 metadata より低い `PackageVersion` の publish を許可する。既定は skip + warning。
- Credential 解決の決定性は CLI option ではなく環境変数 `AZURE_TOKEN_CREDENTIALS` で確保する。未設定なら `publish` 開始時に warning を出す(00-overview.md 6.19)。

MVP:

- validate
- plan(base-ref changed detection + manifest-list.json 出力)
- package
- publish stub

#### `IntuneLobPublisher.Core`

CI / Intune / Azure に依存しない core logic。

Responsibilities:

- YAML load
- manifest model(SchemaVersion 検証を含む)
- validation(repository 全体の identity / DisplayName 一意性 lint を含む)
- changed manifest detection(base-ref diff + script 逆引き)
- staging
- checksum
- 決定的 input hash 計算(manifest + 入力ファイル群)
- path traversal 防止
- dry-run plan generation
- Intune metadata generation

#### `IntuneLobPublisher.Core/Publishing`

Microsoft Graph 経由で Intune を操作する implementation layer。

Responsibilities:

- Graph token acquisition(`--expected-tenant` の tid 照合、および取得ごとの identity(`appid`/`idtyp`/`roles`)ログを含む)
- mobile app search(複数一致は fail、DisplayName fallback 時は adopt)
- win32LobApp create / update
- `.intunewin` 展開と `Detection.xml` からの `fileEncryptionInfo` 組み立て
- Azure Storage SAS URI へのコンテンツアップロード(`renewUpload` 対応)
- commit + `committedContentVersion` PATCH
- upload state polling
- publishing state polling
- app category relationship sync(`$ref` add/remove、`Publishing/Categories`)
- assignment apply
- notes metadata update
- 429 / 503 の `Retry-After` 尊重 retry(全 Graph 呼び出し共通)

#### `IntuneLobPublisher.Core/Sources`

Azure Blob、private GitHub Release、public HTTP の source provider を担当する。将来、provider
ごとの独立 project が必要になった場合は、この directory と interface を境界に分割する。

---


## 8. NuGet package 案

### CLI

- `System.CommandLine`

### YAML

- `YamlDotNet`

### Validation

- `FluentValidation`

### Logging / DI

- `Microsoft.Extensions.Logging`
- `Microsoft.Extensions.DependencyInjection`

### Azure / Identity

- `Azure.Identity`
- `Azure.Storage.Blobs`

### Graph

最初は Graph SDK を使わず、`HttpClient` + `Azure.Identity` を推奨。

理由:

- Intune upload flow は多段階で REST URL と payload が明確な方が review しやすい。
- Graph request-id や retry を制御しやすい。
- SDK generated model に引きずられにくい。

Token acquisition example:

```csharp
var credential = new DefaultAzureCredential();
var token = await credential.GetTokenAsync(
    new TokenRequestContext(["https://graph.microsoft.com/.default"]),
    cancellationToken);
```

`new DefaultAzureCredential()`(引数なし)の credential chain 解決順は環境依存で保証されない。`AZURE_TOKEN_CREDENTIALS` 環境変数で固定することを推奨する(00-overview.md 6.19)。

### 8.1 Global tool packaging

`IntuneLobPublisher.Cli` は NuGet global tool として pack/publish する。

`IntuneLobPublisher.Cli.csproj` の必須設定:

```xml
<PackAsTool>true</PackAsTool>
<PackageId>relaypublisher</PackageId>
<ToolCommandName>relaypublisher</ToolCommandName>
```

運用ルール:

- `PackageId` と `ToolCommandName` はどちらも `relaypublisher` で固定。
- version は Git tag(`vX.Y.Z`)から CI が `-p:Version=X.Y.Z` で注入する。
- ローカル pack 検証時のみ `csproj` fallback version を使ってよい。
- package metadata(license/readme/repository/tags)は `csproj` に定義し、publish 前の `dotnet pack` で警告ゼロを維持する。

CI pack 例:

```bash
VERSION="${GITHUB_REF_NAME#v}"
dotnet pack src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj \
  --configuration Release \
  -p:ContinuousIntegrationBuild=true \
  -p:Version="$VERSION" \
  --output ./artifacts/nuget
```

---


## 9. Major interfaces

### 9.1 Manifest loader

```csharp
public interface IManifestLoader
{
    Task<IntunePackageManifest> LoadAsync(
        string path,
        CancellationToken cancellationToken);
}
```

### 9.2 Validator

```csharp
public interface IManifestValidator
{
    ValidationResult Validate(IntunePackageManifest manifest);
}
```

### 9.3 Source provider

```csharp
public interface ISourceProvider
{
    string SourceType { get; }

    Task<DownloadedFile> DownloadAsync(
        SourceDownloadRequest request,
        CancellationToken cancellationToken);
}
```

Implementations:

```text
PublicHttpSourceProvider
GitHubReleaseSourceProvider
AzureBlobSourceProvider
```

### 9.4 Staging engine

```csharp
public interface IWindowsStagingService
{
    Task<StagingResult> StageAsync(
        IntunePackageManifest manifest,
        AppManifest app,
        string workingDirectory,
        CancellationToken cancellationToken);
}
```

Processing:

1. Create temporary directory.
2. Copy `RepositoryFiles`.
3. Download `ExternalFiles`.
4. Verify SHA256.
5. Ensure `SetupFile` exists.
6. Generate staging summary.

### 9.5 IntuneWin packager

```csharp
public interface IIntuneWinPackager
{
    Task<IntuneWinPackageResult> CreatePackageAsync(
        StagingResult stagingResult,
        IntuneWinOptions options,
        CancellationToken cancellationToken);
}
```

MVP では `IntuneWinAppUtil.exe` を起動する。

```text
IntuneWinAppUtil.exe -c <source folder> -s <setup file> -o <output folder> -q
```

### 9.6 Intune app resolver

```csharp
public interface IIntuneAppResolver
{
    Task<IntuneAppResolutionResult> ResolveAsync(
        IntuneAppIdentity identity,
        CancellationToken cancellationToken);
}
```

```csharp
public sealed record IntuneAppIdentity(
    string PackageIdentifier,
    string Platform,
    string Architecture);
```

### 9.7 Assignment service

```csharp
public interface IAssignmentService
{
    Task<AssignmentPlan> CreatePlanAsync(
        string mobileAppId,
        IReadOnlyList<AppAssignment> desiredAssignments,
        AssignmentSyncMode syncMode,
        CancellationToken cancellationToken);

    Task ApplyAsync(
        AssignmentPlan plan,
        CancellationToken cancellationToken);
}
```

```csharp
public enum AssignmentSyncMode
{
    Merge,
    Replace
}
```

### 9.8 Category service(GitHub #99)

`Publishing/Categories` は Intune app category relationship の同期だけを担当する。tenant-wide な
`mobileAppCategory` リソースの作成 / 改名 / 削除は行わない(00-overview.md §6.20)。

```csharp
public interface ICategoryGraphClient
{
    Task<IReadOnlyList<IntuneAppCategory>> ListTenantCategoriesAsync(bool useBeta, CancellationToken cancellationToken);
    Task<IReadOnlyList<IntuneAppCategory>> ListAppCategoriesAsync(string appId, bool useBeta, CancellationToken cancellationToken);
    Task<bool> AddCategoryAsync(string appId, string categoryId, bool useBeta, CancellationToken cancellationToken);
    Task<bool> RemoveCategoryAsync(string appId, string categoryId, bool useBeta, CancellationToken cancellationToken);
}

public interface ICategoryService
{
    // existingAppId が null(新規 app)なら tenant 名前解決だけを行い、per-app GET は行わない。
    Task<CategoryPlan> CreatePlanAsync(string? existingAppId, AppManifest app, CancellationToken cancellationToken);

    Task ApplyAsync(CategoryPlan plan, AppManifest app, CancellationToken cancellationToken);
}
```

```csharp
public sealed record IntuneAppCategory(string Id, string DisplayName);

public enum CategoryPlanAction { Add, Keep, Remove }

public sealed record CategoryPlanEntry(CategoryPlanAction Action, string CategoryId, string DisplayName);

// Requested = false は「manifest が Categories を省略した」= Graph read も write も行わない状態。
// Categories: [] (desired set が空集合) とは区別する。
public sealed record CategoryPlan(string AppId, bool Requested, IReadOnlyList<CategoryPlanEntry> Entries);
```

補助クラス:

- `CategoryNameResolver`: `OrdinalIgnoreCase` の完全一致で displayName → ID を解決する pure logic。0 件 / 複数件は
  `CategorySyncException`。
- `CategoryPlanner`: desired と current の差分から Add / Keep / Remove を決める pure logic。remove は表示順が
  Graph の応答順に依存しないよう displayName でソートする。
- `CategoryPlanFormatter`: dry-run / publish 用の decisive な plan 表示。`Requested = false` の plan は空文字を返す。
- `CategoryRefResponseClassifier`: POST `$ref` の「既に関連付け済み」応答と DELETE `$ref` の 404 だけを成功に倒す
  判定を隔離する。

`Win32LobAppPayload` / `MacOsAppPayload` には `categories` を追加しない。relationship 操作は app create/update
payload から完全に分離する。

`IPublishOrchestrator.PublishAsync` は plan callback をまとめた `PublishReport`(`ReportCategoryPlan` /
`ReportAssignmentPlan`)を受け取り、`PublishResult` は `CategoryPlan?` を保持する。publish result JSON には
additive optional field `categoryOutcome`(`applied` / `unchanged` / `not-requested` / null)だけを追加し、
既存 field の名前・型・順序は変更しない。

### 9.9 macOS PKG bundle inspector

macOS の PKG 検査は Graph payload mapper から分離し、どの runner でも同じ結果になる pure な境界にする。
source provider が取得したファイルは、まず manifest の `Source.Sha256` と照合し、**checksum が成功した後にだけ**
inspector へ渡す。検査は source URL、認証情報、SAS URL を結果やログへコピーしない。

```csharp
public interface IPkgBundleInspector
{
    Task<PkgBundleInspectionResult> InspectAsync(
        Stream pkg,
        CancellationToken cancellationToken);
}

public sealed record PkgBundleInspectionResult(
    string InspectorVersion,
    IReadOnlyList<PkgBundleIdentity> Bundles);

public sealed record PkgBundleIdentity(
    string BundleId,
    string? BundleVersion,
    string? BundleBuildVersion,
    string SourceEntry);

public sealed record PkgInspectionReport(
    PkgBundleInspectionResult Inspection,
    string? SelectedPrimaryBundleId,
    IReadOnlyList<PkgInspectionWarning> Warnings,
    bool ForceAcknowledged);

public sealed record PkgInspectionWarning(
    PkgInspectionWarningCode Code,
    string? BundleId,
    string? Detail);

public enum PkgInspectionWarningCode
{
    MultipleBundlesWithoutExplicitPrimary,
    ManifestBundleNotFound,
    ManifestBundleVersionMismatch
}
```

`IPkgBundleInspector` は XAR の header、圧縮 TOC、TOC が指す `Distribution` / `PackageInfo` の entry を読み、
`CFBundleIdentifier`、`CFBundleShortVersionString`、`CFBundleVersion`を抽出する。TOC の文字列検索だけでは
file body を検査できないため、heap offset と長さを検証して必要な entry を展開する。payload 全体の展開や
macOS の `pkgutil` への依存は持たない。

`IPkgBundleInspector` は archive facts（bundle 一覧と inspector version）だけを返し、manifest 依存の判定は別の
`MacOsPkgInspectionPolicy` が `PkgInspectionReport` にまとめる。これにより同じ XAR fixtureを parser の
unit test、manifestとの突合 test、CLI の preflight test で再利用できる。

検査の上限は実装で固定し、XAR header、offset/length の checked arithmetic、TOC/XML の最大バイト数、XML の
深さ・要素数、bundle 一覧の最大件数、cancellation を検証する。header/offset の不整合、truncated archive、
未対応 compression、invalid UTF-8/XML、DTD/外部 entity、必要な `Distribution` / `PackageInfo` を読めない場合は
**hard error** とし、`--force` で回避できない。重複する bundle ID、曖昧な `PrimaryBundleId`、metadata の不正、
artifact の checksum 不一致も hard error とする。

manifest との突合で発生する `MultipleBundlesWithoutExplicitPrimary`、`ManifestBundleNotFound`、
`ManifestBundleVersionMismatch` は semantic warning として扱う。`IgnoreAppVersion: true` の場合、version mismatch
は検出判定上の warning にはせず、観測結果として report に残す。warning は TTY の確認または明示的な `--force` で
のみ承認できる。

### 9.10 package metadata と artifact trust

`package` は source の SHA256 検証後に PKG を検査し、結果を `package-metadata.json` に保存する。metadata は
次の additive schema を持つ。既存 Windows metadata では `inspection` を省略できるが、macOS artifact では
`contentSha256`、`cliVersion`、`inspection` を必須とする。

```json
{
  "metadataSchemaVersion": 2,
  "packageIdentifier": "Contoso.Tool",
  "platform": "macos",
  "architecture": "arm64",
  "inputHash": "<sha256>",
  "contentFile": "macos-arm64/contoso.pkg",
  "contentSha256": "<sha256>",
  "cliVersion": "1.2.3",
  "inspection": {
    "inspectorVersion": "1",
    "bundles": [
      {
        "bundleId": "com.contoso.tool",
        "bundleVersion": "1.2.3",
        "bundleBuildVersion": "123",
        "sourceEntry": "PackageInfo"
      }
    ],
    "selectedPrimaryBundleId": "com.contoso.tool",
    "warnings": [],
    "forceAcknowledged": false
  }
}
```

`contentSha256` は metadata の宣言値であり、publish の信頼根拠だけにはしない。`publish` は artifact を読み、
実ファイルを再ハッシュし、保存された `contentSha256` と一致することを確認してから、**source を再ダウンロード
せずに**同じ artifact を `IPkgBundleInspector` で再検査する。検査結果（bundle 一覧、selected primary、warning、
inspector/CLI version）が保存結果と一致しない、または metadata schema を解釈できない場合は Graph write 前に fail。

複数 manifest を publish する場合、全 artifact の checksum・metadata・PKG inspection・manifest の primary 選択を
先に preflight する。全件の preflight と warning acknowledgement が成功するまで、app resolve、content upload、
PATCH、assignment を一件も実行しない。これにより後続 manifest の拒否で先行 manifest だけが更新される部分適用を防ぐ。

### 9.11 macOS Graph payload mapping

primary の選択結果は manifest のファイル順を変更せず、payload の順序と top-level field にだけ反映する。

- `AppType: pkg` (`macOSPkgApp`): selected entry を `includedApps[0]`、`primaryBundleId`、
  `primaryBundleVersion` に反映する。
- `AppType: lob` (`macOSLobApp`): selected entry を `childApps[0]`、**top-level `bundleId`**、
  `buildNumber`、`versionNumber` に反映する。top-level `bundleId` は必ず selected primary の bundle ID とする。
- LOB の version は二つの manifest 値を混同しない。`BundleVersion` は `CFBundleShortVersionString` / Graph
  `buildNumber`、`BundleBuildVersion` は `CFBundleVersion` / Graph `versionNumber` に対応し、LOB では両方を
  検証する。PKG の `bundleVersion` と同じ値を機械的に `versionNumber` へ複製しない。

---


## 10. Manifest model 案

```csharp
public sealed class IntunePackageManifest
{
    public required string SchemaVersion { get; init; }

    public required string PackageIdentifier { get; init; }
    public required string PackageName { get; init; }
    public required string Publisher { get; init; }
    public required string Description { get; init; }
    public required string PackageVersion { get; init; }

    public string? Owner { get; init; }
    public string? Developer { get; init; }
    public string? InformationUrl { get; init; }
    public string? Icon { get; init; }
    public List<string> RoleScopeTagIds { get; init; } = [];

    public AssignmentSyncMode AssignmentSync { get; init; } = AssignmentSyncMode.Merge;

    public required List<AppManifest> Apps { get; init; }
}
```

```csharp
public sealed class AppManifest
{
    public required string Platform { get; init; }

    // Windows: required ("x64" | "arm64"). macOS: optional ("x64" | "arm64" | "universal"); an omitted
    // value resolves to "universal" via AppArchitecture.Resolve (issue #122), never written back here.
    public string? Architecture { get; init; }
    public required string InstallerType { get; init; }

    // macOS のみ: pkg (既定, macOSPkgApp) | lob (macOSLobApp)
    public string? AppType { get; init; }

    public string? DisplayName { get; init; }

    public WindowsPackageManifest? Package { get; init; }
    public SourceManifest? Source { get; init; }

    public InstallManifest? Install { get; init; }
    public DetectionManifest? Detection { get; init; }
    public RequirementsManifest? Requirements { get; init; }

    public List<AssignmentManifest> Assignments { get; init; } = [];

    // 省略(null)= 既存 relationship を維持、[] = 全解除、1 件以上 = 完全同期。
    // 省略と空配列を区別するため nullable かつ初期値なし(00-overview.md §6.7 / §6.20)。
    public List<string>? Categories { get; init; }
}
```

```csharp
public sealed class InstallManifest
{
    public required string CommandLine { get; init; }
    public required string UninstallCommandLine { get; init; }
    public required string InstallExperience { get; init; }
    public required string RestartBehavior { get; init; }

    // 省略時は Intune 既定セット (0/1707 success, 3010 softReboot, 1641 hardReboot, 1618 retry)
    public List<ReturnCodeManifest>? ReturnCodes { get; init; }
}
```

```csharp
public sealed class AssignmentManifest
{
    public string Target { get; init; } = "group"; // group | allDevices | allLicensedUsers
    public Guid? GroupId { get; init; }
    public string Mode { get; init; } = "include"; // include | exclude
    public string? Intent { get; init; }           // required | available | uninstall
    public Guid? FilterId { get; init; }
    public string? FilterMode { get; init; }       // include | exclude
    public AssignmentSettingsManifest? Settings { get; init; }
}
```

---


## 11. Implementation phases

### Issue #112 の stacked implementation boundary

Issue #112 / PR #113 の実装は、レビュー可能な機能単位で次の順に積み上げる。各 PR は前段の branch を
base とし、前段のテストと設計契約を壊さないことを merge 条件にする。後段の CLI/CI 変更を先に入れて、
未対応の PKG を warning 無視で publish できる期間を作らない。

1. **Foundation: manifest / selector / Graph mapping**
   - nullable な `Detection.PrimaryBundleId` と LOB 用 `BundleBuildVersion` を manifest model、loader、validator、
     canonical input hash に追加する。
   - exact または segment-boundary prefix の一意選択、未指定時の `IncludedApps[0]` 互換を実装する。
   - pkg の `primaryBundleId` / `primaryBundleVersion` と、LOB の `childApps[0]` / top-level `bundleId` /
     `buildNumber` / `versionNumber` を同じ selected primary から生成する。LOB の `BundleVersion` と
     `BundleBuildVersion` は別フィールドとして検証する。
   - payload mapping、validation、pinned input-hash、既存 manifest の回帰テストを完了する。

2. **Secure PKG inspector / artifact integrity**
   - `IPkgBundleInspector` と XAR parser を追加し、source SHA256 検証後にだけ inspection を実行する。
   - deterministic resource limits、hard error と semantic warning の分類、TTY/`--force` とは独立した parser
     failure の fail-closed 契約を実装する。
   - `package-metadata.json` に metadata schema version、CLI version、content SHA256、inspection result、
     inspector version、warning code、force acknowledgement を記録する。
   - publish は source を再取得せず artifact を再ハッシュ・再検査し、全件 preflight が完了するまで Graph write
     を開始しない。metadata tamper、checksum mismatch、inspection result mismatch は hard error とする。

3. **CLI / CI / E2E operations**
   - package/publish の warning presentation、TTY prompt、非対話時の fail、semantic warning のみを対象とする
     `--force` を実装する。
   - GitHub Actions / Azure Pipelines は CLI version を固定し、force は手動実行の protected environment approval
     がある場合だけ条件付きで渡す。package artifact と inspection report は job/stage 間でそのまま handoff する。
   - fake Graph contract、Windows/Ubuntu CLI integration、deterministic XAR fixture、protected manual tenant E2E を
     追加する。E2E は create/update/idempotent rerun、warning refusal、tampered artifact、LOB payload read-back、
     tenant guard を含め、成功後は作成物を cleanup する。

前段 PR が提供しない型・metadata・CLI optionを後段 PRで暗黙に追加しない。仕様変更が必要な場合は、先にこの
設計と `doc/00-overview.md` / `doc/01-manifest-schema.md` の正本を更新してからstackを組み直す。

### テスト実行環境

テストスイートでは、Windows 専用のパッケージング境界に対して MSTest の OS condition を使用する。以下のクラスにクラスレベルで
`[OSCondition(OperatingSystems.Windows)]` を付与する。

- `IntuneWinPackagerTests`
- `IntuneWinToolResolverTests`
- `WindowsStagingServiceTests`

これらのクラスは、初期化コードが実行される前に macOS / Linux 上ではスキップされる。IntuneWin ZIP の展開、パッケージメタデータの読み取り、
Graph payload のマッピング、macOS publish のテストなどポータブルなテストは、サポートするすべての runner で引き続き実行できるようにする。
標準の `dotnet test` コマンドはシェル固有のフィルタを必要としない。Windows 専用のカバレッジの実行が必要な場合は Windows runner 上で
実行する。この設計では独立した Windows CI job は追加しない。

### Phase 1: Manifest schema and validation

Tasks:

- Create manifest model.
- Load YAML.
- Validate top-level metadata.
- Validate Windows win32 app requirements.
- Validate assignments.
- Validate path safety.
- Add sample manifests.
- Add unit tests.

Acceptance criteria:

- Invalid manifests fail before download or Intune API call.
- Validation errors include field/path and reason.

### Phase 2: Windows staging engine

Tasks:

- Create per-app staging directory.
- Copy repository files.
- Download external files.
- Verify SHA256.
- Ensure setup file exists.
- Generate staging summary JSON.
- Support dry-run.

Acceptance criteria:

- x64 and Arm64 can use different script files.
- Missing file fails.
- Checksum mismatch fails.

### Phase 3: Source providers

Tasks:

- Implement `publicHttp`.
- Implement `githubRelease`.
- Implement `azureBlob`.
- Add retry/backoff.
- Mask credentials in logs.

### Phase 4: IntuneWin generation

Tasks:

- Locate `IntuneWinAppUtil.exe`(バージョン固定 + SHA256 検証。不一致は fail)。
- Run it on Windows runner.
- Generate `.intunewin`.
- Compute deterministic input hash(manifest + 入力ファイル群。`.intunewin` 自体のハッシュは非決定的なので identity に使わない)。
- Record tool version/hash and input hash in package metadata JSON.

### Phase 5: Intune app resolver

Tasks:

- Resolve app by notes metadata.
- Fallback to display name.
- 複数一致は fail(誤 app 上書き防止)。
- DisplayName fallback 一致時は notes metadata を書き戻す(adopt)。
- Do not match by version.

### Phase 6: Windows Intune publisher

Tasks:

- Map manifest to win32LobApp(`allowedArchitectures` / `minimumSupportedWindowsRelease` / `returnCodes` / detection script の base64 埋め込みを含む。詳細は issue-003)。
- Guard downgrade(既定 skip、`--allow-downgrade` で許可)。
- Create a new app when the resolver reports no match. For an existing app, defer the metadata PATCH
  until its content is published.
- Read `publishingState` before deciding whether stored `inputHash` permits a skip. Wait for
  `processing` to become `published`; force content upload for `notPublished` even when the hash matches;
  fail immediately for an unknown state.
- For `notPublished`, list typed `contentVersions` before creating one. Create a version when none exists;
  reuse a sole existing version. When that version has no files, create its first file. When it contains
  uncommitted files, renew and reuse only when the total count is one, its terminal failure state is supported,
  and its name and sizes match the current payload; reject non-matching or multiple files, multiple versions, or
  mixed/ambiguous committed state without deleting the app or committed content. When the stored and current
  `inputHash` values match and the sole file is already committed, resume at the
  `committedContentVersion` PATCH instead of uploading again.
- Extract `.intunewin` and build `fileEncryptionInfo` from `Detection.xml`.
- Upload encrypted payload to Azure Storage SAS URI(renewUpload 対応)。
- Build content URLs with the concrete OData type-cast segment after the app id
  (`win32LobApp`, `macOSPkgApp`, or `macOSLobApp`); the uncast `/contentVersions` route is not reliable.
  Interrupted-upload recovery uses `renewUpload` for a metadata-compatible file and does not depend on
  content-version/file DELETE or PATCH routes. It fails instead of adding a sibling file when stale file metadata differs.
- Commit file with `fileEncryptionInfo` and poll commit state.
- PATCH `committedContentVersion`.
- Poll publishing state.
- Update notes metadata.
- After content activation reaches `published`, update the existing app metadata, then apply categories
  and assignments.
- Honor `Retry-After` on 429/503.

### Phase 7: Assignment management

Tasks:

- Parse assignments(Target / Mode / Filter / Settings を含む拡張モデル)。
- Validate group GUID / built-in target / filter の組み合わせ。
- Support intents:
  - required
  - available
  - uninstall
- Merge = グループ単位 upsert(intent 競合は manifest が勝つ)。unlisted は削除しない。
- Replace = 完全同期(unlisted を削除)。
- Add dry-run diff.

### Phase 8: macOS publisher

Tasks:

- Download PKG.
- Verify SHA256.
- After the source SHA256 succeeds, inspect the PKG with `IPkgBundleInspector` and persist the result in
  `package-metadata.json`.
- At publish time, rehash the artifact and re-inspect it without downloading the source again. Compare both results with
  package metadata before any Graph call.
- Map manifest to `macOSPkgApp`(既定)または `macOSLobApp`(`AppType: lob`)。
- Map `Detection.IncludedApps`(bundleId + version リスト)to `includedApps` / `childApps` and move the selected primary
  to the first payload entry without changing the manifest file.
- For `AppType: lob`, set top-level `bundleId` from the selected primary. Map `BundleVersion` to `buildNumber` and
  `BundleBuildVersion` to `versionNumber`; do not copy one value into both fields.
- Enforce app type constraints(pkg: uninstall intent 不可 / lob: 署名必須・2 GB・Icon 必須)。
- Run all selected macOS artifacts through preflight before resolving apps or applying assignments. A hard error stops
  immediately; semantic warnings require TTY confirmation or `--force`.

Acceptance criteria:

- A source checksum mismatch, truncated/malformed XAR, unsupported compression, invalid metadata, artifact rehash
  mismatch, or report mismatch fails with zero Graph writes and cannot be bypassed by `--force`.
- Package and publish use the same content SHA256 and inspector version; publish never re-downloads a source.
- A multi-entry publish that is declined on a later item performs no Graph write for earlier items.
- The Graph payload contains the selected primary in the documented pkg/LOB fields, including LOB top-level `bundleId`
  and separate `buildNumber` / `versionNumber` values.
- A successful inspection report contains only bundle identities, warning codes, hashes, and tool versions; it never
  contains source credentials or signed URLs.

### Phase 9: GitHub Actions workflow

Tasks:

- PR: validate + dry-run.
- main: validate + package + publish.
- `concurrency` group で publish を直列化。
- `fetch-depth: 0` + `plan --base-ref` で changed を確定し、`manifest-list.json` を artifact で後続 job に渡す。
- Pin one exact `relaypublisher` CLI version for validate, package, publish, and manual E2E; record that version in the
  package metadata and fail if a handoff uses a different version.
- Permissions は job 単位で最小化(PR に `id-token: write` を付けない)。
- Package job に source provider 用 secrets を環境変数で渡す(fork PR には secrets が来ない点を明記)。
- Use OIDC.
- Use Windows runner for `.intunewin` generation(publish は REST のみなので ubuntu で可)。
- Use protected environment for production.
- `workflow_dispatch` の `forceWarnings` は既定 false とし、true のときだけ protected `production-force`
  environment の required reviewers を通過した場合に semantic warning 用 `--force` を渡す。push/PR や hard error
  に対して force を有効にする経路は設けない。
- Upload `manifest-list.json`、package files、`package-metadata.json`、inspection report を同じ run の artifact
  として handoff し、publish job で changed set を再計算せず、source を再ダウンロードしない（artifact 自体の
  rehash/reinspection は必須）。
- Add a protected manual E2E workflow for a disposable tenant. It must exercise real Graph create/update/read-back and
  cleanup, but it must never run for fork PRs or use production credentials.

### Phase 10: Azure Pipelines support

Tasks:

- Add `azure-pipelines.yml`.
- Use Azure Resource Manager service connection with workload identity federation.
- Production environment に Exclusive Lock check を設定。
- Reuse same .NET CLI.
- Pin one exact `relaypublisher` version in all stages; the version is a pipeline parameter or repository variable, not
  an unbounded global-tool install.
- Add a manual boolean `forceWarnings` parameter(default false). When true, require the protected `production-force`
  environment approval before passing `--force`; normal CI uses protected `production` and never passes it.
- Publish the manifest list and package/metadata artifacts from Validate/Package to Publish without recalculating the
  changed set. Publish performs artifact rehash and PKG reinspection before any Graph write.
- Add a protected manual E2E stage against a disposable tenant with tenant-id guard, Graph read-back, idempotent rerun,
  and cleanup. Keep tenant credentials and source-provider secrets out of PR validation.

---
