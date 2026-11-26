<p align="center">
  <img src="assets/MailViewer.svg" width="96" height="96" alt="Mail Evidence logo">
</p>

<h1 align="center">Mail Evidence</h1>

<h3 align="center">A user-friendly email analysis tool built around the analyst’s workflow</h3>

<p align="center">Mail Evidence is a Windows tool for examining email in a familiar inbox layout. Open mail files and folders or extract mail from an NTFS disk image. Preview attachments, spot anomalies, and trace findings to the original headers or bytes.</p>

<p align="center">
  <a href="https://github.com/absolroot/Mail-Evidence/releases/latest"><img src="https://badgen.net/github/release/absolroot/Mail-Evidence/latest" alt="Latest release"></a>
  <a href="https://www.codefactor.io/repository/github/absolroot/mail-evidence/issues/main"><img src="https://www.codefactor.io/repository/github/absolroot/mail-evidence/badge/main" alt="CodeFactor code quality grade"></a>
  <a href="https://github.com/absolroot/Mail-Evidence/actions/workflows/ci.yml"><img src="https://github.com/absolroot/Mail-Evidence/actions/workflows/ci.yml/badge.svg?branch=main&amp;event=push" alt="Windows build and tests"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/absolroot/Mail-Evidence?color=blue" alt="Apache 2.0 license"></a>
</p>

<p align="center"><a href="https://github.com/absolroot/Mail-Evidence/releases/download/v1.6.0/Mail-Evidence-v1.6.0-win-x64-compact.zip">Windows x64 compact</a> · <a href="https://github.com/absolroot/Mail-Evidence/releases/download/v1.6.0/Mail-Evidence-v1.6.0-win-x64-standalone.zip">Windows x64 with runtime</a> · <a href="https://github.com/absolroot/Mail-Evidence/releases/download/v1.5.7/Mail-Evidence-CLI-v0.4.0-win-x64.zip">Rust CLI 0.4.0</a></p>

<p align="center"><a href="#quick-start">Quick start</a> · <a href="#investigation-features">Investigation features</a> · <a href="#supported-inputs">Supported inputs</a></p>

<p align="center"><a href="README.ko.md">한국어</a> · <a href="README.ja.md">日本語</a></p>

## Investigation features

[![English walkthrough showing pointer clicks through search, attachment preview, anomaly evidence, and hex](docs/media/full-app-walkthrough-dark.gif)](docs/media/full-app-walkthrough-dark.gif)

Open the [24-message sample mailbox](tests/fixtures/readme-demo/) to try the image previews and findings shown in the demo.

- Browse folders, search as you type, and read the selected message in one window. Pin or bookmark messages for later.
- Open mail files and folders together, or extract selected mail from an NTFS disk image. The Windows app also opens older PST files, Outlook Express DBX and MBX, and Apple Mail EMLX.
- Preview attachments and inspect the headers, link destinations, or bytes behind a finding. Find other messages by exact address, URL, Message-ID, or attachment SHA-256.
- Open evidence read-only. Export selected messages, search results, or attachments and verify the saved sizes and SHA-256 hashes.

## Quick start

1. Extract the Windows x64 ZIP and run `MailEvidence.exe`. The compact edition requires the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
2. Choose files or a folder in **Open mail sources**, then select **Analyze**. Use **Find mail files on this PC…** to look in Outlook storage locations. [Open mail from a disk image](docs/disk-image-mail.md)
3. Search in **All mail**, or review findings in **Anomalies**. Try the [sample messages](tests/fixtures/README.md) to see the workflow.

To follow the GIF, download `Inbox.mbox` and `Sent.mbox` from the [24-message demo mailbox](tests/fixtures/readme-demo/README.md). Select both files in **Open mail sources** and choose **Analyze**.

