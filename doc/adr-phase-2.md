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
