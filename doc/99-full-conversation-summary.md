# Intune LOB App Publisher 設計メモ / Copilot 実装用 Issue 一式

作成日: 2026-07-04

> **注意**: この文書は初期検討時の会話スナップショット(歴史的記録)です。
> その後のレビューで設計が更新されており、この文書の内容は最新ではありません。
> 最新の設計は `00-overview.md` 〜 `04-ci-azure-pipelines.md` および `issues/` 配下を参照してください。

このドキュメントは、GitHub / Azure Pipelines から YAML manifest をトリガーに Microsoft Intune の LOB / Win32 アプリを登録・更新する仕組みについて、会話で整理した要件、設計方針、実装計画、GitHub Copilot に渡す Issue をまとめたものです。

---

## 1. 初期要件

winget のような仕組みで、Intune 管理の LOB アプリケーションを Git に push すると Intune にアプリ登録する。

GitHub へ YAML 定義を commit したら、Intune へアプリを upload する。

条件:

- winget の schema を流用する。
- 組織内向けのため、以下は不要。
  - license
  - history
  - release notes
- public に認証なしで download できるものは repository に binary を持たない。
- public download 可能なものは YAML に download URL を書くだけにする。
- 組織内 binary は以下から取得する。
  - Azure Blob
  - private GitHub Release
- Azure Blob は Azure 認証で download する。
- private GitHub Release は GitHub PAT などの認証で download する。
- Intune に存在しない app であれば新規追加。
- Intune に存在すれば更新。
- macOS / Windows Arm64 / Windows x64 を別 app として管理する。

---

## 2. 追加要件

追加で確定した要件:

1. 小規模運用なので assignment も YAML で管理する。
   - 対象 group は GUID 指定。
2. `.intunewin` は自動生成する。
   - `.intunewin` は script + 実行 app で構成されることが多い。
   - script は repository 内に置く。
   - binary は別の public URL に置くケースを考慮する。
   - script は platform / architecture、たとえば x64 / Arm64 で異なる場合がある。
3. 既存 app 更新時は同じ Intune app ID を更新する。
   - assignment を維持する。
4. Intune app の display name に version は含めない。
5. GitHub hosted runner を主対象にする。
   - Azure Pipelines も検討する。
6. 実装言語は .NET とする。
   - レビュー容易性を重視。

---

## 3. 基本方針

### 3.1 ゴール

GitHub repository または Azure Repos に Intune LOB app 定義 YAML を commit すると、CI が起動し、定義に従って app package を取得・検証・必要に応じて package 化し、Intune に新規登録または更新する。

### 3.2 管理方針

- winget manifest の考え方を流用する。
- 組織内向けなので license / history / public release notes は省略する。
- Intune 上では以下を別 app として管理する。
  - Windows x64
  - Windows Arm64
  - macOS x64
  - macOS Arm64
  - 必要なら macOS universal
- 同一 app 判定は `PackageIdentifier + Platform + Architecture` を基本とする。
- Intune に存在しなければ新規追加。
- Intune に存在すれば更新。
- public に認証なしで取得可能な binary は Git に置かない。
- private / internal なものは source provider 経由で取得する。

---

## 4. Repository 構成案

```text
repo/
  manifests/
    Contoso/
      Contoso.Tool/
        1.2.3/
          Contoso.Tool.yaml
  scripts/
    windows/
      x64/
      arm64/
      common/
  src/
    IntuneLobPublisher.Cli/
    IntuneLobPublisher.Core/
    IntuneLobPublisher.Intune/
    IntuneLobPublisher.Azure/
    IntuneLobPublisher.GitHub/
  tests/
    IntuneLobPublisher.Core.Tests/
    IntuneLobPublisher.Intune.Tests/
    IntuneLobPublisher.IntegrationTests/
  samples/
    manifests/
    scripts/
  docs/
    manifest-schema.md
    operation.md
    troubleshooting.md
  .github/
    workflows/
      publish-intune-apps.yml
  azure-pipelines.yml
```

---

## 5. YAML schema 案

### 5.1 Windows x64 例

