# Windows mail formats and source locations

The Windows app bundles the Rust reader starting with 1.6.0. It reads ANSI PST files and PST variants outside the C# reader's coverage. The same source picker opens DBX, MBX, OLM, EMLX, MMDF, Babyl, Rmail, ZIP, standalone TNEF, and CMS SignedData. Open Maildir and MH by selecting their directories with their original layout intact. The analysis results show Outlook Express auxiliary indexes and MH sequences.

| Source | How the app locates the evidence |
| --- | --- |
| EMLX, MMDF, Babyl, MBX, supported CMS | Byte ranges in the source file. The EMLX length prefix and trailing plist are outside the message range. |
| DBX | Reads the segments linked by the message index in order. The hex view shows an offset relative to the stored item, and the manifest records its segments. |
| ZIP and OLM | Records the member name, index, and byte range within the member. Hex offsets are relative to the decompressed member. |
| Partial EMLX | Records the path and SHA-256 for matching external files. The app checks them again on export to detect later changes. |
| ANSI PST and embedded MSG | Displays the PST folder and message NIDs and MAPI properties, or the MSG CFB storage and streams. Embedded MSG is preserved as CFB storage and streams. |

Search and export available headers, bodies, and attachments. Inspect available source bytes in the hex view or an evidence package. The CLI also offers profile collection commands and an Outlook Express file relationship report. Some attachments opened through the extended readers have limited structure checks. EDB and NSF support covers format identification. CMS support is limited to reading embedded mail in SignedData.

## Verification coverage

- Public Outlook Express DBX and MBX, Outlook for Mac OLM, Microsoft PST SDK ANSI 14 PST, standalone TNEF, and CMS SignedData.
- Synthetic ZIP, EMLX and partial EMLX with external attachments, MMDF, Babyl, Maildir, MH, and NWS.
- Twenty-nine .NET checks covered reading, exporting, and verifying EML, MBOX, MSG, PST, OST, and the extended formats.
- In an isolated Windows desktop, the test searched a ZIP, compared header bytes and attachment source locations, and exported an attachment. The EMLX view opened at the file's actual message offset after the length prefix. The EML, MBOX, and MSG UI regression tests also passed.
- Both Windows x64 packages, compact and with runtime, opened an EMLX and displayed its subject in the message list.

[.NET format tests](../tests/MailViewer.Verification/Program.Formats.cs) · [Windows UI tests](../tools/FlowSmoke/Program.Formats.cs) · [Rust CLI verification and samples](rust-mail-cli.md#verification)
