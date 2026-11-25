# Attachment preview

Select a file in **Attachments** to open its preview on the right. Drag the divider between the file list and the preview to adjust their widths. **File information** shows the file size, MIME type, SHA-256, and source location.

| Attachment | Preview |
| --- | --- |
| PDF, HWP, HWPX, DOCX | First page |
| DOC, PPT, PPTX | Document or slide text |
| XLS, XLSX | Sheet names and cell values as text |
| ZIP, TAR, 7z | File and folder names |
| JPG, JPEG, PNG | Image |
| TXT, CSV, and other text files | First 32 KiB. Supports UTF encodings and CP949. |
| BIN, DAT | Printable ASCII from the first 8 KiB when the file cannot be displayed as text. |

The Rust worker renders documents and images inside a Windows AppContainer without network access. It receives the attachment bytes and returns an image up to 1,600 pixels along its longest side or up to 64 KiB of Office text or archive names. The renderer may substitute fonts installed on the system.

- The attachment input limit is 24 MiB.
- ZIP inputs may contain up to 4,096 entries. Office and HWPX packages also have a combined declared uncompressed size limit of 96 MiB.
- Archive previews list entries without extracting files. Encrypted ZIP and 7z files show a password-protection notice alongside readable names. When the 7z file names are encrypted, the preview reports that a password is required. TAR has no built-in password protection.
- The worker has a memory limit of 448 MiB. Renderer startup and response have an 8-second timeout.

**HEX** opens the stored MIME byte range, MSG stream, or PST or OST property. When opened from an attachment structure finding, the offset is relative to the decoded attachment. [Attachment structure checks](../docs/attachment-signatures.md)

## Build

Run from the repository root on Windows x64. The publish script compresses the worker with Brotli and embeds it in the app.

```powershell
$env:RUSTFLAGS = '-C target-feature=+crt-static'
cargo build --manifest-path preview/Cargo.toml --release --locked
pwsh -NoProfile -File tools/publish-mail-evidence.ps1 -OutputDirectory artifacts/mail-evidence -Variant Both
```

[Renderer dependencies](Cargo.toml) · [License notices](../licenses/mail-attachment-preview.txt) · [Preview UI tests](../tools/FlowSmoke/Program.ReaderAttachments.cs)