```yaml
PackageIdentifier: Contoso.Tool
PackageName: Contoso Tool
Publisher: Contoso Ltd.
Description: Internal tool for Contoso employees.
PackageVersion: 1.2.3
AssignmentSync: merge

Apps:
  - Platform: windows
    Architecture: x64
    InstallerType: win32
    DisplayName: Contoso Tool [Windows x64]

    Package:
      IntuneWin:
        SetupFile: install.ps1

      RepositoryFiles:
        - Source: scripts/windows/x64/install.ps1
          Destination: install.ps1
        - Source: scripts/windows/common/uninstall.ps1
          Destination: uninstall.ps1
        - Source: scripts/windows/common/detect.ps1
          Destination: detect.ps1

      ExternalFiles:
        - Type: publicHttp
          Url: https://example.com/downloads/contoso-tool-1.2.3-x64.exe
          Destination: bin/contoso-tool.exe
          Sha256: "<sha256>"

    Install:
      CommandLine: powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
      UninstallCommandLine: powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1
      InstallExperience: system
      RestartBehavior: suppress

    Detection:
      Type: script
      ScriptFile: scripts/windows/common/detect.ps1
      RunAs32Bit: false
      EnforceSignatureCheck: false

    Requirements:
      MinimumOSVersion: 10.0.19045
      Architecture: x64

    Assignments:
      - GroupId: "00000000-0000-0000-0000-000000000001"
        Intent: required
      - GroupId: "00000000-0000-0000-0000-000000000002"
        Intent: available
```

### 5.2 Windows Arm64 例

```yaml
  - Platform: windows
    Architecture: arm64
    InstallerType: win32
    DisplayName: Contoso Tool [Windows Arm64]

    Package:
      IntuneWin:
        SetupFile: install.ps1

      RepositoryFiles:
        - Source: scripts/windows/arm64/install.ps1
          Destination: install.ps1
        - Source: scripts/windows/common/uninstall.ps1
          Destination: uninstall.ps1
        - Source: scripts/windows/common/detect.ps1
          Destination: detect.ps1

      ExternalFiles:
        - Type: githubRelease
          Owner: contoso
          Repository: internal-tools
          Tag: v1.2.3
          AssetName: contoso-tool-1.2.3-arm64.exe
          Destination: bin/contoso-tool.exe
          Sha256: "<sha256>"
          AuthSecretName: GH_RELEASE_PAT

    Install:
      CommandLine: powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
      UninstallCommandLine: powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1
      InstallExperience: system
      RestartBehavior: suppress

    Detection:
      Type: script
      ScriptFile: scripts/windows/common/detect.ps1
      RunAs32Bit: false
      EnforceSignatureCheck: false

    Requirements:
      MinimumOSVersion: 10.0.22621
      Architecture: arm64

    Assignments:
      - GroupId: "00000000-0000-0000-0000-000000000001"
        Intent: required
```

### 5.3 macOS 例

```yaml
  - Platform: macos
    Architecture: arm64
    InstallerType: pkg
    DisplayName: Contoso Tool [macOS Arm64]

    Source:
      Type: azureBlob
      AccountName: contosopackages
      Container: intune-packages
      BlobName: macos/contoso-tool/1.2.3/contoso-tool-arm64.pkg
      Destination: contoso-tool-arm64.pkg
      Sha256: "<sha256>"
      Auth:
        Type: workloadIdentity

    Detection:
      Type: bundle
      BundleId: com.contoso.tool
      Version: 1.2.3
      Operator: greaterThanOrEqual

    Assignments:
      - GroupId: "00000000-0000-0000-0000-000000000003"
        Intent: required
```

---

## 6. 重要な設計判断

### 6.1 App identity

Intune 側に独自の package identifier field はないため、検索 key を明示的に決める。

推奨:

```text
PackageIdentifier + Platform + Architecture
```

Intune app の `notes` に management metadata を保存する。

```json
{
  "managedBy": "intune-lob-manifest",
  "packageIdentifier": "Contoso.Tool",
  "packageVersion": "1.2.3",
  "platform": "windows",
  "architecture": "x64",
  "manifestPath": "manifests/Contoso/Contoso.Tool/1.2.3/Contoso.Tool.yaml",
  "manifestHash": "...",
  "packageSha256": "...",
  "sourceCommit": "..."
}
```

照合順:

1. `notes` 内の management metadata
2. `DisplayName` fallback

### 6.2 Display name

Version は含めない。

```text
Contoso Tool [Windows x64]
Contoso Tool [Windows Arm64]
Contoso Tool [macOS x64]
Contoso Tool [macOS Arm64]
```

理由:

- assignment を維持しやすい。
- app ID を維持しやすい。
- Intune app 一覧が増殖しにくい。

