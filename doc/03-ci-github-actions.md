# GitHub Actions Workflow

このドキュメントは 2 種類の workflow を扱う。混同しないこと。

| 区分 | 対象 | 置き場所 |
|---|---|---|
| **Relaypublisher 自身の CI/CD** (§11b, §12a) | このリポジトリのビルド・テスト・リリース | `.github/workflows/` に実在し、実際に動作する |
| **利用者向け Intune publish サンプル** (§12) | manifest を持つ利用者リポジトリ | `workflows/github-actions/publish-intune-apps.yml`。コピーして使う参照サンプル |

## 11a. 3 本共通の方針

- workflow top-level は `permissions: {}`。job ごとに必要最小限の permission だけを与える。
- **`uses:` の参照はすべて 40 桁の commit SHA でピン留めする。** major tag (`@v5` など) は
  上流が同じ tag を別 commit に付け替えられるため、public リポジトリでは supply chain 上のリスクになる。
  `actions/*` も例外にしない。行末コメントに対応する semver を書き、更新時はその両方を差し替える。
  ピン留め対象と現在の版:

  | action | semver | commit SHA |
  |---|---|---|
  | `actions/checkout` | v7.0.1 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
  | `actions/setup-dotnet` | v6.0.0 | `a98b56852c35b8e3190ac28c8c2271da59106c68` |
  | `actions/upload-artifact` | v7.0.1 | `043fb46d1a93c77aae656e7c1c64a875d1fc6a0a` |
  | `azure/login` | v3.0.1 | `f5d393ae46f8fde4be8b75f32e3fc50e654ad0ca` |

- **`actions/checkout` は必ず `persist-credentials: false` を指定する。** 既定の `true` は job token を
  `.git/config` に書き込むため、その後に走る `dotnet build` / `dotnet pack` / `dotnet publish`
  (= tag 時点のリポジトリのビルドコードと NuGet 依存関係)が token を読み出せてしまう。
  とくに `contents: write` を持つ job では、侵害されたビルドターゲットがリポジトリや release を
  書き換えられる経路になる。git 履歴を必要とする検査は git ではなく GitHub API で行う(下記)。
- **外部スクリプトを `curl | sh` でパイプ実行しない。** publishing secrets と OIDC token を持つ job で
  可変リダイレクト(`aka.ms/...`)越しのスクリプトを実行するのは、token 窃取と意図しない publish の
  経路になる。Azure Artifacts credential provider は署名済み NuGet package
  `Microsoft.Artifacts.CredentialProvider.NuGet.Tool` を **version 固定**で
  `dotnet tool install` する。
- **`dotnet nuget push` にワイルドカードを渡さない。** tag から導出した
  `relaypublisher.<version>.nupkg` の**実パス**を指定する。release に別の `.nupkg` が添付されていた
  場合に巻き込みで publish されるのを防ぐ。
- 実 URL / tenant id / feed URL を YAML に直書きしない(AGENTS.md 禁止事項)。secret 経由で渡す。
  GitHub Packages の URL だけは認証なしで公開されている既知 URL なので直書きしてよい。
- secret 由来の値をシェル内で導出した場合は `::add-mask::` でマスクしてからでないと使わない。
- 利用者向け publish workflow で使う `relaypublisher` は、Validate / Package / Publish の全 job で同じ
  exact version を `RELAYPUBLISHER_VERSION` として固定する。version なしの `dotnet tool install` は使用しない。
- PKG の semantic warning を承認する `forceWarnings` は `workflow_dispatch` の boolean input(既定 `false`)だけで
 受け付ける。true の実行は required reviewers を設定した protected `production-force` environment に入り、
  warning 用の `--force` だけを条件付きで追加する。parser error、checksum、metadata、tenant mismatch には
  `--force` を適用しない。

## 11b. CI workflow (PR build / test validation)

`.github/workflows/ci.yml`。main ブランチへの pull request と main への push で動作する。

設計上のポイント:

- trigger は `pull_request` (base: `main`) / `push` (`main`) / `workflow_dispatch`。
  `pull_request_target` は使わない。fork からの PR に secrets と write 権限を渡さないため。
- **secrets を一切参照しない。** リポジトリを public 化すると fork からの PR が走るため、
  CI が secrets を要求する設計だと fork PR が構造的に失敗する。
- workflow top-level は `permissions: {}` とし、job ごとに `contents: read` だけを与える。
- `actions/setup-dotnet` には `global-json-file: global.json` を渡す。SDK バージョンの正本は
  `global.json` (`10.0.100` / `rollForward: latestFeature`) の 1 箇所だけにする。
