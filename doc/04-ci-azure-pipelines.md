# Azure Pipelines Workflow

## 13. Azure Pipelines example

設計上のポイント:

- path filter はディレクトリ名で指定する(`manifests/*` のような wildcard は再帰マッチが保証されない)。
- Windows パスを YAML の double quote で囲むと `\o` などが不正エスケープになる。single quote または `/` 区切りを使う。
- changed detection は Validate stage で一度だけ確定し、`manifest-list.json` を artifact で後続 stage に渡す。
- production environment には **Exclusive Lock** check を設定し、publish を直列化する(並走による app 二重作成防止)。
- source provider 用 secrets は variable group / secret variable から環境変数として明示的にマップする。
- CI 実行時の CLI 呼び出しは `relaypublisher` コマンドに統一し、全 stage/job で同じ
  `RELAYPUBLISHER_VERSION` を使って `dotnet tool install --global relaypublisher --version ...` を実行する。
- publish は Graph REST 呼び出しのみなので ubuntu で動かす。
- PKG の semantic warning を承認する `forceWarnings` は manual run の boolean parameter(既定 `false`)だけで
  受け付ける。true の場合は required reviewers を設定した protected `production-force` environment を通過した
  ときだけ `--force` を渡す。parser error、checksum、metadata、tenant mismatch は常に fail させる。

### Package artifact handoff

Windows の package job は manifest が参照する installer input を download し、file を staging し、checksum を検証して、artifact staging directory に最終 package を生成する。macOS package は source SHA256 成功後に PKG inspection を実行し、`package-metadata.json`(content SHA256、input hash、CLI/inspector version、bundle result、warning code)を同じ directory に保存する。このディレクトリを `intunewin-packages` として publish する。publish stage は `manifest-list` と `intunewin-packages` の両方を download し、manifest set を再計算せず、download した package directory を `--package-dir` に渡す。

`publish` は source を再ダウンロードせず artifact の実体を再ハッシュ・再検査し、metadata と一致することを確認する。複数 manifest の checksum、metadata、inspection、warning acknowledgement は全件 preflight で完了してから Graph write を開始する。

```yaml
- download: current
  artifact: intunewin-packages

- script: >
    relaypublisher publish
    --manifest-list '$(Pipeline.Workspace)/manifest-list/manifest-list.json'
    --package-dir '$(Pipeline.Workspace)/intunewin-packages'
    --expected-tenant '<tenant-id>'
```

package input の download は manifest の各 item で制御する。`publicHttp` は匿名、`githubRelease` は `Auth.Type: token` の場合に `Auth.SecretName` が指定する環境変数を読み取り、`azureBlob` は Azure login 後の `DefaultAzureCredential` を利用する。source provider 用 secret は package job にだけ map する。

manifest が `azureBlob` を使う場合、Package stage は `relaypublisher package` の前に Azure CLI login を実行する必要がある。別 job である publish stage の login は package job には引き継がれない。workload identity service connection を使う `AzureCLI@2` の login step を Package job に追加し、package storage scope に `Storage Blob Data Reader` を付与する。

```yaml
- task: AzureCLI@2
  inputs:
    azureSubscription: '<workload-identity-service-connection-name>'
    scriptType: bash
    scriptLocation: inlineScript
    inlineScript: az account show
```

実際にコピーして使える参照サンプルは `workflows/azure-pipelines/azure-pipelines.yml`。この repository では
有効化されないため、対象 repository の root に `azure-pipelines.yml` としてコピーして使用する。導入前の
variable、service connection、Exclusive Lock、source provider の確認は `doc/05-operation.md` §6 を参照する。

PowerShell:

```powershell
Copy-Item workflows/azure-pipelines/azure-pipelines.yml azure-pipelines.yml
```

bash:

```bash
cp workflows/azure-pipelines/azure-pipelines.yml azure-pipelines.yml
```

