# MSG reading and export verification

On 23 November 2026, the Release build passed **15 of 15 checks**. The run added five MSG checks and included the ten existing EML and MBOX checks, including the 4 GiB boundary case. The console runner uses the same reader and export service as the app.

```powershell
dotnet build tests/MailViewer.Verification/MailViewer.Verification.csproj -c Release -warnaserror
dotnet run --project tests/MailViewer.Verification/MailViewer.Verification.csproj -c Release --no-build -- --large
```

## Independent reference values

Python `olefile` 0.47 read the CFB streams directly to record their complete bytes and SHA-256 hashes. Header field order and offsets came from those streams. The console runner compares these references with the application's stream reads, the field ranges used by the UI, and the exported files. [Reference bytes and hashes](../tests/fixtures/msg-oracle.json) · [Test code](../tests/MailViewer.Verification/MsgChecks.cs)

| Public sample | Source evidence and results |
|---|---|
| Apache Tika Outlook Express MSG | The root stream `/__substg1.0_007D001E` is 377 bytes long and its SHA-256 is `ac174e5029430171be1729c355ee41071facfc5369ccfaaaa1d9a888ab5d9af6`. The reader preserved all eight fields in order. The original `Date` was `Thu, 5 Apr 2007 09:26:06 -0700`, which is 16:26:06 UTC. The `Content-Type` at stream offset 222 was `text/html`. The check also confirmed `X-MimeOLE`. Searching for subject `Express 6` or body text `Synchronisation hors connexion` each returned one match. [Fixture path and download](../tests/fixtures/README.md) |
| Apache POI PDF and embedded MSG | The Unicode header stream `/__substg1.0_007D001F` is 1,574 bytes long and its SHA-256 is `faea65c3fae66107d7d1fb63b26edb5b28c3c26619230f1a8012241f1f681d4c`. The check confirmed the order and offsets of all 17 fields. The subject `test email` and attachment name `KdcPjl.pdf` matched the same record. The PDF stream `/__attach_version1.0_#00000001/__substg1.0_37010102` is 13,539 bytes long and its SHA-256 is `1bd629440fff7a30e340c95e51f2732f239ff7115be211aaa23ba498d0f1b208`. The complete stream and a 43-byte range starting at logical offset 4,090 both matched. The embedded object appeared as storage `/__attach_version1.0_#00000000/__substg1.0_3701000D`. [Pinned sample](https://github.com/apache/poi/blob/c94ee8ad9096bdb8cf8e5e59d8d12432e950dbf3/test-data/hsmf/attachment_msg_pdf.msg) |
| Apache POI duplicate headers, absent headers, and HTML-only MSG | In `simple_test_msg.msg`, two `Received` fields remained in their original order at logical offsets 134 and 380. The bytes of the folded lines matched the source. In `blank.msg`, the test verified the absence of the transport header property. In `HTMLBodyBinary_UTF-8.msg`, a search for the rendered text `HTML öäü` matched the message. [Pinned fixtures and notices](../tests/fixtures/poi-msg/) |

## Exports and failure handling

- **MSG source copies.** Both representative inputs were exported as `.msg` files. Every byte and the SHA-256 hashes matched the originals. The manifest retained each record's header status, CFB stream name, MAPI tag, and length.
- **PDF attachment export.** The exported by-value CFB stream matched the independent reference byte for byte. The manifest recorded the storage path, stream name, and MAPI tag. The evidence location is relative to the CFB stream.
- **Search results CSV.** A search by subject exported one record with its ID and subject. Verification confirmed the file sizes and SHA-256 hashes of the MSG, PDF, and CSV packages. Both original MSG hashes matched before and after export.
- **Damaged input.** A test copy retained only the first 512 bytes of a sample. The healthy MSG opened in the same session remained searchable, and the damaged input had a recorded read error. Windows denied a write handle to the original while the investigation session held it open.

## Coverage

The test confirmed storage locations for embedded MSG objects. Exporting those objects as standalone MSG files is outside the app's coverage. Nine additional ordinary POI MSG files were checked at the same pinned commit. Every inspected compressed RTF body had a plain-text body alongside it. RTF-only recovery accuracy, MAPI code page coverage, empty headers, and isolated header-stream corruption were outside this test. The MSG file and storage layout and transport header types follow the public Microsoft specifications. [MS-OXMSG](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxmsg/) · [Transport header property](https://learn.microsoft.com/en-us/office/client-developer/outlook/mapi/pidtagtransportmessageheaders-canonical-property) · [EML and MBOX verification](verification.md)
