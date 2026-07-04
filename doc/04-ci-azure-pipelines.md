# Azure Pipelines Workflow

## 13. Azure Pipelines example

設計上のポイント:

- path filter はディレクトリ名で指定する(`manifests/*` のような wildcard は再帰マッチが保証されない)。
- Windows パスを YAML の double quote で囲むと `\o` などが不正エスケープになる。single quote または `/` 区切りを使う。
- changed detection は Validate stage で一度だけ確定し、`manifest-list.json` を artifact で後続 stage に渡す。
- production environment には **Exclusive Lock** check を設定し、publish を直列化する(並走による app 二重作成防止)。
- source provider 用 secrets は variable group / secret variable から環境変数として明示的にマップする。
- publish は Graph REST 呼び出しのみなので ubuntu で動かす。

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
              version: 9.0.x
          - script: dotnet build IntuneLobPublisher.sln --configuration Release
          - script: dotnet test IntuneLobPublisher.sln --configuration Release --no-build
          # changed detection をここで一度だけ確定する
          - script: >
              dotnet run --project src/IntuneLobPublisher.Cli --no-build --configuration Release --
              plan
              --base-ref "$(System.PullRequest.TargetCommitId)"
              --output '$(Build.ArtifactStagingDirectory)/manifest-list.json'
          - script: >
              dotnet run --project src/IntuneLobPublisher.Cli --no-build --configuration Release --
              validate --manifest-list '$(Build.ArtifactStagingDirectory)/manifest-list.json'
          - publish: '$(Build.ArtifactStagingDirectory)/manifest-list.json'
            artifact: manifest-list

  - stage: Package
    dependsOn: Validate
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
              version: 9.0.x
          - download: current
            artifact: manifest-list
          - script: >
              dotnet run --project src/IntuneLobPublisher.Cli --configuration Release --
              package
              --manifest-list '$(Pipeline.Workspace)/manifest-list/manifest-list.json'
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
                    version: 9.0.x
                - task: AzureCLI@2
                  inputs:
                    azureSubscription: '<workload-identity-service-connection-name>'
                    scriptType: bash
                    scriptLocation: inlineScript
                    inlineScript: >
                      dotnet run --project src/IntuneLobPublisher.Cli --configuration Release --
                      publish
                      --manifest-list '$(Pipeline.Workspace)/manifest-list/manifest-list.json'
                      --package-dir '$(Pipeline.Workspace)/intunewin-packages'
                      --expected-tenant '<tenant-id>'
```

補足:

- `System.PullRequest.TargetCommitId` は PR ビルドでのみ設定される。push ビルドでは CLI 側で直前コミットとの diff または全件 fallback を使う。
- `--expected-tenant` の `<tenant-id>` は variable group から渡す(placeholder のまま commit しない)。

---
