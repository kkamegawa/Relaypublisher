# トラブルシューティングガイド

このガイドは、Relaypublisher の production failure と recovery path をまとめたものです。

正式ドキュメントは英語版の [06-troubleshooting.md](06-troubleshooting.md) です。

## 1. Intune notes の management metadata が壊れた

Relaypublisher は management metadata を JSON として Intune app の `notes` field に保存します。`notes` field は Intune admin center から編集できるため、operator が誤って metadata を削除または破損する可能性があります。

期待される recovery path:

1. Manifest の `DisplayName` が同じままであることを確認します。
2. Full manifest root に対して `plan` を実行し、続けて `validate --manifest-list` で repository-wide の `DisplayName` 一意性を確認します。
3. 対象 manifest に対して `publish --dry-run` を実行します。Full batch を確認する場合は同じ manifest list を使います。
4. `DisplayName` に一致する Intune app が 1 件だけであれば、Relaypublisher は DisplayName fallback で解決します。
5. 次の実 publish で、Relaypublisher は fresh management metadata を `notes` に書き戻して app を adopt します。

Commands:

```powershell
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  plan --manifest-root manifests --output manifest-list.json

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  validate --manifest-list manifest-list.json

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  publish --manifest <manifest-path> --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

```bash
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  plan --manifest-root manifests --output manifest-list.json

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  validate --manifest-list manifest-list.json

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  publish --manifest <manifest-path> --package-dir ./out \
  --expected-tenant <tenant-id> --dry-run
```

Management metadata または DisplayName fallback のどちらかで複数 app が一致した場合、publish は fail します。この failure を bypass しないでください。先に重複した Intune app または manifest を修正してから rerun します。

## 2. `TenantMismatchException`

`TenantMismatchException` は Graph token の tenant claim が `--expected-tenant` と一致しないことを意味します。Relaypublisher は Intune に write する前に fail します。

確認項目:

- `<tenant-id>` に使っている CI variable または secret が意図した tenant である。
- Entra app registration がその tenant に所属している。
- Federated credential が CI で使う同じ app registration に設定されている。
- CI login step が意図した `AZURE_CLIENT_ID` と `AZURE_TENANT_ID` を受け取っている。
- GitHub Actions environment または Azure Pipelines service connection が古い identity を参照していない。

Recovery:

1. CI identity または expected tenant value を修正します。
2. 失敗した workflow を rerun します。
3. `--expected-tenant` は維持します。Run を通すために削除しないでください。

## 3. Downgrade が skip された

Relaypublisher は既定で、manifest の `PackageVersion` が Intune management metadata に保存された version より低い場合、publish を skip します。

典型的な原因:

- `plan` が古い manifest を選択した。
- Release branch に古い package version が含まれている。
- Operator が意図的に以前の package に rollback しようとしている。

選択された manifest を確認します。

```powershell
Get-Content manifest-list.json
```

```bash
cat manifest-list.json
```

意図的な rollback でない場合は、manifest version を更新するか、`plan` で使った base ref を修正します。

意図的な rollback の場合は、`--allow-downgrade` を付けて以前の package を明示的に publish します。

```powershell
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  publish --manifest <manifest-path> --package-dir ./out `
  --expected-tenant <tenant-id> --allow-downgrade
```

```bash
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  publish --manifest <manifest-path> --package-dir ./out \
  --expected-tenant <tenant-id> --allow-downgrade
```

Rollback 後は Intune で app を確認し、assignments が意図した manifest state と一致していることを確認します。

## 4. GitHub release token がない

`githubRelease` source が `Auth.Type: token` を使う場合、Relaypublisher は `Auth.SecretName` に指定された environment variable から token を読みます。

Variable が missing または empty の場合:

- Manifest が意図した `SecretName` を使っていることを確認します。
- CI job が secret を同じ名前の environment variable に map していることを確認します。
- Pull request job で private release assets を download する前提になっていないことを確認します。

例:

```yaml
Auth:
  Type: token
  SecretName: GH_RELEASE_PAT
```

CI environment は package job に `GH_RELEASE_PAT` を公開する必要があります。

## 5. Azure Blob source を download できない

`azureBlob` source では、Relaypublisher は static secret ではなく workload identity を使います。

確認項目:

- Package job で OIDC が有効になっている。
- CI identity に必要な storage scope の storage reader role が付与されている。
- Login step で `AZURE_CLIENT_ID`、`AZURE_TENANT_ID`、`AZURE_SUBSCRIPTION_ID` が利用できる。
- Manifest の `AccountName`、`Container`、`BlobName` が意図した package object を指している。

Identity または storage permission を修正してから rerun します。Storage account key や signed package URI を manifest、log、artifact に置かないでください。

## 6. Package metadata がない

`publish` は `package` が書き出した `package-metadata.json` を使います。

Publish が package metadata missing を報告した場合:

- Package job が成功していることを確認します。
- Publish job が `intunewin-packages` artifact を `--package-dir` に渡した path と同じ場所に download していることを確認します。
- `package` と `publish` が同じ `manifest-list.json` を使っていることを確認します。
- Package metadata を手動編集せず、`package` を rerun します。

## 7. Safe rerun rules

- `validate`、`plan`、`package --stage-only` は rerun して安全です。
- `package` は rerun して安全で、同じ input なら同じ deterministic `inputHash` を再現するべきです。
- `publish --dry-run` は rerun して安全です。
- 実 publish は収束するよう設計されていますが、content activation step は tool では undo できません。Rollback は以前の manifest version を `--allow-downgrade` 付きで publish して行います。
