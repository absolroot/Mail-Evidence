# Mail Evidence

<p align="center"><img src="assets/MailViewer.svg" alt="Mail Evidence logo" width="72"></p>

<h3 align="center">A user-friendly email analysis tool built around the analyst’s workflow</h3>

[한국어](README.md) · [English](README.en.md) · [日本語](README.ja.md)

[Windows x64 compact](https://github.com/absolroot/Mail-Evidence/releases/download/v1.6.0/Mail-Evidence-v1.6.0-win-x64-compact.zip) · [Windows x64 with runtime](https://github.com/absolroot/Mail-Evidence/releases/download/v1.6.0/Mail-Evidence-v1.6.0-win-x64-standalone.zip) · [Rust CLI 0.4.0](https://github.com/absolroot/Mail-Evidence/releases/download/v1.5.7/Mail-Evidence-CLI-v0.4.0-win-x64.zip)

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

HTML messages block external images, scripts, and link navigation by default. HTML rendering uses [Microsoft Edge WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

**Settings → Display language** offers Korean, English, Japanese, German, Spanish, French, Indonesian, Italian, Polish, Brazilian Portuguese, Turkish, Vietnamese, Russian, Ukrainian, and Simplified and Traditional Chinese.

## Supported inputs

### Mail files and stores

The Windows app can open the inputs below directly starting with **1.6.0**. The readers are bundled with the app, so a separate CLI installation is unnecessary.

| Input | What the app can read |
| --- | --- |
| Internet mail `.eml`, `.nws`, `.mime`, `.rfc822` and mailbox `.mbox` | Headers, bodies, and MIME attachments. An MBOX envelope can identify a mailbox without an extension. |
| Outlook `.msg` | ANSI and Unicode messages, bodies, transport headers, and regular attachments. Embedded messages can be opened as separate entries. |
| Outlook `.pst`, `.ost` | Folders, local mail, available transport headers, and regular attachments from supported ANSI and Unicode stores. |
| Outlook Express `.dbx`, `.mbx` | Mail and attachments from Outlook Express 5–6 DBX and Outlook Express 4 JMF6 MBX. |
| Outlook for Mac `.olm` | Mail and linked attachments, with the location inside the ZIP archive. |
| Apple Mail `.emlx`, `.partial.emlx` | The stored message byte range. Partial EMLX also supports external attachments with matching locations and sizes. |
| `.mmdf`, `.babyl`, `.rmail`, Maildir, MH | Mail and MIME attachments. Open Maildir and MH by selecting the folder. |
| Mail archives `.zip` | EML, EMLX, and MBOX members inside the ZIP. |
| Standalone TNEF `.tnef`, `winmail.dat` | Stored regular attachments. |
| CMS SignedData `.p7m`, `.p7s` | Mail embedded in DER SignedData. Signature verification and decryption are outside the supported coverage. |

PST and OST files are limited to **2 GiB** each. The app reads 512-byte pages in ANSI 14–15 and Unicode 21 and 23 with encodings 0–2, and 4 KiB pages in Unicode 36 with encoding 0. EDB and NSF support stops at format identification. The new Outlook cache, protected OST files, server-only mail, and deleted-item recovery are outside the supported coverage. [Windows format coverage](docs/windows-format-coverage.md) · [CLI format notes](docs/rust-mail-cli.md#입력과-첨부)

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
| Documents | First page of `.pdf`, `.hwp`, `.hwpx`, and `.docx` |
| Images | `.jpg`, `.jpeg`, `.png` |
| Text | Excerpts from `.txt`, `.log`, `.csv`, `.tsv`, `.json`, `.xml`, `.md`, `.ini`, `.cfg`, `.yaml`, `.yml`, and `text/*` attachments |
| Binary data | Excerpts from `.bin` and `.dat`, with printable ASCII where text decoding is not available |

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
