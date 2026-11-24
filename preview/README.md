# Attachment preview

Select a file in **Attachments** to open its preview on the right. Drag the divider between the file list and the preview to adjust their widths. **File information** shows the file size, MIME type, SHA-256, and source location.

| Attachment | Preview |
| --- | --- |
| PDF, HWP, HWPX, DOCX | First page |
| JPG, JPEG, PNG | Image |
| TXT, CSV, and other text files | First 32 KiB. Supports UTF encodings and CP949. |
| BIN, DAT | Printable ASCII from the first 8 KiB when the file cannot be displayed as text. |

The Rust worker renders documents and images inside a Windows AppContainer without network access. It receives the attachment bytes and returns an image up to 1,600 pixels along its longest side. The renderer may substitute fonts installed on the system.

- The attachment input limit is 24 MiB.
- ZIP inputs may contain up to 4,096 entries with a combined declared uncompressed size of up to 96 MiB.
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