### 6.3 Assignment sync

既定は `merge`。

```yaml
AssignmentSync: merge
```

明示的に YAML を正とする場合のみ `replace`。

```yaml
AssignmentSync: replace
```

事故防止のため、初期値は `merge` がよい。

### 6.4 Azure 認証

GitHub hosted runner の場合、Azure VM の managed identity は使えないため、GitHub Actions OIDC + Microsoft Entra ID workload identity federation を使う。

Azure Pipelines の場合も、Azure Resource Manager service connection with workload identity federation を使う。

---

## 7. .NET architecture

### 7.1 Solution structure

```text
IntuneLobPublisher.sln
  src/
    IntuneLobPublisher.Cli/
      Program.cs

    IntuneLobPublisher.Core/
      Manifests/
      Validation/
      Staging/
      Packaging/
      Sources/
      Assignments/
      Metadata/

    IntuneLobPublisher.Intune/
      Graph/
      Apps/
      Win32/
      MacOS/
      Upload/
      Assignments/

    IntuneLobPublisher.Azure/
      Identity/
      Blob/

    IntuneLobPublisher.GitHub/
      Releases/

  tests/
    IntuneLobPublisher.Core.Tests/
    IntuneLobPublisher.Intune.Tests/
    IntuneLobPublisher.IntegrationTests/
```

### 7.2 Project responsibilities

#### `IntuneLobPublisher.Cli`

CLI entry point.

Commands:

```powershell
intune-lob-publisher validate --manifest manifests/**/*.yaml
intune-lob-publisher plan --changed
intune-lob-publisher package --changed --output ./out
intune-lob-publisher publish --changed --package-dir ./out
intune-lob-publisher publish --manifest .\manifests\Contoso.Tool.yaml
```

MVP:

- validate
- package
- publish stub

#### `IntuneLobPublisher.Core`

CI / Intune / Azure に依存しない core logic。

Responsibilities:

- YAML load
- manifest model
- validation
- changed manifest detection
- staging
- checksum
- path traversal 防止
- dry-run plan generation
- Intune metadata generation

#### `IntuneLobPublisher.Intune`

Microsoft Graph 経由で Intune を操作する layer。

Responsibilities:

- Graph token acquisition
- mobile app search
- win32LobApp create / update
- `.intunewin` content upload
- upload state polling
- publishing state polling
- assignment apply
- notes metadata update

#### `IntuneLobPublisher.Azure`

Azure Blob download を担当。

#### `IntuneLobPublisher.GitHub`

Private GitHub Release asset download を担当。

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

---

## 10. Manifest model 案

```csharp
public sealed class IntunePackageManifest
{
    public required string PackageIdentifier { get; init; }
    public required string PackageName { get; init; }
    public required string Publisher { get; init; }
    public required string Description { get; init; }
    public required string PackageVersion { get; init; }

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

    public string? DisplayName { get; init; }

    public WindowsPackageManifest? Package { get; init; }
    public SourceManifest? Source { get; init; }

    public InstallManifest? Install { get; init; }
    public DetectionManifest? Detection { get; init; }
    public RequirementsManifest? Requirements { get; init; }

    public List<AssignmentManifest> Assignments { get; init; } = [];
}
```

---

## 11. Implementation phases

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

- Locate or download `IntuneWinAppUtil.exe`.
- Run it on Windows runner.
- Generate `.intunewin`.
- Compute generated package SHA256.

### Phase 5: Intune app resolver

Tasks:

- Resolve app by notes metadata.
- Fallback to display name.
- Do not match by version.

### Phase 6: Windows Intune publisher

Tasks:

- Map manifest to win32LobApp.
- Create or update app.
- Upload generated `.intunewin`.
- Commit content version.
- Poll upload state.
- Poll publishing state.
- Update notes metadata.

### Phase 7: Assignment management

Tasks:

- Parse assignments.
- Validate group GUID.
- Support intents:
  - required
  - available
  - uninstall
- Merge by default.
- Replace only if specified.

### Phase 8: macOS publisher

Tasks:

- Download PKG.
- Verify SHA256.
- Map manifest to macOS LOB app.
- Apply assignments.

### Phase 9: GitHub Actions workflow

Tasks:

- PR: validate + dry-run.
- main: validate + package + publish.
- Use OIDC.
- Use Windows runner for `.intunewin` generation.
- Use protected environment for production.

### Phase 10: Azure Pipelines support

