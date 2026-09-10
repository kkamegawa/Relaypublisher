# GitHub Actions CI Design: Release Workflow Guidance

This is the English counterpart for the release-workflow guidance in
[03-ci-github-actions.md](03-ci-github-actions.md). The Japanese document remains the complete
GitHub Actions CI design.

## 12a. NuGet Global Tool Release Workflow

Release `relaypublisher` with two workflows that are fully separate from the Intune publishing
workflow:

| Workflow | Trigger | Responsibility |
| --- | --- | --- |
| `.github/workflows/release-draft.yml` | Push of a `v*` tag | Build, test, pack, publish single-file apps, create a **draft** GitHub release with its assets, and push the exact same `.nupkg` to Azure Artifacts for internal testing. |
| `.github/workflows/release-publish.yml` | `release: [published]` | When a person publishes the draft release, push its reviewed package asset to GitHub Packages and nuget.org. |

nuget.org does not permit deleting a published package version and can only unlist one. Therefore,
publishing a draft GitHub release remains the final human gate for public distribution. Azure
Artifacts is an internal-test feed, so the draft workflow pushes the release asset there after
validating the tag.

An Azure Artifacts package can be deleted into the recycle bin, but its version identifier remains
permanently reserved and cannot be republished. If a draft workflow may have reached the Azure
Artifacts push, do not reuse that version tag after deleting the draft release; create a new version
tag instead. The draft workflow may be rerun with the same version tag only when the normalized
package contents and metadata are unchanged. `--skip-duplicate` retains the already-published
package bytes; it does not allow different package content to be republished under the same version.

### Safe draft-workflow reruns

The workflow must not replace assets on an existing draft release. Before retaining the existing
expected package asset, download and expand both packages, then compare normalized package contents
and metadata with the newly produced package. NuGet ZIP entry timestamps can differ between builds
and are excluded from that comparison. If any actual package content or metadata differs, create a
new version tag. When the normalized contents match, the draft workflow uploads a short-lived
`release-package` workflow artifact containing the selected bytes: the freshly attached package for
a new draft, or the existing draft asset bytes for an existing draft.

The Azure Artifacts job must download that `release-package` workflow artifact by exact package
name and push it without rebuilding. It keeps only `contents: read` plus `id-token: write`; it must
not download the draft release with its read-only token and must not receive repository write
permission alongside Azure credentials.

Never upload replacement assets to a published release. The `release: [published]` event does not
fire again when assets are replaced, which could otherwise leave the release asset inconsistent with
the packages already pushed to public feeds.
