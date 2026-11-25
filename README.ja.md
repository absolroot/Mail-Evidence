<p align="center">
  <img src="assets/MailViewer.svg" width="96" height="96" alt="Mail Evidence のロゴ">
</p>

<h1 align="center">Mail Evidence</h1>

<h3 align="center">調査の進めやすさを重視したメール分析ツール</h3>

<p align="center">Mail Evidence は、使い慣れたメール画面で証拠を調べられる Windows アプリです。メールファイルやフォルダーを開いたり、NTFS ディスクイメージからメールを抽出したりできます。添付ファイルをプレビューし、異常が見つかったら根拠となるヘッダーや元のバイト列まで確認できます。</p>

<p align="center">
  <a href="https://github.com/absolroot/Mail-Evidence/releases/latest"><img src="https://badgen.net/github/release/absolroot/Mail-Evidence/latest" alt="最新リリース"></a>
  <a href="https://www.codefactor.io/repository/github/absolroot/mail-evidence/issues/main"><img src="https://www.codefactor.io/repository/github/absolroot/mail-evidence/badge/main" alt="CodeFactor のコード品質評価"></a>
  <a href="https://github.com/absolroot/Mail-Evidence/actions/workflows/ci.yml"><img src="https://github.com/absolroot/Mail-Evidence/actions/workflows/ci.yml/badge.svg?branch=main&amp;event=push" alt="Windows のビルドとテスト"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/absolroot/Mail-Evidence?color=blue" alt="Apache 2.0 ライセンス"></a>
</p>

<p align="center"><a href="https://github.com/absolroot/Mail-Evidence/releases/download/v1.6.0/Mail-Evidence-v1.6.0-win-x64-compact.zip">Windows x64 軽量版</a> · <a href="https://github.com/absolroot/Mail-Evidence/releases/download/v1.6.0/Mail-Evidence-v1.6.0-win-x64-standalone.zip">Windows x64 ランタイム同梱版</a> · <a href="https://github.com/absolroot/Mail-Evidence/releases/download/v1.5.7/Mail-Evidence-CLI-v0.4.0-win-x64.zip">Rust CLI 0.4.0</a></p>

<p align="center"><a href="#使い方">使い方</a> · <a href="#調査機能">調査機能</a> · <a href="#対応形式">対応形式</a></p>

<p align="center"><a href="README.md">English</a> · <a href="README.ko.md">한국어</a></p>

## 調査機能

[![ポインターでメール検索、添付ファイルのプレビュー、異常の根拠から16進表示までをたどる英語版の操作デモ](docs/media/full-app-walkthrough-dark.gif)](docs/media/full-app-walkthrough-dark.gif)

デモと同じ[サンプルメール 24 通](tests/fixtures/readme-demo/)で、画像のプレビューや異常の根拠を確認できます。

- フォルダー、メール一覧、閲覧ペインを一つの画面に表示します。入力に合わせて検索結果が絞られ、後で確認するメールは固定したり、ブックマークを付けたりできます。
- メールファイルやフォルダーをまとめて開きます。NTFS イメージから必要なメールも抽出できます。Windows アプリでは旧形式の PST、Outlook Express の DBX・MBX、Apple Mail の EMLX も開けます。
- 添付ファイルをプレビューし、異常の根拠となったヘッダー、リンク先、バイト列を確認できます。同じアドレス、URL、Message-ID、添付ファイルの SHA-256 で別のメールを探せます。
- 元の資料は読み取り専用で開きます。メールや検索結果、添付ファイルを書き出し、保存したファイルのサイズと SHA-256 を検証できます。

## 使い方

1. Windows x64 用の ZIP を展開して `MailEvidence.exe` を起動します。軽量版には [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) が必要です。
2. **メールデータを開く**画面でファイルかフォルダーを選び、**分析を開始**を押します。Outlook の保存先を調べるには **この PC でメールファイルを探す…** を使います。[ディスクイメージからメールを開く](docs/disk-image-mail.md)
3. **すべてのメール**で検索するか、**異常の兆候**に表示されたメールを選びます。[サンプルメール](tests/fixtures/README.md)でも試せます。

