# ADR: CI とリリース配布

このリポジトリ自身の CI/CD workflow、および `relaypublisher` パッケージの配布 feed とリリース手順に関する設計判断を記録します。

`doc/task.md`・`doc/plan.md` に記載のない仕様変更を、日付・該当 task・変更理由とともに箇条書きで記録します。
仕様を変更する必要がある場合は、必ず該当領域のファイルを確認し、変更理由が以前の修正と矛盾しないか確認してください。
矛盾する可能性がある場合はユーザーに承認を求めます。

## 2026-09-15: Homebrew tap による macOS 配布 (Issue #173)

- **決定**: macOS(Apple silicon)向けに、本リポジトリとは別の tap リポジトリ `kkamegawa/homebrew-tap` から
  Homebrew formula で配布する。formula は GitHub Release の `relaypublisher-<version>-osx-arm64.zip` を取得し、
  Homebrew 用に別のバイナリはビルドしない。
  - **理由**: バイナリの正本を GitHub Release に一本化したまま `brew install` / `brew upgrade` を提供するため。
    tap を本リポジトリに同居させると、利用者が tap の URL を明示する必要があるうえ、release 後に formula 更新の
    commit を main に入れる必要が生じ、tag と main を軸にした release gate と噛み合わない。
- **決定**: Cask ではなく Formula を使う。
  - **理由**: Homebrew は cask のダウンロードにだけ quarantine 属性を付け、Homebrew 5.0 で `--no-quarantine` を廃止した。
    署名・notarization のない single-file app は cask だと Gatekeeper にブロックされるが、formula なら属性が付かない。
- **決定**: 対象は `osx-arm64` のみとし、Intel Mac は `depends_on arch: :arm64` で拒否する。Linux も対象外とする。
  - **理由**: release は `osx-arm64` しか作っておらず、Homebrew 7.0 で Intel macOS は Tier 3 に下がった(2027-09 にサポート終了)。
