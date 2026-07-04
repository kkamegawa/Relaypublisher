# Add IntuneWinAppUtil integration to .NET CLI

## Goal

Add `.intunewin` package generation to the .NET CLI.

## Requirements

- Locate IntuneWinAppUtil.exe from:
  - command-line option
  - environment variable
  - tools directory
- The tool version can be pinned in configuration or via command-line option.
- When no version is pinned, download the latest release from the official GitHub repository (`microsoft/Microsoft-Win32-Content-Prep-Tool`).
- When a pinned version has a configured known-good SHA256, verify the downloaded tool against it. Fail on mismatch (supply chain protection: this tool touches every package payload).
- When the latest release is fetched (no known-good hash available), compute and record the downloaded tool's SHA256 in package metadata for auditability.
- Run IntuneWinAppUtil.exe on Windows.
- Use Package.IntuneWin.SetupFile.
- Generate output into the specified package output directory.
- Capture stdout and stderr.
- Fail with useful error messages.
- Compute a deterministic input hash:
  - SHA256 over the normalized manifest hash plus each staged input file's relative path and SHA256, sorted by path.
  - Note: the generated `.intunewin` itself is NOT deterministic (random encryption key per run), so its hash must not be used for identity or skip decisions.
- Add package metadata JSON including:
  - input hash
  - tool version and tool SHA256
  - generated `.intunewin` SHA256 (informational only)

## Acceptance criteria

- `.intunewin` is generated from staged files.
- The generated package includes repository scripts and external binaries.
- CLI fails when SetupFile is missing.
- When no version is pinned, the CLI downloads the latest release from GitHub and records its version and SHA256.
- CLI fails when the tool hash does not match the configured known-good hash for a pinned version.
- Package metadata JSON contains the deterministic input hash and tool version/hash.
- The same staged input produces the same input hash across runs.