- job 構成:
  - `build-test`: `ubuntu-latest` / `windows-latest` の matrix (`fail-fast: false`)。
    `dotnet build` → `dotnet test`。trx を `if: always()` で artifact 化する。
  - `package`: `dotnet pack -p:Version=0.0.0-ci.<run_number>` で `.nupkg` を生成し artifact 化する。
    NuGet global tool として pack できることの検証を兼ねる。
  - `single-file`: `win-x64` / `win-arm64` / `osx-arm64` の matrix で
    self-contained single-file app を生成し artifact 化する。
- single-file は native AOT ではないため、3 RID とも `ubuntu-latest` からクロスビルドできる。
- **`PublishTrimmed` は明示的に `false`。** YamlDotNet / FluentValidation / Azure.Identity が
  リフレクションに依存しており、trim すると実行時に落ちる。
- CI 版のバージョンは `0.0.0-ci.<run_number>`。実バージョンの正本は release tag だけ。

導入前 checklist は `doc/05-operation.md` §6 を参照する。

## 12. GitHub Actions example

設計上のポイント:

- `concurrency` で publish を直列化する(並走による app 二重作成防止)。
- `fetch-depth: 0` で checkout し、`plan --base-ref` で changed manifest を確定して `manifest-list.json` を artifact 化する。後続 job は changed を再計算しない。
- `permissions` は job 単位で最小化する。PR で動く job に `id-token: write` を付けない。
- CI 実行時の CLI 呼び出しは `relaypublisher` コマンドに統一し、各 job で
  `dotnet tool install --global relaypublisher --version "$RELAYPUBLISHER_VERSION"` を実行する。
- source provider が使う secrets(GitHub PAT 等)は package job に環境変数で渡す。fork からの PR には secrets が渡らないため、認証が必要な download を含む manifest は PR では dry-run に留める。
- `.intunewin` 生成のみ Windows runner が必要。publish は Graph REST 呼び出しだけなので ubuntu で動かす。
- `workflow_dispatch` の `dryRun` input を publish 実行判定に使う。
- `package` は source SHA256 検証後に macOS PKG inspection を行い、package file と
  `package-metadata.json`(content SHA256、input hash、CLI/inspector version、bundle result、warning code)を
  同一 artifact に含める。`publish` は source を再取得せず artifact を再ハッシュ・再検査し、全件 preflight 完了
  後にだけ Graph write を開始する。

### Package artifact handoff

Windows の package job は manifest が参照する installer input を download し、repository file を staging し、checksum を検証して、`./out` に最終 package を生成する。macOS の package も source SHA256 の後に PKG inspection を行い、同じ出力ディレクトリへ `package-metadata.json` を書く。このディレクトリを `intunewin-packages` artifact として upload する。artifact 内 metadata の `contentSha256`、`inputHash`、`cliVersion`、`inspection` は package file と一緒に保存する。

publish job は `publish` の前に同じ artifact を download する必要があり、manifest set を再構築または再計算しない。`publish` は package file を再ハッシュし、metadata の content SHA256 と比較したうえで、source を再取得せず同じ PKG を再検査する。複数 manifest の検査、metadata 整合性、warning acknowledgement は Graph の app resolve/upload/PATCH より前に全件完了させる。

```yaml
- uses: actions/download-artifact@v4
  with:
    name: manifest-list

- uses: actions/download-artifact@v4
  with:
    name: intunewin-packages
    path: ./out

- run: >
    relaypublisher publish
    --manifest-list manifest-list.json
    --package-dir ./out
    --expected-tenant "<tenant-id>"
```

package job の source provider download は manifest の各 item で制御する。`publicHttp` は匿名、`githubRelease` は `Auth.Type: token` の場合に `Auth.SecretName` が指定する環境変数を読み取り、`azureBlob` は Azure login 後の `DefaultAzureCredential` を利用する。source provider 用 secret は package job にだけ渡す。

実際にコピーして使える参照サンプルは `workflows/github-actions/publish-intune-apps.yml`。対象 repository の
`.github/workflows/publish-intune-apps.yml` にコピーしてから、environment、secrets、OIDC、source provider
の設定を行う。

PowerShell:

```powershell
New-Item -ItemType Directory -Force .github/workflows | Out-Null
Copy-Item workflows/github-actions/publish-intune-apps.yml .github/workflows/publish-intune-apps.yml
```