- **決定**: `release-draft.yml` の ubuntu runner でのビルドは変えない。
  - **理由**: .NET 10 SDK は non-macOS host の single-file publish でも managed signer で ad-hoc 署名する
    (dotnet/runtime PR #110417)。Apple silicon が要求する署名はこれで満たされる。
  - **今後の注意**: .NET SDK を更新したら、tap CI の `codesign --verify --strict` が通ることを必ず確認する。
- **決定**: tap に配信するのは stable release だけとし、formula は `release-publish.yml` の `update-homebrew-tap` job が
  作る pull request で更新する。tap CI 通過後に人がマージする。書き込みには tap にだけインストールした GitHub App の token を使う。
  - **理由**: 壊れた formula が利用者へ即座に届くのを防ぐため。GitHub App は PAT と違って有効期限の管理が要らず、
    `GITHUB_TOKEN` と違って他リポジトリに書け、作った pull request で tap の CI が起動する。
  - **影響**: `release` environment に `HOMEBREW_TAP_APP_CLIENT_ID` / `HOMEBREW_TAP_APP_PRIVATE_KEY` が必要。
    tap の job は `push-packages` と独立しており、どちらの失敗も他方を止めない。
- **今後の注意**: Homebrew 6.0 の tap trust により、利用者は最初に `brew tap --trust kkamegawa/tap` が必要。
  Homebrew 7.0 で第三者 tap の `post_install` が非推奨になったため、formula に `post_install` を追加しない。

## 2026-08-30: nuget.org Trusted Publishing (OIDC) への移行 (Issue #131)

- **決定**: `nuget.org` への自動 publish は GitHub Actions の `.github/workflows/release-publish.yml` に一本化し、
  `NuGet/login` v1.2.0 (`8d196754b4036150537f80ac539e15c2f1028841`) で Trusted Publishing を利用する。
  - **理由**: 長期有効な NuGet API key を repository/environment secret に保持せず、GitHub の OIDC token から
    publish 直前に発行される短期 API key へ移行するため。
  - **影響**: publish job は `id-token: write` と `NUGET_USER` を必要とする。action output は同じ job の
    push にだけ渡し、secret や artifact として保存しない。既存の GitHub Packages / Azure Artifacts の push は変更しない。
- **決定**: Trusted Publishing policy の Repository Owner=`kkamegawa`、Repository=`Relaypublisher`、
  Workflow File=`release-publish.yml` (basename only)、Environment=`release` を正とする。
  - **理由**: NuGet が発行元 workflow と environment を限定できるようにし、実ファイル名(hyphen)との不一致を防ぐため。
- **決定**: Azure Pipelines から `nuget.org` へ publish する設計・参照サンプルを削除し、Azure Pipelines は
  Intune publish と Azure Artifacts の用途に限定する。
  - **理由**: 本リポジトリの NuGet.org Trusted Publishing の自動化経路を一つにし、長期 API key を使う別経路を残さないため。

## 2026-08-21: 配布 feed とリリース workflow の再構成(doc/task.md 同日エントリ参照)

同じ作業日の publish / Graph に関する決定は [publishing.md](publishing.md) に記録しています。

- **決定**: `relaypublisher` パッケージの配布先を `nuget.org` 単独から、GitHub Packages(このリポジトリ)/
  Azure Artifacts / nuget.org の 3 feed に拡張する。
  - **理由**: 到達できる feed が利用者ごとに異なる。一般利用者は nuget.org、このリポジトリを直接使う利用者は
    GitHub Packages、社内 CI や閉じたネットワークは Azure Artifacts が現実的な経路になる。
  - **影響**: `doc/issues/issue-019` の「nuget.org へのリリース運用」というスコープを 3 feed に更新した。
    Azure Artifacts の feed URL は実 URL を書けない(AGENTS.md 禁止事項)ため secret
    `AZURE_ARTIFACTS_FEED_URL` から渡す。認証は PAT ではなく OIDC (workload identity federation) +
    artifacts-credprovider を使う。
  - **今後の注意**: 3 feed とも `--skip-duplicate` を付ける。片方だけ push 済みの状態から再実行しても
    冪等に完了させるため。

- **決定**: NuGet feed への push の trigger を `push: tags` から `release: published` に変更し、
  release workflow を `release-draft.yml` と `release-publish.yml` の 2 本に分割する。
  - **理由**: NuGet feed は一度 push した version を削除できない(unlist しかできない)。
    「tag を打った瞬間に公開が確定する」構成だと、誤った tag からの publish を取り消せない。
    draft release を人がレビューして publish する操作を最後の関門に置くことで、tag の打ち直しは
    draft release を消すだけでやり直せるようにする。
  - **影響**: `release-publish.yml` は再ビルドせず `gh release download` で release に添付された `.nupkg` を
    そのまま push する。レビューした bits と publish する bits を一致させるため。
    publishing secrets は repository ではなく `release` environment にスコープする。
  - **今後の注意**: `release-draft.yml` は tag が main から到達可能であることを
    `git merge-base --is-ancestor` で検証する。main 以外の履歴から release を作らせないため。

- **決定**: このリポジトリ自身の CI/CD workflow を `workflows/github-actions/`(参照サンプル)から
  `.github/workflows/`(実 workflow)に移す。`workflows/github-actions/ci.yml` と
  `release-nuget-tool.yml` は削除する。
  - **理由**: リポジトリを public 化するため、自身の CI を実際に動かす必要がある。この 2 つは
    Relaypublisher 自身のビルド/リリースであって利用者向けサンプルではないので、実 workflow 化すると
    重複する。`workflows/github-actions/publish-intune-apps.yml` と `workflows/azure-pipelines/` は
    利用者向けサンプルなので残す。
  - **影響**: public 化後は fork からの PR が走るため、`ci.yml` は secrets を一切参照しない設計にした
    (`pull_request_target` も使わない)。`doc/00-overview.md` のリポジトリ構成図と
    `doc/05-operation.md` §6 の checklist を、利用者向けと Relaypublisher 自身向けに分けて記述し直した。