Tasks:

- Add `azure-pipelines.yml`.
- Use Azure Resource Manager service connection with workload identity federation.
- Reuse same .NET CLI.

---

## 12. GitHub Actions example

```yaml
name: Publish Intune Apps

on:
  pull_request:
    paths:
      - "manifests/**/*.yaml"
      - "scripts/**"
      - "src/**"
  push:
    branches:
      - main
    paths:
      - "manifests/**/*.yaml"
      - "scripts/**"
      - "src/**"
  workflow_dispatch:
    inputs:
      dryRun:
        type: boolean
        default: true

permissions:
  contents: read
  id-token: write

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "9.0.x"

      - run: dotnet build IntuneLobPublisher.sln --configuration Release

      - run: dotnet test IntuneLobPublisher.sln --configuration Release --no-build

      - run: dotnet run --project src/IntuneLobPublisher.Cli -- validate --changed

  package-windows:
    needs: validate
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "9.0.x"

      - run: dotnet run --project src/IntuneLobPublisher.Cli -- package --changed --output ./out

      - uses: actions/upload-artifact@v4
        with:
          name: intunewin-packages
          path: ./out

  publish:
    needs:
      - validate
      - package-windows
    if: github.event_name == 'push' && github.ref == 'refs/heads/main'
    runs-on: windows-latest
    environment: production
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "9.0.x"

      - name: Azure login
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - uses: actions/download-artifact@v4
        with:
          name: intunewin-packages
          path: ./out

      - run: dotnet run --project src/IntuneLobPublisher.Cli -- publish --changed --package-dir ./out
```

---

## 13. Azure Pipelines example

```yaml
trigger:
  branches:
    include:
      - main
  paths:
    include:
      - manifests/*
      - scripts/*
      - src/*

pr:
  paths:
    include:
      - manifests/*
      - scripts/*
      - src/*

stages:
  - stage: Validate
    jobs:
      - job: Validate
        pool:
          vmImage: ubuntu-latest
        steps:
          - checkout: self
          - task: UseDotNet@2
            inputs:
              packageType: sdk
              version: 9.0.x
          - script: dotnet build IntuneLobPublisher.sln --configuration Release
          - script: dotnet test IntuneLobPublisher.sln --configuration Release --no-build
          - script: dotnet run --project src/IntuneLobPublisher.Cli -- validate --changed

  - stage: Package
    dependsOn: Validate
    jobs:
      - job: PackageWindows
        pool:
          vmImage: windows-latest
        steps:
          - checkout: self
          - task: UseDotNet@2
            inputs:
              packageType: sdk
              version: 9.0.x
          - script: dotnet run --project src/IntuneLobPublisher.Cli -- package --changed --output "$(Build.ArtifactStagingDirectory)\out"
          - publish: "$(Build.ArtifactStagingDirectory)\out"
            artifact: intunewin-packages

  - stage: Publish
    dependsOn: Package
    condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))
    jobs:
      - deployment: PublishToIntune
        environment: production
        pool:
          vmImage: windows-latest
        strategy:
          runOnce:
            deploy:
              steps:
                - checkout: self
                - download: current
                  artifact: intunewin-packages
                - task: UseDotNet@2
                  inputs:
                    packageType: sdk
                    version: 9.0.x
                - task: AzureCLI@2
                  inputs:
                    azureSubscription: '<workload-identity-service-connection-name>'
                    scriptType: ps
                    scriptLocation: inlineScript
                    inlineScript: |
                      dotnet run --project src/IntuneLobPublisher.Cli -- publish --changed --package-dir "$(Pipeline.Workspace)\intunewin-packages"
```

---

## 14. Copilot に渡す初期 Issue: .NET 版

