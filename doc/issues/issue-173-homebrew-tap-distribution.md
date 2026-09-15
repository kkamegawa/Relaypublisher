# Distribute relaypublisher on macOS through a Homebrew tap (GitHub #173)

## Summary

`relaypublisher` is distributed on macOS (Apple silicon) through a Homebrew tap owned by this project,
`kkamegawa/homebrew-tap`, in addition to the NuGet global tool. It is not submitted to homebrew-core or
homebrew-cask. The formula installs the `relaypublisher-<version>-osx-arm64.zip` asset that
`release-draft.yml` already attaches to every GitHub release, so no binary is built for Homebrew. See
`doc/00-overview.md` 6.17 for the invariants and `doc/adr/ci-release.md` (2026-09-15 entry) for the
reasoning.

- A Formula, not a Cask: Homebrew does not quarantine formula downloads, while cask downloads are
  quarantined and Homebrew 5.0 removed `--no-quarantine`. The single-file app carries only the .NET
  SDK's ad-hoc signature; it is not signed with a Developer ID and not notarized.
- Apple silicon only (`depends_on arch: :arm64`). Intel macOS dropped to Homebrew Tier 3 in 7.0.
- The .NET 10 SDK ad-hoc signs osx-arm64 single-file bundles on non-macOS hosts, so the ubuntu build is
  unchanged; the tap CI verifies the signature.
- Stable releases only. `release-publish.yml` opens a pull request against the tap, and a human merges
  it after the tap CI passes.

## Required work

- Update `doc/00-overview.md` 6.17, `doc/03-ci-github-actions.md` 12a, `doc/adr/ci-release.md` and the
  `doc/adr.md` index, and point `doc/issues/issue-019` at this issue.
- Add `tools/New-HomebrewFormula.ps1` (PowerShell 7.3+), which renders `Formula/relaypublisher.rb` from a
  stable version, the release's `SHA256SUMS.txt`, and the repository name, and rejects prereleases,
  malformed repositories, and missing, duplicated, or malformed checksum lines. Add
  `tests/Tools/HomebrewFormula.Tests.ps1` and run it in `ci.yml`.
- Extend `release-publish.yml`: `guard` outputs `stable` and `mac-archive`; a new `update-homebrew-tap`
  job, independent of `push-packages`, checks that exactly one archive and one `SHA256SUMS.txt` are
  attached, verifies the archive hash, renders the formula, and opens or refreshes the tap pull request
  with a GitHub App token scoped to the tap.
- Prepare the tap repository: `Formula/relaypublisher.rb` (first version: v1.1.1), a `brew test-bot`
  workflow that also runs `codesign --verify --strict` and `relaypublisher --version` on an Apple silicon
  runner, `README.md` / `README_ja.md`, `LICENSE` (MIT), and `SECURITY.md`.
- Document installing, upgrading, pinning, and uninstalling with Homebrew in `README.md` / `README_ja.md`
  and `doc/05-operation.md` / `_ja`.

## Acceptance criteria

- `pwsh -NoProfile -File ./tests/Tools/HomebrewFormula.Tests.ps1` passes locally and in CI.
- The tap CI installs the formula on an Apple silicon runner, `codesign --verify --strict` passes, and
  `relaypublisher --version` prints the release version.
- Publishing a stable release opens a pull request against the tap; publishing a prerelease skips the
  job. Rerunning the job does not open a second pull request, and a release older than the version the
  tap already pins opens no downgrade pull request.
- On an Apple silicon Mac, `brew tap kkamegawa/tap`, `brew trust --tap kkamegawa/tap`, and
  `brew install relaypublisher` install a working CLI, and `brew upgrade relaypublisher` picks up the
  next release after its tap pull request is merged.

## Manual setup

The repository owner creates the tap repository, a GitHub App installed only on it (Contents and Pull
requests: read and write), the `HOMEBREW_TAP_APP_CLIENT_ID` / `HOMEBREW_TAP_APP_PRIVATE_KEY` secrets in
the `release` environment, and a branch protection rule on the tap that requires the `brew test-bot`
check. See `doc/03-ci-github-actions.md` 12a.

## Out of scope

Intel macOS (`osx-x64`), Homebrew on Linux, homebrew-core / homebrew-cask submission, and Developer ID
code signing or notarization of the single-file apps.

## Dependencies

Builds on the release workflows from `doc/issues/issue-019-nuget-global-tool-distribution.md`.
