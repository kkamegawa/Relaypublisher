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
    public required string Architecture { get; init; }
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
- Create or update app.
- Skip content upload when stored `inputHash` matches.
- Guard downgrade(既定 skip、`--allow-downgrade` で許可)。
- Extract `.intunewin` and build `fileEncryptionInfo` from `Detection.xml`.
- Upload encrypted payload to Azure Storage SAS URI(renewUpload 対応)。
- Commit file with `fileEncryptionInfo` and poll commit state.
- PATCH `committedContentVersion`.
- Poll publishing state.
- Update notes metadata.
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
- Map manifest to `macOSPkgApp`(既定)または `macOSLobApp`(`AppType: lob`)。
- Map `Detection.IncludedApps`(bundleId + version リスト)to `includedApps`。
- Enforce app type constraints(pkg: uninstall intent 不可 / lob: 署名必須・2 GB・Icon 必須)。
- Apply assignments.

### Phase 9: GitHub Actions workflow

Tasks:

- PR: validate + dry-run.
- main: validate + package + publish.
- `concurrency` group で publish を直列化。
- `fetch-depth: 0` + `plan --base-ref` で changed を確定し、`manifest-list.json` を artifact で後続 job に渡す。
- Permissions は job 単位で最小化(PR に `id-token: write` を付けない)。
- Package job に source provider 用 secrets を環境変数で渡す(fork PR には secrets が来ない点を明記)。
- Use OIDC.
- Use Windows runner for `.intunewin` generation(publish は REST のみなので ubuntu で可)。
- Use protected environment for production.

### Phase 10: Azure Pipelines support

Tasks:

- Add `azure-pipelines.yml`.
- Use Azure Resource Manager service connection with workload identity federation.
- Production environment に Exclusive Lock check を設定。
- Reuse same .NET CLI.

---
