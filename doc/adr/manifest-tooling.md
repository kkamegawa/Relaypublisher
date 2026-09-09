# ADR: manifest schema と作成ツール

YAML manifest の schema、検証、および `tools/yamlcreate.ps1` による manifest 作成 / バージョン更新に関する設計判断を記録します。

`doc/task.md`・`doc/plan.md` に記載のない仕様変更を、日付・該当 task・変更理由とともに箇条書きで記録します。
仕様を変更する必要がある場合は、必ず該当領域のファイルを確認し、変更理由が以前の修正と矛盾しないか確認してください。
矛盾する可能性がある場合はユーザーに承認を求めます。

## 2026-09-06: manifest 作成スクリプトの Windows file detection 対応 (Issue #140)

- **決定**: `tools/yamlcreate.ps1` の Windows Detection は `Type` を選択させ、`script` と `file` の両方を生成する。
  - **理由**: Issue #141 で `Detection.Type` の discriminator が manifest schema に入り(v1.1.0)、script 固定の生成では
    doc/01-manifest-schema.md §5.2.1 を満たす manifest を作れなくなったため。PR #139 はこの変更より前に書かれている。
  - **影響**: `Type: script` の出力は一切変えない。既存 manifest の canonical hash に影響しないことは、script 経路の
    回帰ケースで固定する。
- **決定**: `Detection.Path` / `FileOrFolderName` は `Test-SafeRelativePath` / `Read-RelativePath` を通さず、
  `ManifestValues.IsValidTargetDevicePath` / `IsValidTargetDeviceLeafName` を移植した専用の検証を使う。
  - **理由**: 2026-09-05 の ADR と同じ理由で、これらは管理対象端末上で評価される値であり、repository path の規則で
    検証すると drive letter・UNC・root-relative のいずれも必ず誤って拒否されるため。
- **決定**: `Detection.Path` はシングルクォート、`ComparisonValue` はダブルクォートで出力する。
  - **理由**: 既定の quoting では `%` が leading indicator に該当するため `%ProgramFiles%\Contoso` だけが
    `"%ProgramFiles%\\Contoso"` になり、同じ意味の path が 3 通りの表記で出力される。doc/01-manifest-schema.md §5.2.1 と
    `samples/manifests/contoso-tool-windows-file-detection.yaml` の表記に揃える。Update モードの scalar decoder は
    既にシングルクォートと `''` を復号できるため、読み取り側の変更は不要。
- **決定**: version bump の対象キーに `ComparisonValue` を追加する。
  - **理由**: `greaterThanOrEqual` のルールを旧バージョンのまま残すと、旧リリースが条件を満たすと Intune が判定して
    更新が適用されない(`equal` では逆に新リリースが未検出になる)。置換は旧バージョンと完全一致する箇所だけなので、
    意図的に置いた下限値は保持される。新バージョンが数値形式でない場合は保存後の `validate` が弾く。
- **決定**: `-DetectionType` パラメーターは追加しない。
  - **理由**: New モードは `PackageIdentifier` などを必ず対話で聞くため非対話実行はそもそもできず、同じ構造分岐を持つ
    `Source type` にもパラメーターがない。テストは `Read-Host` の差し替えで分岐を駆動できる。

## 2026-09-05: Windows file-system detection (Issue #141)

- **決定**: Windows `Detection.Type` は既存の `script` に加えて `file` を受け付け、`exists` と `version` の
  `win32LobAppFileSystemRule` だけを生成する。
  - **理由**: file version による Intune 検出では PowerShell detection script を repository に追加・管理する必要が
    ない。一方、Graph の `modifiedDate`、`createdDate`、`sizeInMB` は comparison value の形式を別途定義・検証する
    必要があるため、この変更には含めない。
  - **影響**: `exists` の manifest は `Operator` / `ComparisonValue` を持たず、mapper が Graph の
    `operator: notConfigured` を生成する。`version` は 6 つの比較演算子と 1～4 part の数値 comparison value を
    必須とする。Graph の unset sentinel `notConfigured` は manifest 入力として許可しない。
- **決定**: file detection の `Path` / `FileOrFolderName` は target-device value として validation し、
  `PathSafety` を使わない。
  - **理由**: drive-rooted、root-relative、UNC、environment-variable-rooted の path は Intune が管理対象端末上で
    評価する正当な値であり、repository root の下に解決する `PathSafety` に渡すと誤って拒否されるため。
  - **影響**: file detection は detection script を読み取りも staging もしない。script 用と file 用の fields は
    相互排他にし、macOS は既存の `IncludedApps` 検出を維持する。
- **決定**: Graph の `rules` は System.Text.Json polymorphic payload とし、derived rule に手書きの
  `@odata.type` property を持たせない。
  - **理由**: base-typed collection では derived property が失われる。polymorphic discriminator と同名 property
    を併用すると serialization failure になるため。
  - **影響**: script rule は `Win32LobAppPowerShellScriptRulePayload` に改名する。既存 script manifest の
    canonical hash を維持するため、file 用の全 fields は nullable・初期値なしで定義する。file criteria の変更は
    manifest hash / inputHash を変更するが、script body は引き続き hash に含めない。

## 2026-09-02: manifest 作成スクリプトのソース契約 (Issue #140)

- **決定**: `tools/yamlcreate.ps1` の `publicHttp` は `Auth.Type: none` に限定する。
  - **理由**: PR #139 の設計にあった `token` の選択肢は、既存の `PublicHttpSourceProvider` が認証付き取得を拒否する契約と矛盾していたため。新たな認証方式は追加せず、生成内容を既存の取得処理に合わせる。
- **決定**: GitHub Release の自動ハッシュ取得は public / private ともアセット ID の REST API を使い、ソースの認証は YAML キー順に依存せず読み取る。
  - **理由**: private release で利用できないブラウザー向け URL と、`Auth` が `Sha256` より前にある場合の認証情報の欠落を解消するため。
- **決定**: 表示する URL の認証情報・クエリ・フラグメントは除去し、保存する manifest では元の値を保持する。
  - **理由**: ダウンロード先の署名をログへ出さず、取得に必要な URL は維持するため。
