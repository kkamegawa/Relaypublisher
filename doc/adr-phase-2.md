# ADR Phase 2

`doc/adr.md` が 200 行を超えたため、以後の仕様変更を phase 2 として記録します。記録方針は
`doc/adr.md` 冒頭の規則を引き継ぎます。

## 2026-08-30: macOS Architecture 移行衝突を publish 前に collapse(PR #126 review)

- **決定**: publish batch 内で identity ごとの最高 version を選択した後、同じ
  `PackageIdentifier + Platform + DisplayName` を持つ macOS entry に複数の実効 architecture が残り、
  その中に `universal` が含まれる場合は移行 alias とみなし、Graph 呼び出し前に最高
  `PackageVersion` の 1 entry へ collapse する。同一 version の場合は `universal` を優先する。
  - **理由**: `arm64` などを明示した旧 version folder と、`Architecture` を省略した新 version folder は
    identity が異なるため、従来の identity 単位の最高 version 選択では両方が残る。同じ `DisplayName` の
    旧 entry は fallback adopt により `resolution.Metadata == null` となり、downgrade guard を回避して
    履歴 content を再 upload できた。処理順によって Intune app が旧版で終了するため、Graph write 前の
    batch selection で除去する。
  - **影響**: `AppIdentity` と DisplayName fallback の意味は変更しない。`universal` を含まない x64/arm64
    の組み合わせは真正な multi-architecture として別 entry のまま維持し、repository validation でも
    拒否しない。

## 2026-08-30: macOS PKG inspector に bzip2 対応を追加し、SharpZipLib を third-party package として許可(issue #127)

- **決定**: XAR heap entry の compression として `none`/`gzip` に加えて `bzip2` に対応する。実装には
  MIT license の `SharpZipLib`(NuGet package id `SharpZipLib`)を新規に third-party package として許可し、
  `AGENTS.md` の許容 package 一覧に追加する。`XarPkgBundleInspector` からは `BZip2InputStream` を直接
  参照させず、`Bzip2DecompressionStream` という 1 ファイルの adapter 越しにのみ使う。
  - **理由**: XAR archive(macOS の全 `.pkg` が採用)の compression は仕様上 `none`/`gzip`/`bzip2` の
    3 値のみで、bzip2 は狭いエッジケースではない。Microsoft 自身が配布する実パッケージ
    (Global Secure Access Client)が bzip2 を使っており、既存の gzip-only 実装は実運用を止めていた。
    .NET の BCL には bzip2 decompressor が存在せず、`AGENTS.md` の
    「Microsoft 製で要件を満たせない場合のみサードパーティを使う」という条項がまさにこのケースに該当する。
    候補比較(実際に nupkg を取得して検証): `SharpZipLib`(MIT、252 KB、CVE 実績は本リポジトリが呼ばない
    `ZipFile`/`TarArchive` の展開 API に限られる)を、`SharpCompress`(MIT だが 1.5 MB、RAR/7z 等
    不要な decoder を含む)より小さい配布面積として採用した。自前 bzip2 decoder(Huffman + MTF + RLE +
    inverse BWT)は、XAR inspector が避けようとしている「攻撃者制御入力への複雑な自前パース」そのものに
    なるため見送った。
    adapter で隔離する理由は 2 点、いずれも SharpZipLib のソースを確認して検証済み:
    (1) `BZip2InputStream.IsStreamOwner` の既定値は `true`(`GZipStream(..., leaveOpen: true)` と挙動を
    揃えるため adapter 内で `false` に設定)。
    (2) `BZip2Exception` は `SharpZipBaseException : Exception` を継承し `IOException` ではないため、
    `ReadHeapEntryAsync` の既存 catch フィルタ(`InvalidDataException or IOException or OverflowException`)
    を素通りして生の例外が漏れる。adapter が `SharpZipBaseException` 等を `InvalidDataException` に
    変換することで、既存の hard-fail・`--force` 不可の契約を保つ。
  - **影響**: `doc/00-overview.md`・`doc/01-manifest-schema.md` §5.4.3・`doc/02-dotnet-architecture.md` の
    「.NET 標準ライブラリのみ」という文言は撤回するが、「`pkgutil` 非依存・macOS runner 不要」という
    実質的な目標(完全な managed 実装で Linux/Windows CI runner でも動く)は変わらない。依存は adapter
    1 ファイルに隔離しており、将来 `SharpCompress` 等へ切り替える場合もその 1 ファイルの変更で済む。
    `XarPkgBundleInspector.CurrentInspectorVersion`(`"1"`)は変更しない。対応 compression の追加は
    inspection report の読み手にとって後方互換であり、`PackageMetadataReader.InspectionFactsEqual` が
    version を厳密一致で比較しているため、上げると既存の全 `package-metadata.json` が publish 時に
    version mismatch で fail する。
  - **今後の注意**: `SharpZipLib` の既知 CVE は `ZipFile`/`TarArchive` の展開パス処理にあり、本リポジトリは
    それらの API を一切呼ばない。この前提が変わる(該当 API を使い始める、あるいは `SharpZipLib` の
    メンテが完全に停止する)場合は、この決定を再確認すること。
