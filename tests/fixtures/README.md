# Mail test fixtures

| Sample | What to try |
| --- | --- |
| [Anomalies](anomaly-ux-complete/) | Header, link, and attachment findings, including checks that could not be completed |
| [Attachment boundaries](attachment-boundaries/) | File signatures, length fields, missing end markers, and trailing bytes |
| [Conversation history](conversation-history/) | Reply-To changes and related messages |
| [Everyday mail](everyday/) | Order receipt, table reservation, attachments with matching names, and a long subject line |
| [Harbor Market demo mailbox](readme-demo/) | 24 inbox and sent messages, image previews, attachment structure findings, and a Reply-To domain mismatch |

## Run with synthetic fixtures

Run from the repository root. See [Windows UI verification](../../docs/ui-verification.md) for desktop tests.

```powershell
dotnet run --project tests/MailViewer.Verification -c Release -- --synthetic-only
```

## Run with public samples

Save each file at the indicated path relative to this directory. The test runner checks the SHA-256 of each input. The small Apache POI MSG fixtures and their license are included in [poi-msg](poi-msg/).

| Save as | Source |
| --- | --- |
| `external/tika-multipart-attachments.eml` | [Download](https://raw.githubusercontent.com/apache/tika/f1f785f533877ce7426f45b1460ed52e154bcaca/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-mail-module/src/test/resources/test-documents/testRFC822_multipart_attachments.eml) |
| `external/public-apache-tika.mbox` | [Download](https://mail-archives.apache.org/mod_mbox/tika-dev/200802.mbox) |
| `external/tika-test-outlook.msg` | [Download](https://raw.githubusercontent.com/apache/tika/f1f785f533877ce7426f45b1460ed52e154bcaca/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/resources/test-documents/test-outlook.msg) |
| `external/poi-attachment-msg-pdf.msg` | [Download](https://raw.githubusercontent.com/apache/poi/c94ee8ad9096bdb8cf8e5e59d8d12432e950dbf3/test-data/hsmf/attachment_msg_pdf.msg) |
| `external/tika-testPST.pst` | [Download](https://svn.apache.org/repos/asf/tika/branches/2.x/tika-test-resources/src/test/resources/test-documents/testPST.pst?p=1940239) |
| `pst/libyal-outlook.pst` | [Download](https://raw.githubusercontent.com/libyal/testdata/c3631ff17ce4ae6798ed9abd065c6a67ad626bce/pst/outlook.pst) |

Use the commands below to fetch the OST samples and prepare the test copies. Add `--large` to the test command to check MBOX offsets beyond 4 GiB.

```powershell
python tests/fixtures/make-ost-oracle.py --fetch-only
python tests/fixtures/make-ost-oracle.py --expanded --fetch-only
dotnet run --project tests/MailViewer.Verification -c Release -- --ost --ost-expanded
```

[EML and MBOX verification](../../docs/verification.md) · [MSG verification](../../docs/msg-verification.md) · [PST verification](../../docs/pst-verification.md) · [OST verification](../../docs/ost-expanded-verification.md)