```yaml
parameters:
  - name: relaypublisherVersion
    type: string
    default: '1.2.3'
  - name: forceWarnings
    displayName: 'Acknowledge semantic PKG inspection warnings'
    type: boolean
    default: false

variables:
  # production-force must have required reviewers; production retains the normal approval policy.
  ${{ if eq(parameters.forceWarnings, true) }}:
    publishEnvironment: production-force
  ${{ else }}:
    publishEnvironment: production

trigger:
  branches:
    include:
      - main
  paths:
    include:
      - manifests
      - scripts
      - src

pr:
  paths:
    include:
      - manifests
      - scripts
      - src

stages:
  - stage: Validate
    jobs:
      - job: Validate
        pool:
          vmImage: ubuntu-latest
        steps:
          - checkout: self
            fetchDepth: 0
          - task: UseDotNet@2
            inputs:
              packageType: sdk
              version: 10.0.x
          - script: dotnet build IntuneLobPublisher.slnx --configuration Release
          - script: dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
          - script: dotnet tool install --global relaypublisher --version '${{ parameters.relaypublisherVersion }}'
          # changed detection をここで一度だけ確定する
          - script: >
              relaypublisher plan
              --base-ref "$(System.PullRequest.TargetCommitId)"
              --output '$(Build.ArtifactStagingDirectory)/manifest-list.json'
          - script: >
              relaypublisher validate --manifest-list '$(Build.ArtifactStagingDirectory)/manifest-list.json'
          - publish: '$(Build.ArtifactStagingDirectory)/manifest-list.json'
            artifact: manifest-list

  - stage: Package
    dependsOn: Validate
    # PR ビルドは secrets を持つ variable group を読ませない(認証が必要な download は本番ブランチでのみ実行する)。
    condition: and(succeeded(), ne(variables['Build.Reason'], 'PullRequest'))
    jobs:
      - job: PackageWindows
        pool:
          vmImage: windows-latest
        variables:
          # githubRelease provider 用。manifest の Auth.SecretName と対応させる
          - group: intune-publisher-secrets
        steps:
          - checkout: self
          - task: UseDotNet@2
            inputs:
              packageType: sdk
              version: 10.0.x
          - script: dotnet tool install --global relaypublisher --version '${{ parameters.relaypublisherVersion }}'
          - download: current
            artifact: manifest-list
          # AzureCLI@2 establishes the workload-identity CLI session used by DefaultAzureCredential
          # when a manifest downloads an azureBlob source during packaging.
          - task: AzureCLI@2
            inputs:
              azureSubscription: '<workload-identity-service-connection-name>'
              scriptType: pscore
              scriptLocation: inlineScript
              inlineScript: |
                relaypublisher package `
                  --manifest-list '$(Pipeline.Workspace)/manifest-list/manifest-list.json' `
                  --output '$(Build.ArtifactStagingDirectory)/out'
            env:
              GH_RELEASE_PAT: $(GH_RELEASE_PAT)
          - publish: '$(Build.ArtifactStagingDirectory)/out'
            artifact: intunewin-packages

  - stage: Publish
    dependsOn: Package
    condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))
    jobs:
      # production environment に Exclusive Lock check を設定して直列化すること
      - deployment: PublishToIntune
        environment: $(publishEnvironment)
        pool:
          vmImage: ubuntu-latest
        strategy:
          runOnce:
            deploy:
              steps:
                - checkout: self
                - download: current
                  artifact: manifest-list
                - download: current
                  artifact: intunewin-packages
                - task: UseDotNet@2
                  inputs:
                    packageType: sdk
                    version: 10.0.x
                - task: AzureCLI@2
                  inputs:
                    azureSubscription: '<workload-identity-service-connection-name>'
                    scriptType: bash
                    scriptLocation: inlineScript
                    inlineScript: |
                      dotnet tool install --global relaypublisher --version '${{ parameters.relaypublisherVersion }}'
                      args=(publish \
                        --manifest-list '$(Pipeline.Workspace)/manifest-list/manifest-list.json' \
                        --package-dir '$(Pipeline.Workspace)/intunewin-packages' \
                        --expected-tenant '<tenant-id>')
                      if [ '${{ parameters.forceWarnings }}' = 'True' ]; then
                        args+=(--force)
                      fi
                      relaypublisher "${args[@]}"
```

補足:

- `System.PullRequest.TargetCommitId` は PR ビルドでのみ設定される。push ビルドでは CLI 側で直前コミットとの diff または全件 fallback を使う。
- `--expected-tenant` の `<tenant-id>` は variable group から渡す(placeholder のまま commit しない)。
- `forceWarnings: true` は manual run に限定し、`production-force` environment の required reviewers が承認した
  場合だけ semantic warning 用 `--force` を渡す。通常の CI は `production` を使い、hard error は常に fail する。
- `package-metadata.json` の CLI version と Publish stage の CLI version が一致しない場合は publish を開始しない。
  artifact の content SHA256 は実体から再計算し、source provider の再ダウンロードは行わない。

### Protected manual E2E

build/test stage とは分離した manual E2E stage を `intune-e2e` environment に配置する。専用の disposable tenant、
test group、workload identity を使用し、`--expected-tenant` を必須にする。production credential を使わず、PR
triggerから起動しない。

受入シナリオ:

- deterministic XAR fixture と実際の macOS PKG で inspection 結果が Windows/Ubuntu runner 間で一致すること。
- pkg / lob の create、update、idempotent rerun と Graph read-back。selected primary、LOB top-level `bundleId`、
  `buildNumber`、`versionNumber`、`includedApps` / `childApps` を検証すること。
- warning拒否、protected `forceWarnings`、checksum/metadata tamper、CLI version不一致、malformed XAR、後段
  preflight拒否で Graph write が0件であること。
- 成功後の app/content/assignment/test group cleanup。cleanup失敗を手動E2Eの失敗として扱うこと。

## 13a. NuGet global tool release (optional)

GitHub Actions を使わず Azure Pipelines で `relaypublisher` を `nuget.org` に公開する場合は、Intune publish pipeline と分離した release pipeline を用意する。
NuGet release の参照サンプルは `workflows/azure-pipelines/release-nuget-tool.yml`。対象 repository の
pipeline 定義として登録し、NuGet push 用 secret を設定してから使用する。

設計上のポイント:

- trigger は `v*` tag のみ。
- version は tag から取り出し、`dotnet pack -p:Version=<X.Y.Z>` で注入する。
- `dotnet build` / `dotnet test` を通した後に `dotnet nuget push --skip-duplicate` を実行する。
- API key は secret variable で保持し、publish stage 以外に公開しない。

```yaml
trigger:
  tags:
    include:
      - v*

stages:
  - stage: ReleaseNuGet
    jobs:
      - job: PackAndPush
        pool:
          vmImage: ubuntu-latest
        steps:
          - checkout: self
            fetchDepth: 0
          - task: UseDotNet@2
            inputs:
              packageType: sdk
              version: 10.0.x
          - script: dotnet build IntuneLobPublisher.slnx --configuration Release
          - script: dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
          - script: |
              VERSION="${BUILD_SOURCEBRANCHNAME#v}"
              dotnet pack src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj \
                --configuration Release \
                -p:ContinuousIntegrationBuild=true \
                -p:Version="$VERSION" \
                --output '$(Build.ArtifactStagingDirectory)/nuget'
          - script: |
              dotnet nuget push '$(Build.ArtifactStagingDirectory)/nuget/*.nupkg' \
                --source https://api.nuget.org/v3/index.json \
                --api-key '$(NUGET_API_KEY)' \
                --skip-duplicate
```

---