```markdown
# Implement .NET CLI foundation for Intune LOB app publisher

## Goal

Create a .NET CLI tool that validates winget-like YAML manifests and stages Windows Win32 app packages.

The staging process must combine repository scripts and externally downloaded binaries into a single directory that can later be converted to `.intunewin`.

This issue focuses on the foundation only. Actual Microsoft Intune upload, Microsoft Graph integration, assignment API calls, Azure Blob downloads, GitHub Release downloads, macOS support, and actual `.intunewin` generation are out of scope for this issue.

## Technology

Use the following technologies:

- .NET 9
- C#
- System.CommandLine
- YamlDotNet
- FluentValidation
- xUnit
- Microsoft.Extensions.Logging
- Microsoft.Extensions.DependencyInjection

## Solution structure

Create or update the solution with the following projects:

- `src/IntuneLobPublisher.Cli`
- `src/IntuneLobPublisher.Core`
- `tests/IntuneLobPublisher.Core.Tests`

The CLI project should depend on the Core project.

The test project should depend on the Core project.

## CLI commands

Implement the following commands.

### validate

Validates one or more manifest files.

Example:

```bash
dotnet run --project src/IntuneLobPublisher.Cli -- validate --manifest manifests/Contoso.Tool.yaml
```

Also support glob-style or directory-based input if practical:

```bash
dotnet run --project src/IntuneLobPublisher.Cli -- validate --manifest manifests/**/*.yaml
```

Command options:

- `--manifest`
  - One or more manifest paths or glob patterns.
- `--repo-root`
  - Repository root directory.
  - Default: current working directory.
- `--verbose`
  - Enables verbose logging.

Behavior:

- Load YAML manifests.
- Validate required fields.
- Print validation errors with useful paths and messages.
- Return non-zero exit code if validation fails.

### package

Stages Windows Win32 app package files.

Example:

```bash
dotnet run --project src/IntuneLobPublisher.Cli -- package --manifest manifests/Contoso.Tool.yaml --output ./out
```

Command options:

- `--manifest`
  - One or more manifest paths or glob patterns.
- `--repo-root`
  - Repository root directory.
  - Default: current working directory.
- `--output`
  - Output directory for staged packages.
- `--dry-run`
  - Show what would happen without copying or downloading files.
- `--verbose`
  - Enables verbose logging.

Behavior:

- Validate manifest before staging.
- For each Windows app entry:
  - Create a per-app staging directory.
  - Copy repository files into the staging directory.
  - Download external files into the staging directory.
  - Verify SHA256 for external files.
  - Ensure `Package.IntuneWin.SetupFile` exists in the staging directory.
  - Generate a staging summary JSON file.
- Return non-zero exit code if staging fails.

### publish

Add the command but keep it as a stub for now.

Example:

```bash
dotnet run --project src/IntuneLobPublisher.Cli -- publish --manifest manifests/Contoso.Tool.yaml
```

Behavior:

- Print a clear message that publish is not implemented yet.
- Return non-zero exit code or dedicated `NotImplemented` result.

## Manifest schema

Support the following top-level YAML fields.

```yaml
PackageIdentifier: Contoso.Tool
PackageName: Contoso Tool
Publisher: Contoso Ltd.
Description: Internal tool for Contoso employees.
PackageVersion: 1.2.3
AssignmentSync: merge

Apps:
  - Platform: windows
    Architecture: x64
    InstallerType: win32
    DisplayName: Contoso Tool [Windows x64]

    Package:
      IntuneWin:
        SetupFile: install.ps1

      RepositoryFiles:
        - Source: scripts/windows/x64/install.ps1
          Destination: install.ps1
        - Source: scripts/windows/common/uninstall.ps1
          Destination: uninstall.ps1
        - Source: scripts/windows/common/detect.ps1
          Destination: detect.ps1

      ExternalFiles:
        - Type: publicHttp
          Url: https://example.com/downloads/contoso-tool-1.2.3-x64.exe
          Destination: bin/contoso-tool.exe
          Sha256: "<sha256>"

    Install:
      CommandLine: powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
      UninstallCommandLine: powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1
      InstallExperience: system
      RestartBehavior: suppress

    Detection:
      Type: script
      ScriptFile: scripts/windows/common/detect.ps1
      RunAs32Bit: false
      EnforceSignatureCheck: false

    Requirements:
      MinimumOSVersion: 10.0.19045
      Architecture: x64

    Assignments:
      - GroupId: "00000000-0000-0000-0000-000000000001"
        Intent: required
