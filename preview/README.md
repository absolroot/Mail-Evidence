# Attachment preview

Select a file in **Attachments** to open its preview on the right. Drag the divider between the file list and the preview to adjust their widths. **File information** shows the file size, MIME type, SHA-256, and source location.

| Attachment | Preview |
| --- | --- |
| PDF, HWP, HWPX, DOC, DOCX | First page as an image |
| PPT, PPTX | First slide as an image |
| XLS, XLSX | First worksheet excerpt as an image |
| ZIP, TAR, 7z | File and folder names |
| JPG, JPEG, PNG | Image |
| TXT, CSV, and other text files | First 32 KiB. Supports UTF encodings and CP949. |
| BIN, DAT | Printable ASCII from the first 8 KiB when the file cannot be displayed as text. |

The Rust worker renders documents and images inside a Windows AppContainer without network access. It receives the attachment bytes and returns an image up to 1,600 pixels along its longest side or up to 64 KiB of archive names. The renderer may substitute fonts installed on the system. DOC and PPT are converted in memory before rendering with rdocx and rpptx. Their layout can differ from Word or PowerPoint. The worksheet image shows up to 32 rows and 10 columns, with cell values, grid lines, and available basic styling.

- The attachment input limit is 24 MiB.
- ZIP inputs may contain up to 4,096 entries. Office and HWPX packages also have a combined declared uncompressed size limit of 96 MiB.
- Archive previews list entries without extracting files. Encrypted ZIP and 7z files show a password-protection notice alongside readable names. When the 7z file names are encrypted, the preview reports that a password is required. TAR has no built-in password protection.
- The worker has a memory limit of 448 MiB. Renderer startup and response have an 8-second timeout.

**HEX** opens the stored MIME byte range, MSG stream, or PST or OST property. When opened from an attachment structure finding, the offset is relative to the decoded attachment. [Attachment structure checks](../docs/attachment-signatures.md)

## Windows verification

An EML with 13 attachments was opened on an isolated Windows desktop. Apache POI samples covered DOC and DOCX first pages, PPT and PPTX first slides, and the first XLS and XLSX worksheets. A modified test deck verified the slide background color. The DOC sample has an explicit page break and the second page was outside the preview.

The narrow attachment view and the inline PPTX preview were also checked. Plain ZIP, TAR and 7z listings and ZIP/7z password states were verified with generated fixtures. The EML SHA-256 matched before and after the run. [Apache POI test files](https://github.com/apache/poi/tree/trunk/test-data)

## Build

Run from the repository root on Windows x64. The publish script compresses the worker with Brotli and embeds it in the app.

```powershell
$env:RUSTFLAGS = '-C target-feature=+crt-static'
cargo build --manifest-path preview/Cargo.toml --release --locked
pwsh -NoProfile -File tools/publish-mail-evidence.ps1 -OutputDirectory artifacts/mail-evidence -Variant Both
```

[Legacy Word conversion](https://docs.rs/rwml/0.1.5/rwml/) · [PowerPoint rendering](https://docs.rs/rpptx/0.15.0/rpptx/) · [Renderer dependencies](Cargo.toml) · [License notices](../licenses/mail-attachment-preview.txt) · [Preview UI tests](../tools/FlowSmoke/Program.ReaderAttachments.cs)
