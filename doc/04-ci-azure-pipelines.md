# Azure Pipelines Workflow

## 13. Azure Pipelines example

設計上のポイント:

- path filter はディレクトリ名で指定する(`manifests/*` のような wildcard は再帰マッチが保証されない)。
- Windows パスを YAML の double quote で囲むと `\o` などが不正エスケープになる。single quote または `/` 区切りを使う。
- changed detection は Validate stage で一度だけ確定し、`manifest-list.json` を artifact で後続 stage に渡す。
- production environment には **Exclusive Lock** check を設定し、publish を直列化する(並走による app 二重作成防止)。
- source provider 用 secrets は variable group / secret variable から環境変数として明示的にマップする。
- CI 実行時の CLI 呼び出しは `relaypublisher` コマンドに統一し、各 job で `dotnet tool install --global relaypublisher` を実行する。
- publish は Graph REST 呼び出しのみなので ubuntu で動かす。

### Package artifact handoff

Windows の package job は manifest が参照する installer input を download し、file を staging し、checksum を検証して、artifact staging directory に最終 package を生成する。このディレクトリを `intunewin-packages` として publish する。publish stage は `manifest-list` と `intunewin-packages` の両方を download し、manifest set を再計算せず、download した package directory を `--package-dir` に渡す。

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
          - script: dotnet tool install --global relaypublisher
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
          - script: dotnet tool install --global relaypublisher
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
        environment: production
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
                      dotnet tool install --global relaypublisher
                      relaypublisher publish \
                        --manifest-list '$(Pipeline.Workspace)/manifest-list/manifest-list.json' \
                        --package-dir '$(Pipeline.Workspace)/intunewin-packages' \
                        --expected-tenant '<tenant-id>'
```

補足:

- `System.PullRequest.TargetCommitId` は PR ビルドでのみ設定される。push ビルドでは CLI 側で直前コミットとの diff または全件 fallback を使う。
- `--expected-tenant` の `<tenant-id>` は variable group から渡す(placeholder のまま commit しない)。

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