HTML 本文では外部画像、スクリプト、リンク先への移動が既定でブロックされます。HTML 表示には [Microsoft Edge WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) を使います。

## 対応形式

### メールファイルとメールストア

普段見かけるメール形式のほとんどに対応しています。懐かしの Outlook Express のメールも開けます。

| 形式 | Windows アプリ | Rust CLI |
| --- | :---: | :---: |
| インターネットメール `.eml` | ✓ | ✓ |
| `.nws`、`.mime`、`.rfc822` | ✓ | ✓ |
| メールボックス `.mbox` | ✓ | ✓ |
| Outlook メール `.msg` | ✓ | ✓ |
| Outlook アーカイブ `.pst` | ✓ | ✓ |
| Outlook のオフラインメール `.ost` | ✓ | ✓ |
| Outlook Express `.dbx`、`.mbx` | ✓ | ✓ |
| Outlook for Mac `.olm` | ✓ | ✓ |
| Apple Mail `.emlx`、`.partial.emlx` | ✓ | ✓ |
| `.mmdf`、`.babyl`、`.rmail`、Maildir、MH | ✓ | ✓ |
| メールを含む `.zip` | ✓ | ✓ |
| TNEF `.tnef`、`winmail.dat` | ✓ | ✓ |
| メールを含む CMS SignedData `.p7m`、`.p7s` | ✓ | ✓ |

