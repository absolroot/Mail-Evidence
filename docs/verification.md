# Windows mail evidence verification

On 23 November 2026, the tests used .NET SDK 10.0.102 on Windows 11 Pro build 26200 at standard integrity level. The console runner uses the same reader and export service as the UI. The test code reads the originals and exports separately and compares their SHA-256 hashes, byte ranges, order, and CSV rows.

## Run the tests

```powershell
dotnet build tests/MailViewer.Verification/MailViewer.Verification.csproj --nologo
dotnet run --project tests/MailViewer.Verification/MailViewer.Verification.csproj -- --large
```

The build produced zero warnings and errors. **All 10 checks passed.** On Windows, omitting `--large` skips the 4 GiB boundary test, which uses a sparse file. [Test code](../tests/MailViewer.Verification/Program.cs) · [4 GiB boundary test](../tests/MailViewer.Verification/LargeMbox.cs)

## Inputs and independent reference values

| Input | Source and reference values | Verified result |
|---|---|---|
| Synthetic EML | 1,148 bytes. SHA-256 `9a8106fd358cd0be272e515248196d4fd6cb31a21baae41607bc5360059c61ad`. | The original bytes of the folded `Received` fields and the order of both fields matched. The `+0900` timezone was preserved and the normalized time was 03:00 UTC. Terms specific to the plain-text and HTML parts matched the corresponding MIME parts of the same message. The hash of the eight-byte attachment also matched. |
| Synthetic MBOX | SHA-256 `390e94e0e8755c5230eadc30f488550d99700ddc70296f27a418f9a7ade36b95`. | Message offset and length pairs were `50/1149`, `1249/304`, and `1603/315` bytes. The message bytes and SHA-256 hashes matched, and the two-byte `CRLF` separator remained a separate range. Both messages with the same Message-ID were preserved. The last message had an envelope time of 03:03 and a Date header of 03:02. Message Date governed date-range inclusion. |
| Public Apache Tika EML fixture | 1,904 bytes. SHA-256 `86e5feb18e16178359d6aaa704bd74ceebe7a41f302df5b656f5e6fe0ea34cc5`. [Pinned public sample](https://github.com/apache/tika/blob/f1f785f533877ce7426f45b1460ed52e154bcaca/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-mail-module/src/test/resources/test-documents/testRFC822_multipart_attachments.eml) | `Test TxtA.txt` and `Test TxtB.txt` were found by name. The decoded results matched the Python standard MIME parser in length (33 bytes) and SHA-256. Exported attachment A had SHA-256 `710b0ea810c12c93cae9982b60ca333e5302e72b8948b194f611513d4737c97d`. |
| Public Apache Tika developer MBOX | February 2008 archive, 139,161 bytes. SHA-256 `76f15052d9cd1a3c8157d945f2a3778f469f36d550bffd27c986bd6f0df8bfe7`. Python's standard `mailbox` module and the raw envelope lines both indicated 36 messages. [Apache download](https://mail-archives.apache.org/mod_mbox/tika-dev/200802.mbox) | The app read all 36 messages. Subject searches returned 19 matches for `tika` and four for `mime`. The source offsets for `mime` were 32,630, 35,955, 39,859, and 44,219. The first result had a 2,059-byte source header and eight `Received` lines. The CSV and manifest preserved the order and ranges of all four selected records. |

## Exports and failure handling

- EML and MBOX source ranges were exported separately. Their SHA-256 hashes and source offsets matched the manifest. Replacing bytes without changing the length or deleting an export caused verification to fail. Both input SHA-256 hashes matched before and after export.
- The service rejected an export request with a required search result omitted. A valid export saved the results to CSV in the requested order. A formula-like subject containing a comma and Korean text acquired the `[text] ` prefix in CSV, while the reader preserved the original subject.
- Open and export operations returned a cancellation result when canceled. A canceled export produced no output directory. Exporting the same scope again preserved the previous result. Windows denied writes while the app held an open handle to the original. After closing the handle and replacing the input with different content of the same size and modification time, reopening returned a new search result and hash.
- The sparse MBOX was 4,294,968,626 bytes long. The reader skipped the first oversized message and recorded the reason. It found the following message at byte 4,294,967,475, compared 1,149 bytes with the source, and successfully exported and verified them. The search result manifest recorded the skipped range. This checks 64-bit offsets. General throughput on large collections requires a separate measurement.

These console checks cover parsing, source evidence, and export. Forced process termination, all MBOX variants and forms of corruption, controlled network capture, and formal performance comparisons are outside the scope of this record.
