# GitHub Actions Workflow

## 12. GitHub Actions example

設計上のポイント:

- `concurrency` で publish を直列化する(並走による app 二重作成防止)。
- `fetch-depth: 0` で checkout し、`plan --base-ref` で changed manifest を確定して `manifest-list.json` を artifact 化する。後続 job は changed を再計算しない。
- `permissions` は job 単位で最小化する。PR で動く job に `id-token: write` を付けない。
- CI 実行時の CLI 呼び出しは `relaypublisher` コマンドに統一し、各 job で `dotnet tool install --global relaypublisher` を実行する。
- source provider が使う secrets(GitHub PAT 等)は package job に環境変数で渡す。fork からの PR には secrets が渡らないため、認証が必要な download を含む manifest は PR では dry-run に留める。
- `.intunewin` 生成のみ Windows runner が必要。publish は Graph REST 呼び出しだけなので ubuntu で動かす。
- `workflow_dispatch` の `dryRun` input を publish 実行判定に使う。

実際にコピーして使えるサンプルは `workflows/github-actions/publish-intune-apps.yml` を参照。

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
      manifests:
        description: "Explicit manifest paths (space separated). Empty = all manifests."
        required: false
        type: string

concurrency:
  group: intune-publish
  cancel-in-progress: false

permissions: {}

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

      - run: dotnet tool install --global relaypublisher

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
        run: dotnet tool install --global relaypublisher

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
    environment: production
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - run: dotnet tool install --global relaypublisher

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

      - run: >
          relaypublisher publish
          --manifest-list manifest-list.json
          --package-dir ./out
          --expected-tenant "${{ secrets.AZURE_TENANT_ID }}"
```

補足:

- `github.event.before` はブランチ新規作成や force push 直後に zero SHA になる。CLI 側で全件 fallback する。
- federated credential の subject claim は `repo:<owner>/<repo>:environment:production` に限定する(`00-overview.md` 6.5)。
- 誤テナント防止のため publish は `--expected-tenant` を必須運用とする。

## 12a. NuGet global tool release workflow

`relaypublisher` を `nuget.org` に公開する workflow は、Intune publish workflow と分離する。
実際にコピーして使えるサンプルは `workflows/github-actions/release-nuget-tool.yml` を参照。

設計上のポイント:

- trigger は `v*` tag push のみ(`v1.2.3` 形式)。
- version は tag から抽出し、`dotnet pack -p:Version=<X.Y.Z>` で注入する。
- pack 対象は `src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj` のみ。
- publish step は `--skip-duplicate` を付け、再実行可能にする。
- `dotnet build` / `dotnet test` を先に通してから publish する。

```yaml
name: Release NuGet Tool

on:
  push:
    tags:
      - "v*"

permissions:
  contents: read

jobs:
  release-nuget:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - name: Build and test
        run: |
          dotnet build IntuneLobPublisher.slnx --configuration Release
          dotnet test IntuneLobPublisher.slnx --configuration Release --no-build

      - name: Pack global tool
        shell: bash
        run: |
          VERSION="${GITHUB_REF_NAME#v}"
          dotnet pack src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj \
            --configuration Release \
            -p:ContinuousIntegrationBuild=true \
            -p:Version="$VERSION" \
            --output ./artifacts/nuget

      - name: Publish to nuget.org
        run: |
          dotnet nuget push ./artifacts/nuget/*.nupkg \
            --source https://api.nuget.org/v3/index.json \
            --api-key "${{ secrets.NUGET_API_KEY }}" \
            --skip-duplicate
```

補足:

- `NUGET_API_KEY` は package publish 権限のみを持つ key を使う。
- NuGet Trusted Publishing(OIDC)を使う場合は、上記 publish step を trusted publishing 用手順に置き換える。

---