bash:

```bash
mkdir -p .github/workflows
cp workflows/github-actions/publish-intune-apps.yml .github/workflows/publish-intune-apps.yml
```

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
      forceWarnings:
        description: "Acknowledge semantic PKG inspection warnings (requires production-force approval)."
        required: false
        type: boolean
        default: false
      manifests:
        description: "Explicit manifest paths (space separated). Empty = all manifests."
        required: false
        type: string

concurrency:
  group: intune-publish
  cancel-in-progress: false

permissions: {}

env:
  # Pin this value to the released CLI used by all three jobs and manual E2E.
  RELAYPUBLISHER_VERSION: "1.2.3"

jobs:
  validate:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - run: dotnet build IntuneLobPublisher.slnx --configuration Release

      - run: dotnet test IntuneLobPublisher.slnx --configuration Release --no-build

      - run: |
          dotnet tool install --global relaypublisher --version "$RELAYPUBLISHER_VERSION"
          echo "$HOME/.dotnet/tools" >> "$GITHUB_PATH"

      # changed detection をここで一度だけ確定する。
      # PR: merge-base / push: event.before / dispatch: 明示指定または全件
      - name: Resolve changed manifests
        shell: bash
        run: |
          case "${{ github.event_name }}" in
            pull_request)
              BASE_REF=$(git merge-base "${{ github.event.pull_request.base.sha }}" HEAD)
              ;;
            push)
              BASE_REF="${{ github.event.before }}"
              ;;
            *)
              BASE_REF=""
              ;;
          esac

          ARGS=(plan --output manifest-list.json)
          if [ -n "$BASE_REF" ]; then
            ARGS+=(--base-ref "$BASE_REF")
          fi
          if [ -n "${{ inputs.manifests }}" ]; then
            ARGS+=(--manifests ${{ inputs.manifests }})
          fi

          relaypublisher "${ARGS[@]}"

      - name: Validate
        run: >
          relaypublisher validate --manifest-list manifest-list.json

      - uses: actions/upload-artifact@v4
        with:
          name: manifest-list
          path: manifest-list.json

  package-windows:
    needs: validate
    # PR は認証が必要な download や id-token/secrets を伴う job を実行しない。
    # PR での package 検証が必要な場合は、secrets/OIDC を持たない別 job を dry-run で用意すること。
    if: github.event_name != 'pull_request'
    runs-on: windows-latest
    permissions:
      contents: read
      id-token: write   # ExternalFiles に azureBlob を使う場合のみ必要
    env:
      # githubRelease provider 用。manifest の Auth.SecretName と対応させる。
      GH_RELEASE_PAT: ${{ secrets.GH_RELEASE_PAT }}
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - shell: pwsh
        run: |
          dotnet tool install --global relaypublisher --version $env:RELAYPUBLISHER_VERSION
          Add-Content -Path $env:GITHUB_PATH -Value "$env:USERPROFILE\.dotnet\tools"

      - uses: actions/download-artifact@v4
        with:
          name: manifest-list

      # ExternalFiles に azureBlob を使う場合のみ必要
      - name: Azure login
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - shell: pwsh
        run: >
          relaypublisher package --manifest-list manifest-list.json --output ./out

      - uses: actions/upload-artifact@v4
        with:
          name: intunewin-packages
          path: ./out

  publish:
    needs:
      - validate
      - package-windows
    if: >
      (github.event_name == 'push' && github.ref == 'refs/heads/main') ||
      (github.event_name == 'workflow_dispatch' && inputs.dryRun == false)
    runs-on: ubuntu-latest
    permissions:
      contents: read
      id-token: write
    # production-force is a separately protected environment with required reviewers.
    environment: ${{ inputs.forceWarnings == true && 'production-force' || 'production' }}
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - run: |
          dotnet tool install --global relaypublisher --version "$RELAYPUBLISHER_VERSION"
          echo "$HOME/.dotnet/tools" >> "$GITHUB_PATH"

      - name: Azure login
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - uses: actions/download-artifact@v4
        with:
          name: manifest-list

      - uses: actions/download-artifact@v4
        with:
          name: intunewin-packages
          path: ./out

      - name: Publish after artifact preflight
        shell: bash
        env:
          FORCE_WARNINGS: ${{ inputs.forceWarnings }}
        run: |
          ARGS=(publish --manifest-list manifest-list.json --package-dir ./out --expected-tenant "${{ secrets.AZURE_TENANT_ID }}")
          if [[ "$FORCE_WARNINGS" == "true" ]]; then
            ARGS+=(--force)
          fi
          relaypublisher "${ARGS[@]}"
