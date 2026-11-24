# Windows UI verification

Build the Release configuration from the repository root. Pass a new output directory to each command. The tests run on a separate Windows desktop and save the result JSON and screenshots in that directory.

```powershell
dotnet build MailViewer.slnx -c Release
python tools/run-flow-smoke.py artifacts/smoke-attachment-boundaries --scenario attachment-boundaries --theme dark
python tools/run-flow-smoke.py artifacts/smoke-investigation --scenario investigation --theme light
python tools/run-flow-smoke.py artifacts/smoke-languages --scenario languages
```

To test with original EML, MBOX, MSG, PST, or OST samples, prepare the [format-specific fixtures](../tests/fixtures/README.md) and pass `--scenario` with `mail`, `msg`, `pst`, `ost`, or `ost-expanded`.

## Evidence checked by format

These are representative values checked in the Windows UI on 23 November 2026. Offsets are relative to the source indicated for each format.

| Format | UI checks |
| --- | --- |
| EML and MBOX | The MIME range for `Test TxtA.txt` in the public EML is 45 bytes at file offset `0x575`. The exported, decoded attachment is 33 bytes. The four `mime` subject matches in the public Apache MBOX appeared in the same order in the CSV. [EML and MBOX reference values](verification.md) |
| MSG | The ANSI `0x007D001E` and Unicode `0x007D001F` transport headers matched their CFB streams. The Apache POI PDF attachment starts with `%PDF-` at stream offset 0 and is 13,539 bytes long. [MSG reference values](msg-verification.md) |
| PST | The transport header property for Tika message NID `0x00200024` is 10,660 bytes. In the libpff sample's `Inbox`, `1.jpg` has attachment NID `0x00008025`, property `0x37010102`, and length 1,969 bytes. [PST reference values](pst-verification.md) |
| OST | The Aspose sample yielded 81 regular messages. The `Received` header in `newsletter copy` occupies the first 338 bytes of the decoded transport header property. `image001.jpg` has attachment NID `0x000082A5` and length 20,833 bytes. [OST reference values](ost-expanded-verification.md) |

For each format, the original SHA-256 matched before and after export. The search text and selected message were preserved when returning from export verification. The test exercised WPF logical focus and keyboard events.