HTML messages block external images, scripts, and link navigation by default. HTML rendering uses [Microsoft Edge WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

## Supported inputs

### Mail files and stores

Mail Evidence opens most mail formats you’ll run into, from everyday EML files to decades-old Outlook Express archives.

| Format | Windows app | Rust CLI |
| --- | :---: | :---: |
| Internet mail `.eml` | ✓ | ✓ |
| `.nws`, `.mime`, `.rfc822` | ✓ | ✓ |
| Mailbox `.mbox` | ✓ | ✓ |
| Outlook message `.msg` | ✓ | ✓ |
| Outlook archive `.pst` | ✓ | ✓ |
| Outlook offline mail `.ost` | ✓ | ✓ |
| Outlook Express `.dbx`, `.mbx` | ✓ | ✓ |
| Outlook for Mac `.olm` | ✓ | ✓ |
| Apple Mail `.emlx`, `.partial.emlx` | ✓ | ✓ |
| `.mmdf`, `.babyl`, `.rmail`, Maildir, MH | ✓ | ✓ |
| `.zip` archives containing mail | ✓ | ✓ |
| TNEF `.tnef`, `winmail.dat` | ✓ | ✓ |
| CMS SignedData with embedded mail `.p7m`, `.p7s` | ✓ | ✓ |

PST and OST files are limited to **2 GiB** each. The app reads 512-byte pages in ANSI 14–15 and Unicode 21 and 23 with encodings 0–2, and 4 KiB pages in Unicode 36 with encoding 0. EDB and NSF support stops at format identification. The new Outlook cache, protected OST files, server-only mail, and deleted-item recovery are outside the supported coverage. [Windows format coverage](docs/windows-format-coverage.md) · [CLI format notes](docs/rust-mail-cli.md#inputs-and-attachments)

### Disk images

| Format | Extensions |
| --- | --- |
| RAW | `.raw`, `.dd`, `.img`, split RAW starting at `.000` or `.001` |
| Physical EWF | `.e01`, `.ex01` |
| Virtual disks | `.qcow`, `.qcow2`, `.qcow3`, `.vhd`, `.vhdx`, `.vmdk` |
| AFF1 | `.aff` |

On readable NTFS volumes the app finds supported formats including EML, MBOX, MSG, PST, OST, DBX, EMLX, and OLM. Logical EWF and locked BitLocker volumes are outside this workflow. [Image format limits](third_party/ntfs-tracer/modules/EvidenceImage/README.md)

### Attachment previews

| Type | Preview |
| --- | --- |
| Documents | First page of `.pdf`, `.hwp`, `.hwpx`, `.doc`, and `.docx`. First slide of `.ppt` and `.pptx`. First worksheet excerpt of `.xls` and `.xlsx`. |
| Archives | File and folder names in `.zip`, `.tar`, and `.7z`, with password protection indicated when detected |
| Images | `.jpg`, `.jpeg`, `.png` |
| Text | Excerpts from `.txt`, `.log`, `.csv`, `.tsv`, `.json`, `.xml`, `.md`, `.ini`, `.cfg`, `.yaml`, `.yml`, and `text/*` attachments |
| Binary data | Excerpts from `.bin` and `.dat`, with printable ASCII where text decoding is not available |

Archive listings are limited to 64 KiB of preview output. The archive preview lists names without extracting files. Encrypted ZIP and 7z files show a password-protection notice alongside readable names. If the 7z file names are encrypted too, the preview shows that a password is required.

[Preview limits](preview/README.md)

## What does Mail Evidence flag?

| Finding | Evidence shown |
| --- | --- |
| Repeated single-instance header | Each occurrence of `From`, `Sender`, `Reply-To`, `Date`, or `Message-ID` |
| Different From and Reply-To domains | The two addresses and their domains |
| Reversed relay timestamps | Adjacent `Received` timestamps reversed by at least 30 seconds |
| Different domains in an HTML link | The address shown in the link and its actual destination |
| Disguised attachment filename | A double extension or a Unicode direction control and its position |
| PE content under a document or image name | The filename, PE signature, and offset in the decoded attachment |
| Unexpected attachment structure | A mismatched start signature or internal structure, a missing end marker, or bytes after the end |
| Reply-To change in a conversation | The current address and the most recent comparable referenced message from the same sender in the preceding 30 days |

A finding is an observation to look into. Support addresses and tracking links can also trigger a difference. SPF, DKIM, and DMARC results are shown as recorded in the headers, with trust unverified. [Rules and limits](docs/investigation-rules.md) · [Attachment signatures](docs/attachment-signatures.md)

## Try it with a sample

Open the [attachment sample](tests/fixtures/attachment-boundaries/02-mixed.eml) and select `invalid-idat.png` in **Anomalies**. The PNG declares **8,200 bytes** for the IDAT chunk. There is room for **at most 37 bytes**. **Open full attachment hex** displays the decoded bytes at `0x21`.

[![PNG chunk length and available bytes shown in the Anomalies tab](docs/media/anomaly-evidence-dark.png)](docs/media/anomaly-evidence-dark.png)

The [synthetic mail samples](tests/fixtures/README.md#mail-test-fixtures) also include everyday messages and conversation history.

## Build and test

Build the Windows app with the .NET 10 SDK. The attachment preview worker and CLI use Rust.

```powershell
cargo build --manifest-path preview/Cargo.toml --release --locked
cargo build --manifest-path engine/Cargo.toml --release --locked
dotnet build MailViewer.slnx -c Release
dotnet run --project tests/MailViewer.Verification -c Release -- --synthetic-only
```

[CLI commands](docs/rust-mail-cli.md) · [Fixtures and test commands](tests/fixtures/README.md) · [UI tests](docs/ui-verification.md) · [Build the EWF DLL](third_party/ntfs-tracer/modules/EvidenceImage/Logical/Native/README.md) · [Licenses](licenses/)

Use `tools/publish-mail-evidence.ps1` to publish the Windows builds. To refresh the English README media on Windows, build the preview worker as shown above, install Pillow, and run `python tools/update-readme-media.py`.

## Display language

Choose from 16 interface languages under **View → Display language**. Changes apply immediately.

English · 한국어 · 日本語 · Deutsch · Español · Français · Bahasa Indonesia · Italiano · Polski · Português (Brasil) · Türkçe · Tiếng Việt · Русский · Українська · 简体中文 · 繁體中文

## License

Project-owned code is licensed under the [Apache License 2.0](LICENSE).

Third-party components retain their own licenses. See the [third-party license notices](licenses/).