```

## Manifest model requirements

Create C# model classes for the manifest.

Suggested models:

- `IntunePackageManifest`
- `AppManifest`
- `WindowsPackageManifest`
- `IntuneWinManifest`
- `RepositoryFileManifest`
- `ExternalFileManifest`
- `InstallManifest`
- `DetectionManifest`
- `RequirementsManifest`
- `AssignmentManifest`

### Top-level fields

Required:

- `PackageIdentifier`
- `PackageName`
- `Publisher`
- `Description`
- `PackageVersion`
- `Apps`

Optional:

- `AssignmentSync`

Default:

```yaml
AssignmentSync: merge
```

Supported values:

- `merge`
- `replace`

For this issue, only parse and validate `AssignmentSync`.
Do not implement assignment API behavior.

## Windows app validation rules

For app entries where:

```yaml
Platform: windows
InstallerType: win32
```

Validate the following.

### Required fields

- `Architecture`
- `DisplayName`
- `Package`
- `Package.IntuneWin`
- `Package.IntuneWin.SetupFile`
- `Install.CommandLine`
- `Install.UninstallCommandLine`
- `Detection`
- `Requirements`

### Supported Architecture values

- `x64`
- `arm64`

### Supported InstallExperience values

- `system`
- `user`

### Supported RestartBehavior values

- `suppress`
- `allow`
- `force`

### Supported Detection.Type values

For this issue, implement only:

- `script`

Other detection types can be added later.

For script detection, validate:

- `Detection.ScriptFile` is required.
- `Detection.RunAs32Bit` is optional.
- `Detection.EnforceSignatureCheck` is optional.

### Requirements validation

Validate:

- `Requirements.MinimumOSVersion` is required.
- `Requirements.Architecture` must match the app-level `Architecture`.

### Assignment validation

Validate each assignment:

- `GroupId` must be a valid GUID.
- `Intent` must be one of:
  - `required`
  - `available`
  - `uninstall`

For this issue, only parse and validate assignments.
Do not call Intune assignment APIs.

## Staging behavior

Implement Windows staging for each app entry.

### Staging directory naming

Create a stable and safe staging directory name derived from:

- `PackageIdentifier`
- `Platform`
- `Architecture`

Example:

```text
out/
  Contoso.Tool/
    windows-x64/
      staging/
      staging-summary.json
```

Do not include `PackageVersion` in the staging path unless needed for collision prevention.

### RepositoryFiles

For each item in `Package.RepositoryFiles`:

```yaml
- Source: scripts/windows/x64/install.ps1
  Destination: install.ps1
```

Behavior:

- Resolve `Source` relative to `--repo-root`.
- Validate that the source file exists.
- Copy the source file into the staging directory at `Destination`.
- Create destination directories as needed.
- Preserve file contents exactly.

Security requirements:

- Reject absolute `Destination` paths.
- Reject paths containing traversal, such as:
  - `../`
  - `..\`
- Reject destinations that escape the staging directory after path normalization.

### ExternalFiles

For each item in `Package.ExternalFiles`:

```yaml
- Type: publicHttp
  Url: https://example.com/downloads/contoso-tool-1.2.3-x64.exe
  Destination: bin/contoso-tool.exe
  Sha256: "<sha256>"
```

For this issue, support only:

```yaml
Type: publicHttp
```

Behavior:

- Download the file from `Url`.
- Save it into the staging directory at `Destination`.
- Verify SHA256 if `Sha256` is specified.
- Fail if SHA256 does not match.
- Create destination directories as needed.

Validation:

- `Url` is required.
- `Destination` is required.
- `Sha256` is strongly recommended.
- For this issue, make `Sha256` required for `publicHttp`.

Security requirements:

- Reject absolute `Destination` paths.
- Reject path traversal.
- Do not log sensitive headers.
- Do not log credentials.
- Do not support authenticated downloads in this issue.

### SetupFile validation

After repository files and external files are staged:

- Validate that `Package.IntuneWin.SetupFile` exists under the staging directory.
- Reject path traversal in `SetupFile`.
- Reject absolute `SetupFile`.

Example:

```yaml
Package:
  IntuneWin:
    SetupFile: install.ps1
```

The following file must exist:

```text
<staging-directory>/install.ps1
```

### Detection script validation

For this issue, `Detection.ScriptFile` may refer to a repository path.

Example:

```yaml
Detection:
  Type: script
  ScriptFile: scripts/windows/common/detect.ps1
