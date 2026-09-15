# GitHub Actions CI Design: Release Workflow Guidance

This is the English counterpart for the release-workflow guidance in
[03-ci-github-actions.md](03-ci-github-actions.md). The Japanese document remains the complete
GitHub Actions CI design.

## 12a. NuGet Global Tool Release Workflow

Release `relaypublisher` with two workflows that are fully separate from the Intune publishing
workflow:

| Workflow | Trigger | Responsibility |
| --- | --- | --- |
| `.github/workflows/release-draft.yml` | Push of a `v*` tag | Build, test, pack, publish single-file apps, create a **draft** GitHub release with its assets, and repackage the selected `.nupkg` into a per-build preview-version package for Azure Artifacts internal testing. |
| `.github/workflows/release-publish.yml` | `release: [published]` | When a person publishes the draft release, push its reviewed package asset to GitHub Packages and nuget.org. |

nuget.org does not permit deleting a published package version and can only unlist one. Therefore,
publishing a draft GitHub release remains the final human gate for public distribution. Azure
Artifacts is an internal-test feed, so after validating the tag the draft workflow repackages the
selected `.nupkg` by rewriting only its package `<version>` in the `.nuspec` to
`{X.Y.Z}-preview.{yyyyMMddHHmm}.{run_number}.{run_attempt}` before pushing it there.
Dependency `version="..."` attributes are left unchanged.

Each Azure Artifacts push therefore uses a fresh preview version, so rerunning the same tag no
longer collides with an earlier internal-feed version. The official tag version remains unchanged
on the draft release asset and in `.github/workflows/release-publish.yml`, which still pushes the
reviewed official-version package to GitHub Packages and nuget.org.

### Safe draft-workflow reruns

The workflow must not replace assets on an existing draft release. Before retaining the existing
expected package asset, download and expand both packages, then compare normalized package contents
and metadata with the newly produced package. NuGet ZIP entry timestamps can differ between builds
and are excluded from that comparison. If any actual package content or metadata differs, create a
new version tag. When the normalized contents match, the draft workflow uploads a short-lived
`release-package` workflow artifact containing the selected bytes: the freshly attached package for
a new draft, or the existing draft asset bytes for an existing draft.

The Azure Artifacts job must download that `release-package` workflow artifact by exact package
name, then rewrite only the package `<version>` in its `.nuspec` (leaving dependency version
attributes untouched) and push the rebuilt preview package without rebuilding from source. It keeps
only `contents: read` plus `id-token: write`; it must not download the draft release with its
read-only token and must not receive repository write permission alongside Azure credentials.

Never upload replacement assets to a published release. The `release: [published]` event does not
fire again when assets are replaced, which could otherwise leave the release asset inconsistent with
the packages already pushed to public feeds.