```

補足:

- `github.event.before` はブランチ新規作成や force push 直後に zero SHA になる。CLI 側で全件 fallback する。
- federated credential の subject claim は `repo:<owner>/<repo>:environment:production` に限定する(`00-overview.md` 6.5)。
- 誤テナント防止のため publish は `--expected-tenant` を必須運用とする。
- `forceWarnings: true` の実行は手動 dispatch に限定し、`production-force` environment の required reviewers
  が承認した場合だけ `--force` を渡す。semantic warning 以外の failure は常に停止する。
- `package-metadata.json` の CLI version と実行時 CLI version が異なる場合は publish を開始しない。artifact の
  content SHA256 は実体から再計算し、inspection 結果も再取得した実体から検証する。

### Protected manual E2E (Intune publish)

CI の build/test だけでは Graph payload、PKG の検出、artifact handoff を保証できないため、別 workflow の
`workflow_dispatch` で protected `intune-e2e` environment を使用する。対象は disposable tenant と専用の
テストグループに限定し、`--expected-tenant` を必須にする。production environment の credential は流用しない。

受入シナリオ:

- deterministic XAR fixture と実際の macOS PKG の両方で bundle inspection を実行し、Windows/Ubuntu runner の
  結果が一致することを確認する。
- pkg と lob の create、update、同一artifactの idempotent rerun を実行し、Graph GETで selected primary、
  `includedApps` / `childApps`、LOB top-level `bundleId`、`buildNumber`、`versionNumber` を確認する。
- 複数bundle warningの拒否、`forceWarnings`承認、checksum改ざん、metadata/CLI version不一致、malformed XAR、
  後段manifestのpreflight拒否を確認し、いずれもGraph writeが発生しないことを確認する。
- E2E成功後は作成したapp、content、assignment、test groupを専用cleanup手順で削除し、削除失敗をworkflow失敗
  として残す。fork PR、通常PR、pushからはこのworkflowを起動しない。

## 12a. NuGet global tool release workflow

`relaypublisher` パッケージのリリースは、Intune publish workflow とは完全に分離した 2 本の workflow で行う。

| workflow | trigger | 役割 |
|---|---|---|
| `.github/workflows/release-draft.yml` | `push` tags `v*` | build / test / pack / single-file publish → **draft** GitHub release を作成し資産を添付する |
| `.github/workflows/release-publish.yml` | `release: [published]` | draft release を人が publish した時点で、その資産を NuGet feed へ push する |

### なぜ 2 本に分けるか

NuGet feed は一度 push した version を削除できない(unlist しかできない)。したがって
「tag を打った瞬間に feed へ公開が確定する」構成は取らず、**draft release を人がレビューして
publish する操作を最後の関門にする**。tag の打ち直しは draft release を消せばやり直せる。

### 配布先 feed

| feed | 想定利用者 | 認証 |
|---|---|---|
| GitHub Packages (このリポジトリ) | リポジトリを直接見ている利用者 | `GITHUB_TOKEN` (`packages: write`) |
| Azure Artifacts | 社内 CI / 閉じたネットワーク | OIDC (workload identity federation) + artifacts-credprovider |
| nuget.org | 一般利用者 | `NUGET_API_KEY` |

### release-draft.yml の設計上のポイント

- trigger は `v*` tag push のみ。job は `verify`(build/test)/ `guard`(provenance)/ `draft-release` の 3 本。
- **`guard` job は read-only で、ビルドコードを一切実行しない。** version 検証と main 到達性検証を
  ここで済ませてから、`contents: write` を持つ `draft-release` job を動かす。
- version は tag から抽出し、`^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$` で検証してから
  `dotnet pack -p:Version=<X.Y.Z>` に渡す。不正な tag 名がそのまま MSBuild に流れるのを防ぐ。
- **tag が main から到達可能であることを GitHub API で検証する。**
  `gh api repos/{owner}/{repo}/compare/main...<sha>` の `status` が `behind` または `identical` の
  ときだけ通す(`behind` = main が既にその commit を含む、`identical` = main の先端)。
  `git merge-base` を使わないのは、そのために `persist-credentials: true` で checkout する必要が
  生じるため。
- pack 対象は `src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj` のみ。
- `dotnet build` / `dotnet test` を ubuntu / windows の matrix で先に通してから pack する。
- 添付する資産: `.nupkg`、3 RID の single-file app zip、`SHA256SUMS.txt`。
- `gh release view` で存在確認してから create / upload を出し分け、同一 tag での再実行を冪等にする。
- **ただし既に publish 済みの release には絶対に upload しない**(`isDraft` を確認して fail させる)。
  publish 済み release に tag を打ち直して資産だけ差し替えると、`release: published` は再発火しないため、
  release に添付された資産と feed に push 済みの package が食い違ったまま公開され続ける。
  その場合は新しい version tag を切る。
- prerelease version (`-` を含む) の場合は `--prerelease` を付ける。
- `contents: write` は draft release 作成に必要。

### release-publish.yml の設計上のポイント

- trigger は `release: [published]`。ただし **この event は repository 全体で発火する**。
  `release-draft.yml` が作った draft に限定されないし、`v*` tag に限定もされない。
  そのため provenance は workflow 側で再検証する。
- **read-only の `guard` job で 3 段の provenance 検証を行い、それが通るまで publishing secrets を
  持つ job を起動しない。**
  1. tag が `v` 前置 + `^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$` に一致すること。
  2. tag が指す commit が main から到達可能であること(annotated tag は dereference してから
     `compare/main...<sha>` を見る)。
  3. release に添付された `.nupkg` が `relaypublisher.<version>.nupkg` **ちょうど 1 個**であること。
     `release-draft.yml` は 1 個しか添付しないため、それ以外は別経路で作られた release を意味する。
- **再ビルドしない。** `gh release download --pattern "relaypublisher.<version>.nupkg"` で
  release に添付された bits をそのまま push する。レビューしたものと publish するものを
  一致させるため。ワイルドカードでは download も push もしない。
- `environment: release` に publishing secrets をスコープする。必要なら required reviewers も付ける。
  event 由来の trigger に対する最終的な人間側のゲートはこの environment protection。
- Azure Artifacts は Microsoft Learn の
  [GitHub Actions → Azure Artifacts quickstart (managed identity)](https://learn.microsoft.com/azure/devops/artifacts/quickstarts/github-actions?view=azure-devops)
  に準拠する。`azure/login` → credential provider install →
  `az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798` →
  `VSS_NUGET_ACCESSTOKEN` / `VSS_NUGET_URI_PREFIXES` を設定 → `dotnet nuget push --api-key AzureDevOps`。
  `499b84ac-1321-427f-aa17-267ca6975798` は Azure DevOps の固定リソース ID。
  ただし credential provider の install だけは Learn の例(`curl ... aka.ms/... | sh`)を採らず、
  署名済み NuGet package `Microsoft.Artifacts.CredentialProvider.NuGet.Tool` を version 固定で
  `dotnet tool install` する(§11a の共通方針)。
- **feed URL は secret** (`AZURE_ARTIFACTS_FEED_URL`)。`VSS_NUGET_URI_PREFIXES` はその場で導出し、
  `::add-mask::` でマスクしてからログに出さないようにする。取得した access token も同様にマスクする。
- 3 feed とも `--skip-duplicate` を付け、release publish のやり直しを冪等にする。
- 3 feed の push step は独立させる。どれか 1 つが失敗したら job は失敗する
  (`continue-on-error` は使わない)。

### 必要な secrets (environment `release`)

| 名前 | 用途 |
|---|---|
| `AZURE_ARTIFACTS_FEED_URL` | Azure Artifacts feed の v3 index URL |
| `AZURE_ARTIFACTS_CLIENT_ID` | user-assigned managed identity / app registration の client id |
| `AZURE_ARTIFACTS_TENANT_ID` | tenant id |
| `AZURE_ARTIFACTS_SUBSCRIPTION_ID` | subscription id |
| `NUGET_API_KEY` | nuget.org の package publish 権限のみを持つ API key |

`GITHUB_TOKEN` は自動供給される。Intune publish 用の `AZURE_CLIENT_ID` 等と名前空間を分けるため
`AZURE_ARTIFACTS_` prefix を付けている。

補足:

- Azure Artifacts 側の事前セットアップ(managed identity 作成、federated credential 設定、
  Azure DevOps プロジェクトの Contributors への追加)は `doc/05-operation.md` §6 を参照する。
- NuGet Trusted Publishing (OIDC) を使う場合は、nuget.org 向け push step を trusted publishing 用手順に置き換える。
- single-file app には署名・notarization を行わない。macOS では Gatekeeper の警告が出る。

---
