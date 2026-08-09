# Implement .NET CLI foundation for Intune LOB app publisher

## Goal

Create a .NET CLI tool that validates winget-like YAML manifests and stages Windows Win32 app packages.

The staging process must combine repository scripts and externally downloaded binaries into a single directory that can later be converted to `.intunewin`.

This issue focuses on the foundation only. Actual Microsoft Intune upload, Microsoft Graph integration, assignment API calls, Azure Blob downloads, GitHub Release downloads, macOS support, and actual `.intunewin` generation are out of scope for this issue.

## Technology

Use the following technologies:

- .NET 10
- C#
- System.CommandLine
- YamlDotNet
- FluentValidation
- MSTest
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
relaypublisher validate --manifest manifests/Contoso.Tool.yaml
```

Also support glob-style or directory-based input if practical:

```bash
relaypublisher validate --manifest manifests/**/*.yaml
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
relaypublisher package --manifest manifests/Contoso.Tool.yaml --output ./out
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
relaypublisher publish --manifest manifests/Contoso.Tool.yaml
```

Behavior:

- Print a clear message that publish is not implemented yet.
- Return non-zero exit code or dedicated `NotImplemented` result.

### plan

Resolves the target manifest set and writes it to a JSON file so later commands (and later CI jobs) reuse the same set instead of recomputing it.

Example:

```bash
relaypublisher plan --base-ref <sha> --output manifest-list.json
```

Command options:

- `--base-ref`
  - Git ref/sha to diff against. Changed manifests are those modified since this ref; changes under `scripts/**` map back to the manifests that reference them.
  - When missing or unresolvable (e.g. zero SHA), fall back to all manifests.
- `--manifests`
  - Optional explicit manifest paths; overrides diff-based detection.
- `--output`
  - Output path for `manifest-list.json`.

`validate` and `package` accept `--manifest-list <file>` as an alternative to `--manifest`.

## Manifest schema

Support the following top-level YAML fields.

```yaml
SchemaVersion: "1.0"
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
      - Target: group
        GroupId: "00000000-0000-0000-0000-000000000001"
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

- `SchemaVersion`
  - Fail on unknown major version.
- `PackageIdentifier`
- `PackageName`
- `Publisher`
- `Description`
- `PackageVersion`
- `Apps`

Optional:

- `AssignmentSync`
- `Owner`, `Developer`, `InformationUrl`, `Icon`, `RoleScopeTagIds`
  - For this issue, only parse them. `Icon` must be a repository-relative path when present.

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

Validate each assignment (extended model, see `01-manifest-schema.md` 5.5):

- `Target` must be one of `group` (default), `allDevices`, `allLicensedUsers`.
- `GroupId` must be a valid GUID when `Target: group`; must be absent otherwise.
- `Mode` must be `include` (default) or `exclude`.
- `Intent` must be one of:
  - `required`
  - `available`
  - `uninstall`
- `FilterMode` (`include` | `exclude`) is required when `FilterId` is set.
- Duplicate targets within one app entry fail validation.

For this issue, only parse and validate assignments.
Do not call Intune assignment APIs.

### Repository-wide uniqueness lint

Across all manifests passed to `validate`:

- `PackageIdentifier + Platform + Architecture` must be unique (ignoring version folders of the same package).
- `DisplayName` must be unique.

These are the app-identity resolution keys; duplicates fail validation.

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
- `Sha256` is required (all source types).
- Parse the unified `Auth` block (`Type`: `none` | `token` | `workloadIdentity`, `SecretName`) but for this issue only `Type: none` / omitted is executable.

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

Add MSTest tests under:

```text
tests/IntuneLobPublisher.Core.Tests
```

### Manifest validation tests

Add tests for:

- Valid Windows x64 manifest passes.
- Valid Windows Arm64 manifest passes.
- Missing `SchemaVersion` fails.
- Unknown `SchemaVersion` major fails.
- Missing `PackageIdentifier` fails.
- Duplicate `PackageIdentifier + Platform + Architecture` across manifests fails.
- Duplicate `DisplayName` across manifests fails.
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
