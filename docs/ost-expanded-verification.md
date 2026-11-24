# Legacy OST and block encoding verification

23 November 2026, Mail Viewer 1.4.0 Release. Samples were classified by the `SO` signature in the file header, NDB version, and block encoding. Sources with known provenance were pinned by SHA-256 for local verification. [MS-PST file header](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/c9876f5a-664b-46a3-9887-ba63f113abf5) · [libpff PFF format specification](https://github.com/libyal/libpff/blob/main/documentation/Personal%20Folder%20File%20%28PFF%29%20format.asciidoc)

## Samples and test copies

| Input | Provenance and header values | SHA-256 |
|---|---|---|
| `SampleOstFile.ost` | Original public sample. `!BDN`, `SO`, Unicode 23, 512-byte index pages, Permute `0x01`, 4,080,640 bytes. This sample covers a legacy layout and block encoding together. [Original Aspose sample](https://github.com/aspose-email/Aspose.Email-for-.NET/blob/88cb2da311382bbbba6d14813127b0e6b3854c12/Examples/Data/MAPI/SampleOstFile.ost) | `5ff8bc133935a03fad8241f271a604e1cc41e36640d6538d6bcc9bd39e6d9866` |
| `generated-aspose-v23-cyclic.ost` | Test copy with 906 external data blocks re-encoded as Cyclic `0x02`. This copy was generated for the test. [MS-PST Cyclic rules](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/9979fc01-0a3e-496f-900f-a6a867951f23) | `b8153bd133cf20313a8ec59b2674350060186d165ed9d196706890b242700aee` |
| `generated-aspose-v21.ost` | Copy of the Aspose sample with an updated NDB version and header CRCs to exercise Unicode 21. An independent reader found the same folders, messages, and properties as in the original. | `9452cb9401e2f3bbd4830599016477cd43e9e30ebdaa8a90579c54bd3595a0cb` |
| `generated-sdk-v14.ost` | Copy of the public Microsoft PST SDK ANSI sample with the client signature set to `SO` and the header CRC updated. It exercises ANSI 14, 512-byte pages, and Permute. [Microsoft PST SDK sample copy](https://github.com/emk/pstsdk/blob/21c7d2d57bd09555afd0a08adc966f9b3779cb19/test/sample2.pst) · [Sample provenance and license](https://github.com/bslater/bodu/blob/76abdd27524f59853bd1b994bbf1f2c0e67b62d6/Bodu.IO.Pst/test/Fixtures/Reference/NOTICE.md) | `adf2a442715a56d99a2559c87753a39a3c54b3c925d7f78a4622c19a01e3e10e` |
| `generated-sdk-v15.ost` | Copy of the same ANSI PST with `SO`, NDB version 15, and updated header CRCs. Both ANSI copies yielded the same five folders and one message as the original in an independent reader. | `9625470a5ed8a8687e45a406f04fbc49017498f419a3bfee8638ec9b59968392` |

The `SM` signature identifies Aspose's `PersonalStorageFile.ost` and `test.ost` as PST files. The tests used the generated copies above for ANSI 14 and 15, Unicode 21, and Cyclic `0x02`. Outlook-generated OST examples for those combinations were unavailable. Two previously verified Unicode 36 OST files and a Unicode 23 PST were included in the regression run. [Aspose sample directory](https://github.com/aspose-email/Aspose.Email-for-.NET/tree/88cb2da311382bbbba6d14813127b0e6b3854c12/Examples/Data/MAPI) · [PST verification](pst-verification.md)

## Independent reference values

A separate installation of `libpff-python` 20261030 found 37 regular folders, 81 supported messages, 66 transport header properties, and 1,237 header lines in the original Aspose sample. Of 35 attachments, 22 were by-value attachments with stored bytes. Their full SHA-256 hashes and 34 partial ranges were checked. The remaining 13 attachments could not supply ordinary file bytes. [libpff release](https://pypi.org/project/libpff-python/20261030/) · [Reference values](../tests/fixtures/ost-expanded-oracle.json)

Mbox Viewer's `pst-extractor` 1.12.0 provided a second reading of the original. Both readers found the same 37 regular folders and the 80 `IPM.Note` messages selected by the script. The property tags, lengths, and SHA-256 hashes matched for 65 shared transport headers and 22 by-value attachments. `libpff-python` also returned one delivery status report, `REPORT.IPM.Note.NotSupportedICal.NDR`.

| Evidence | Independent reading |
|---|---|
| Folder and message | `IPM_SUBTREE` NID `0x00002142` → `Inbox` NID `0x00002182` → `newsletter copy` NID `0x00214D84`. `Inbox` contains 11 regular messages. |
| Transport headers | MAPI `0x007D001F`, 1,872 bytes. SHA-256 `3164331448fbd9e9c5e4253911387b1756110252672ddc65c37a74b14e31c3e4`. `Received` occupies the first 338 bytes at property-relative offset 0. |
| Attachment | `image001.jpg`, attachment NID `0x000082A5`, MAPI `0x37010102`, 20,833 bytes. SHA-256 `0f4ff85802be13ad86f82b7e6e886e5a9a6a77ddb84995bc5a7657a3d40c8f9a`. |

## Console results

The Release build had zero warnings and errors. All 26 checks passed with `--ost --ost-expanded --large`. Three new checks covered the following behavior. [Expanded OST test code](../tests/MailViewer.Verification/ExpandedOstChecks.cs)

- Folder names and parent NIDs, message NIDs and subjects, and the complete bytes of stored transport headers and attachment properties matched the independent references for the legacy sample and each test copy. Messages without headers had no source bytes or property locations.
- Searching `Inbox` for `image001.jpg` found one message in the current source. Searching only the parent folder returned zero results. Including subfolders returned two messages from the inbox and sent folder. Source IDs kept results distinct where the Permute original and Cyclic copy had the same NIDs.
- Exported transport headers and attachments from the Permute original and Cyclic copy matched the independent SHA-256 hashes. The manifest retained the source SHA-256, NDB version, page size, block encoding, and folder scope. Changing the last byte of an exported attachment without changing its length caused verification to fail. Running the same search selected the original message again.
- A damaged 512-byte NBT root and an unsupported WIP encoding produced distinct results. The healthy PST and two OST files remained searchable in the same session, and their SHA-256 hashes matched before and after testing.

See the [Windows UI verification](ui-verification.md) for folder navigation, export verification, and return to the prior search.

Run from the repository root to prepare the OST inputs and verify them. [Fetch the samples](../tests/fixtures/make-ost-oracle.py) · [Generate test copies](../tests/fixtures/encode-ost-control.py)

```powershell
python tests/fixtures/make-ost-oracle.py --fetch-only
python tests/fixtures/make-ost-oracle.py --expanded --fetch-only
dotnet build MailViewer.slnx -c Release -warnaserror
dotnet run --project tests/MailViewer.Verification -c Release --no-build -- --ost --ost-expanded --large
```