```

Validation behavior:

- Validate that the file exists relative to repo root.
- The file does not have to be copied into staging unless it is also listed in `RepositoryFiles`.
- If the detection script is intended to be included in the package, the manifest author should also include it in `RepositoryFiles`.

## Staging summary JSON

Generate a `staging-summary.json` file for each staged app.

Example:

```json
{
  "packageIdentifier": "Contoso.Tool",
  "packageName": "Contoso Tool",
  "packageVersion": "1.2.3",
  "platform": "windows",
  "architecture": "x64",
  "displayName": "Contoso Tool [Windows x64]",
  "stagingDirectory": "out/Contoso.Tool/windows-x64/staging",
  "setupFile": "install.ps1",
  "repositoryFiles": [
    {
      "source": "scripts/windows/x64/install.ps1",
      "destination": "install.ps1"
    }
  ],
  "externalFiles": [
    {
      "type": "publicHttp",
      "url": "https://example.com/downloads/contoso-tool-1.2.3-x64.exe",
      "destination": "bin/contoso-tool.exe",
      "sha256": "<sha256>",
      "actualSha256": "<actual-sha256>"
    }
  ]
}
```

Do not include tokens, secrets, Authorization headers, or credentials in this file.

## Dry-run behavior

When `--dry-run` is specified:

- Validate manifests.
- Print what files would be copied.
- Print what files would be downloaded.
- Print the expected staging directory.
- Do not copy files.
- Do not download files.
- Do not create output directories unless necessary for logging.
- Do not generate staging summary files unless explicitly designed as dry-run output.

## Logging requirements

Use `Microsoft.Extensions.Logging`.

Log:

- Manifest path being loaded.
- Number of app entries.
- Validation errors.
- Staging directory path.
- Repository files copied.
- External files downloaded.
- SHA256 verification result.
- Staging summary path.

Do not log:

- Secrets
- Tokens
- Authorization headers
- Full authenticated URLs if future providers add signed URLs

## Error handling

Create clear exception or result types for common errors.

Recommended errors:

- `ManifestLoadException`
- `ManifestValidationException`
- `StagingException`
- `SourceDownloadException`
- `ChecksumMismatchException`
- `UnsafePathException`

CLI should convert these errors into:

- Clear console output
- Non-zero exit code

## Tests

Add xUnit tests under:

```text
tests/IntuneLobPublisher.Core.Tests
```

### Manifest validation tests

Add tests for:

- Valid Windows x64 manifest passes.
- Valid Windows Arm64 manifest passes.
- Missing `PackageIdentifier` fails.
- Missing `PackageVersion` fails.
- Missing `Apps` fails.
- Unsupported `Platform` fails.
- Unsupported `Architecture` fails.
- Unsupported `InstallerType` fails.
- Missing `Package.IntuneWin.SetupFile` fails.
- Missing `Install.CommandLine` fails.
- Missing `Install.UninstallCommandLine` fails.
- Invalid assignment `GroupId` fails.
- Invalid assignment `Intent` fails.
- `Requirements.Architecture` mismatch fails.

### Path safety tests

Add tests for:

- Destination `../evil.ps1` is rejected.
- Destination `..\evil.ps1` is rejected.
- Absolute Windows path is rejected.
- Absolute Unix path is rejected.
- Normal nested destination like `bin/app.exe` is accepted.
- SetupFile with path traversal is rejected.
- SetupFile absolute path is rejected.

### Staging tests

Add tests for:

- Repository file is copied to staging.
- Missing repository file fails.
- SetupFile exists after staging.
- Missing SetupFile fails.
- Different architecture can use a different install script.
- Staging summary JSON is generated.

### Checksum tests

Add tests for:

- Correct SHA256 passes.
- Incorrect SHA256 fails.
- SHA256 comparison is case-insensitive.
- SHA256 with invalid format fails validation.

## Sample manifests

Add sample manifests under:

```text
samples/manifests/
```

Recommended files:

```text
samples/manifests/contoso-tool-windows-x64.yaml
samples/manifests/contoso-tool-windows-arm64.yaml
```

Add sample scripts under:

```text
samples/scripts/windows/x64/install.ps1
samples/scripts/windows/arm64/install.ps1
samples/scripts/windows/common/uninstall.ps1
samples/scripts/windows/common/detect.ps1
```

Sample scripts can be minimal placeholders.

Example `install.ps1`:

```powershell
Write-Host "Installing Contoso Tool"
```

Example `uninstall.ps1`:

```powershell
Write-Host "Uninstalling Contoso Tool"
```

Example `detect.ps1`:

```powershell
Write-Host "Detected"
exit 0
```

## Out of scope

The following are intentionally out of scope for this issue:

- Microsoft Graph authentication
- Intune app create/update
- Intune Win32 upload flow
- Assignment API calls
- Actual `.intunewin` generation
- IntuneWinAppUtil integration
- Azure Blob source provider
- GitHub Release source provider
- macOS app support
- Rollback
- Package history
- License metadata
- Release notes metadata

## Acceptance criteria

- `dotnet build` succeeds.
- `dotnet test` succeeds.
- `validate` succeeds for valid sample manifests.
- `validate` fails for invalid manifests with useful messages.
- `package` can stage a Windows x64 app.
- `package` can stage a Windows Arm64 app with a different install script.
- Repository scripts are copied into staging.
- Public HTTP external files are downloaded into staging.
- SHA256 mismatch fails the command.
- Destination path traversal is rejected.
- Missing repository files fail the command.
- Missing setup file fails the command.
- Staging summary JSON is generated.
- No secrets or tokens are printed in logs.
```

