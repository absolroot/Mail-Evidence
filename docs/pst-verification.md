# PST reading and export verification

## Public samples and independent reference values

The tests used Mbox Viewer's `pst-extractor` 1.12.0, separate from the app's parser, to open PST folders and messages. The reference tool extracted transport headers and attachments directly as MAPI property bytes before any text or EML conversion. Virtual search folders were excluded from regular folder traversal. [Reference bytes, NIDs, and SHA-256 hashes](../tests/fixtures/pst-oracle.json)

| Sample | Independent reading |
|---|---|
| Apache Tika `testPST.pst` | Unicode v23, 271,360 bytes, SHA-256 `acad1e91d58d51494b459d986a1550b352031f0088a90f8e68d5ef449f3e9f5e`. The folder `Début du fichier de données Outlook`, NID 32802, has six messages. Each `0x007D001F` property has 50 `Received` headers. The messages have zero attachments. [Fixture path and download](../tests/fixtures/README.md) |
| libpff `outlook.pst` | Unicode v23, 271,360 bytes, SHA-256 `df01707f76d0e24ab913cf1ffeffa6eaf9c1d590e02102c7ed26bb3ac51d4e24`. `Inbox` (NID 33090) and `Recovered_Group2` (NID 33410) each contain a message with the same subject and 11 attachments. In `Inbox`, `1.jpg` belongs to message NID 2097188. It has attachment NID 32805, property `0x37010102`, length 1,969 bytes, and SHA-256 `0c7da5374fe7e65b84521e111a584c5508eb40e1f8f044e4ff9416740d601311`. [Pinned libyal sample](https://github.com/libyal/testdata/blob/c3631ff17ce4ae6798ed9abd065c6a67ad626bce/pst/outlook.pst) |

The additional sample was published by libyal/testdata for libpff tests. Only its local filename was changed to identify where it came from. [libpff test configuration](https://github.com/libyal/libpff/blob/main/libpff.ini)

## Run the tests

```powershell
dotnet build tests/MailViewer.Verification/MailViewer.Verification.csproj -c Release -warnaserror
dotnet run --project tests/MailViewer.Verification/MailViewer.Verification.csproj -c Release --no-build -- --large
```

The console runner includes checks for folders and original transport headers, folder scope when attachments share names, CSV export and original property export, and isolation of damaged files. [PST test code](../tests/MailViewer.Verification/PstChecks.cs)

## Results

On 23 November 2026, the Release build completed with zero warnings and errors. All **19 checks passed**, covering four new PST cases and 15 existing EML, MBOX, and MSG cases.

| Test | Verified result |
|---|---|
| Tika folders and transport headers | The folder hierarchy and message NIDs matched. A search in the sample folder found two subjects matching `Feature Generators` and one body matching `java.lang.VerifyError`. The test compared the complete `0x007D001F` properties and SHA-256 hashes for all six messages, along with each header line's property-relative offset and bytes. All 50 `Received` lines remained in source order. |
| libpff folder scope and attachments | A search in either `Inbox` or `Recovered_Group2` returned one message from that folder despite duplicate subjects and attachment names. Searching only the parent folder returned zero messages. Including subfolders returned two. Source IDs distinguished equal NID values in the two PST files. The bytes, lengths, SHA-256 hashes, and property-relative ranges crossing 512-byte boundaries matched independent references for 22 attachments. |
| Search and export | The CSV included one record from the selected folder. The header `.bin` in the PST evidence package matched the original property bytes. Messages without headers produced metadata only. The exported `1.jpg` matched the original property bytes and SHA-256. The manifest retained the folder, message, and attachment NIDs and the MAPI tag. A tampered export of the same length failed verification. |
| Fault isolation and read-only access | The test opened a file truncated to 1,024 bytes alongside a healthy original. The damaged input produced a separate report, and all six messages from the healthy original remained available. Windows denied write access to the open original, and the two PST hashes matched before and after testing. |

## Coverage

The two public PST files are healthy Unicode v23 samples. Transport headers and file attachments are recorded as logical locations within MAPI properties. Separate tests are needed for ANSI, other Unicode versions, deleted item recovery, embedded-message attachment extraction, and throughput on large real PST files. The MBOX regression test covers 4 GiB offsets. The PST workflow review documents preservation of folder and search state in the UI.
