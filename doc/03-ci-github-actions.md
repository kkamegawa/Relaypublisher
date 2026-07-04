# GitHub Actions Workflow

## 12. GitHub Actions example

設計上のポイント:

- `concurrency` で publish を直列化する(並走による app 二重作成防止)。
- `fetch-depth: 0` で checkout し、`plan --base-ref` で changed manifest を確定して `manifest-list.json` を artifact 化する。後続 job は changed を再計算しない。
- `permissions` は job 単位で最小化する。PR で動く job に `id-token: write` を付けない。
- source provider が使う secrets(GitHub PAT 等)は package job に環境変数で渡す。fork からの PR には secrets が渡らないため、認証が必要な download を含む manifest は PR では dry-run に留める。
- `.intunewin` 生成のみ Windows runner が必要。publish は Graph REST 呼び出しだけなので ubuntu で動かす。
- `workflow_dispatch` の `dryRun` input を publish 実行判定に使う。

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
          dotnet-version: "9.0.x"

      - run: dotnet build IntuneLobPublisher.sln --configuration Release

      - run: dotnet test IntuneLobPublisher.sln --configuration Release --no-build

      # changed detection をここで一度だけ確定する。
      # PR: merge-base / push: event.before / dispatch: 明示指定または全件
      - name: Resolve changed manifests
        run: >
          dotnet run --project src/IntuneLobPublisher.Cli --no-build --configuration Release --
          plan
          --base-ref "${{ github.event.pull_request.base.sha || github.event.before }}"
          --manifests "${{ inputs.manifests }}"
          --output manifest-list.json

      - name: Validate
        run: >
          dotnet run --project src/IntuneLobPublisher.Cli --no-build --configuration Release --
          validate --manifest-list manifest-list.json

      - uses: actions/upload-artifact@v4
        with:
          name: manifest-list
          path: manifest-list.json

  package-windows:
    needs: validate
    runs-on: windows-latest
    permissions:
      contents: read
      id-token: write   # ExternalFiles に azureBlob を使う場合のみ必要
    env:
      # githubRelease provider 用。manifest の Auth.SecretName と対応させる。
      # fork からの PR では secrets が渡らないため download は失敗する(dry-run 推奨)。
      GH_RELEASE_PAT: ${{ secrets.GH_RELEASE_PAT }}
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "9.0.x"

      - uses: actions/download-artifact@v4
        with:
          name: manifest-list

      # ExternalFiles に azureBlob を使う場合のみ必要
      - name: Azure login
        if: github.event_name != 'pull_request'
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - run: >
          dotnet run --project src/IntuneLobPublisher.Cli --configuration Release --
          package --manifest-list manifest-list.json --output ./out

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
          dotnet-version: "9.0.x"

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
          dotnet run --project src/IntuneLobPublisher.Cli --configuration Release --
          publish
          --manifest-list manifest-list.json
          --package-dir ./out
          --expected-tenant "${{ secrets.AZURE_TENANT_ID }}"
```

補足:

- `github.event.before` はブランチ新規作成や force push 直後に zero SHA になる。CLI 側で全件 fallback する。
- federated credential の subject claim は `repo:<owner>/<repo>:environment:production` に限定する(`00-overview.md` 6.5)。
- 誤テナント防止のため publish は `--expected-tenant` を必須運用とする。

---