---

## 15. Next Issues

### 15.1 Add IntuneWinAppUtil integration

```markdown
# Add IntuneWinAppUtil integration to .NET CLI

## Goal

Add `.intunewin` package generation to the .NET CLI.

## Requirements

- Locate IntuneWinAppUtil.exe from:
  - command-line option
  - environment variable
  - tools directory
- Run IntuneWinAppUtil.exe on Windows.
- Use Package.IntuneWin.SetupFile.
- Generate output into the specified package output directory.
- Capture stdout and stderr.
- Fail with useful error messages.
- Compute SHA256 of generated `.intunewin`.
- Add package metadata JSON.

## Acceptance criteria

- `.intunewin` is generated from staged files.
- The generated package includes repository scripts and external binaries.
- CLI fails when SetupFile is missing.
```

### 15.2 Implement Intune Graph create/update flow

```markdown
# Implement Intune Graph create/update flow for Win32 apps

## Goal

Publish generated `.intunewin` packages to Microsoft Intune through Microsoft Graph.

## Requirements

- Authenticate using Azure.Identity.
- Get Graph token for https://graph.microsoft.com/.default.
- Resolve existing app by metadata in notes.
- Fallback to DisplayName.
- Create win32LobApp if not found.
- Update existing app if found.
- Upload `.intunewin` content.
- Commit file.
- Poll upload state.
- Poll publishing state.
- Store management metadata in notes.
- Preserve existing Intune app ID.
- Do not include package version in display name.

## Acceptance criteria

- New app can be created.
- Existing app can be updated.
- Same PackageIdentifier + Platform + Architecture updates the same app.
- Graph request id is logged on failure.
```

### 15.3 Implement assignment merge

```markdown
# Implement Intune app assignment merge

## Goal

Apply assignment definitions from manifests to Intune apps.

## Requirements

- Parse Assignments from manifest.
- Validate GroupId as GUID.
- Support assignment intents:
  - required
  - available
  - uninstall
- Get current Intune app assignments.
- Add missing assignments.
- Keep existing assignments by default.
- Support AssignmentSync: merge.
- Add dry-run diff.

## Acceptance criteria

- App can be assigned to target groups by GUID.
- Existing assignments are not removed in merge mode.
- Dry-run shows assignment diff.
```

### 15.4 Implement GitHub Release and Azure Blob providers

```markdown
# Implement GitHub Release and Azure Blob source providers

## Goal

Add authenticated source providers for private/internal package binaries.

## Requirements

- Implement githubRelease provider.
- Support private GitHub Release asset download.
- Read GitHub token from configured secret/environment variable.
- Implement azureBlob provider.
- Support Azure workload identity authentication.
- Verify SHA256 for all downloaded files.
- Mask credentials in logs.

## Acceptance criteria

- Private GitHub Release asset can be downloaded.
- Azure Blob can be downloaded using federated identity.
- Credentials are not logged.
```

---

## 16. Final recommended implementation order

1. Manifest model / validation
2. Windows staging
3. publicHttp download + SHA256
4. IntuneWinAppUtil integration
5. Graph authentication
6. Intune app resolver
7. Win32 app create/update
8. Assignment merge
9. GitHub Release provider
10. Azure Blob provider
11. Azure Pipelines support
12. macOS support

---

## 17. Notes

.NET で実装する場合の推奨:

- Graph SDK は最初は使わず、`HttpClient` + `Azure.Identity`。
- IntuneWin 生成は Windows runner + `IntuneWinAppUtil.exe`。
- validation は JSON Schema より C# model + `FluentValidation`。
- app identity は `PackageIdentifier + Platform + Architecture`。
- display name は version なし。
- assignment sync は既定 `merge`。