PST・OST は 1 ファイルあたり最大 **2 GiB** です。512 バイトページは ANSI 14・15 と Unicode 21・23 のエンコード方式 0・1・2 に、4 KiB ページは Unicode 36 の方式 0 に対応しています。EDB・NSF は形式の識別まで対応しています。新しい Outlook 専用のキャッシュ、保護された OST、サーバーにのみ残るメール、削除済みアイテムの復元は対象外です。[Windows 版の対応範囲](docs/windows-format-coverage.md) · [CLI の形式別対応範囲](docs/rust-mail-cli.md#inputs-and-attachments)

### ディスクイメージ

| 形式 | 拡張子 |
| --- | --- |
| RAW | `.raw`、`.dd`、`.img`、`.000` または `.001` から始まる分割 RAW |
| 物理 EWF | `.e01`、`.ex01` |
| 仮想ディスク | `.qcow`、`.qcow2`、`.qcow3`、`.vhd`、`.vhdx`、`.vmdk` |
| AFF1 | `.aff` |

読み取れる NTFS ボリュームから EML、MBOX、MSG、PST、OST、DBX、EMLX、OLM などを検索します。論理 EWF と BitLocker でロックされたボリュームは、この手順の対象外です。[イメージ形式ごとの条件](third_party/ntfs-tracer/modules/EvidenceImage/README.md)

### 添付ファイルのプレビュー

| 種類 | 表示範囲 |
| --- | --- |
| 文書 | `.pdf`、`.hwp`、`.hwpx`、`.doc`、`.docx` の先頭ページ。`.ppt`、`.pptx` の最初のスライド。`.xls`、`.xlsx` の最初のワークシートの一部。 |
| アーカイブ | `.zip`、`.tar`、`.7z` 内のファイル名とフォルダー名。パスワード保護を検出した場合はその状態も表示 |
| 画像 | `.jpg`、`.jpeg`、`.png` |
| テキスト | `.txt`、`.log`、`.csv`、`.tsv`、`.json`、`.xml`、`.md`、`.ini`、`.cfg`、`.yaml`、`.yml` と `text/*` の先頭部分 |
| バイナリ | `.bin`、`.dat` の先頭部分。文字コードを判定できないときは表示可能な ASCII 文字を確認できます。 |

アーカイブの一覧は最大 64 KiB まで表示します。アーカイブは展開せず、項目名だけを表示します。暗号化された ZIP・7z でファイル名を読み取れる場合は、一覧にパスワード保護の状態を表示します。7z のファイル名も暗号化されている場合は、パスワードが必要だと表示します。

[プレビューの条件](preview/README.md)

## 検出する異常の兆候

| 確認項目 | 表示する根拠 |
| --- | --- |
| 一度だけ記録されるヘッダーの重複 | `From`、`Sender`、`Reply-To`、`Date`、`Message-ID` に記録された各値 |
| 送信元と返信先のドメインの違い | 送信元と返信先のアドレス |
| 中継時刻の逆転 | 隣接する `Received` の時刻が 30 秒以上逆転している箇所 |
| HTML リンクのドメインの違い | リンクに表示されたアドレスと実際のリンク先 |
| 紛らわしい添付ファイル名 | 二重拡張子、または文字の表示方向を制御する Unicode 文字とその位置 |
| 文書・画像名を持つ PE ファイル | ファイル名、PE シグネチャ、デコード後のオフセット |
| 添付ファイルの構造異常 | 先頭シグネチャの不一致、内部構造の不一致、終端マーカーが見つからない箇所、終端後のデータ |
| やり取りの途中で返信先が変更 | 直前 30 日に同じ送信元から届いた参照メールのうち、比較可能な最新メールと現在の返信先 |

検出結果は調査の手掛かりです。サポート窓口の返信先や計測用のリンクでも違いが出る場合があります。SPF・DKIM・DMARC はヘッダーに記録された値を表示し、信頼性は未確認と示します。[判定ルールと制限](docs/investigation-rules.md) · [ファイルのシグネチャ](docs/attachment-signatures.md)

## サンプルで試す

[添付ファイルの検証用メール](tests/fixtures/attachment-boundaries/02-mixed.eml)を開き、**異常の兆候**で `invalid-idat.png` を選びます。PNG の IDAT チャンクには **8,200 バイト**と記録されていますが、残りの領域は **最大 37 バイト**です。**添付ファイルの 16 進表示を開く**を選ぶと、デコード後の `0x21` を確認できます。

[![PNG チャンクに記録された長さと残りのバイト数を表示する画面](docs/media/anomaly-evidence-dark.png)](docs/media/anomaly-evidence-dark.png)

[テスト用の合成メール一覧](tests/fixtures/README.md#mail-test-fixtures)には、日常のメールや会話履歴のサンプルもあります。

## ビルドとテスト

Windows アプリのビルドには .NET 10 SDK が必要です。添付ファイルのプレビュー用プログラムと CLI は Rust でビルドします。

```powershell
cargo build --manifest-path preview/Cargo.toml --release --locked
cargo build --manifest-path engine/Cargo.toml --release --locked
dotnet build MailViewer.slnx -c Release
dotnet run --project tests/MailViewer.Verification -c Release -- --synthetic-only
```

[CLI のコマンド](docs/rust-mail-cli.md) · [検証用データとテスト手順](tests/fixtures/README.md) · [画面の検証](docs/ui-verification.md) · [EWF DLL のビルド](third_party/ntfs-tracer/modules/EvidenceImage/Logical/Native/README.md) · [ライセンス](licenses/)

Windows 用の配布ファイルは `tools/publish-mail-evidence.ps1` で作成できます。英語の画面画像と GIF の更新は、Windows で上記の手順に従ってプレビューワーカーをビルドし、Pillow をインストールしてから `python tools/update-readme-media.py` を実行します。

## 表示言語

**表示 → 表示言語**で 16 種類の言語から選べます。変更はすぐに反映されます。

English · 한국어 · 日本語 · Deutsch · Español · Français · Bahasa Indonesia · Italiano · Polski · Português (Brasil) · Türkçe · Tiếng Việt · Русский · Українська · 简体中文 · 繁體中文

## ライセンス

プロジェクト独自のコードは [Apache License 2.0](LICENSE) の下で公開しています。

サードパーティーのコンポーネントには、それぞれのライセンスが適用されます。[ライセンスに関する通知](licenses/)を参照してください。
