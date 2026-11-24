# Rust mail CLI 0.4.0

List messages and export attachments from the command line on Windows x64. Pass files and folders together. The CLI searches folders recursively. [Download the CLI](https://github.com/absolroot/Mail-Evidence/releases/download/v1.5.7/Mail-Evidence-CLI-v0.4.0-win-x64.zip)

```powershell
./mailviewer-engine.exe report D:/Evidence/Mail
./mailviewer-engine.exe report --csv D:/Evidence/Mail | Set-Content -Encoding utf8 D:/Cases/messages.csv
$result = ./mailviewer-engine.exe extract D:/Cases/Output D:/Evidence/Mail | ConvertFrom-Json
./mailviewer-engine.exe verify $result.directory
```

| Command | Output |
| --- | --- |
| `report` | JSON with the source paths and SHA-256 hashes, message metadata, bodies, headers, and attachment locations. Includes missing values and read errors. |
| `report --csv` | A UTF-8 message list. Values that could be interpreted as formulas are prefixed with `[text] `. |
| `extract` | A new output directory for each run with `messages.csv`, `manifest.json`, and `attachments/`. Checks attachment lengths and hashes and saves attachments with the same name as separate files. |
| `verify` | Checks file lengths and SHA-256 hashes against the manifest. Returns an error if a file is missing or has changed. |

## Local mail stores

| Source | Input |
| --- | --- |
| Maildir and MH | For Maildir, select the folder containing `cur/` and `new/`. For MH, select the folder containing numerically named messages and `.mh_sequences`. |
| Apple Mail | Preserve the relative paths of `Messages/` and `Attachments/<message number>/<MIME part number>/`. |
| Thunderbird | Pass the mail folder or use `inspect-profile thunderbird`. This reads `Mail/`, `ImapMail/`, and `.sbd` folder trees, and identifies `.msf` files as auxiliary indexes. |
| Windows Mail and Windows Live Mail | Pass the mail store folder or use `inspect-profile windows-mail` to find EML files. |
| Outlook Express | Pass the mail store folder or use `inspect-profile outlook-express`. Including folder and index files records how messages relate to their stores in the JSON output. |

## Inputs and attachments

| Input | Bytes extracted |
| --- | --- |
| EML, MBOX, EMLX, Maildir, MH, MMDF, Babyl | MIME attachment bytes after transfer decoding. Partial EMLX also includes external attachments whose location can be resolved. |
| MSG | By-value attachment streams in CFB, including those in embedded messages. |
| PST and OST | MAPI data properties of locally readable by-value attachments. |
| Outlook Express DBX and MBX | MIME attachments read from the original message. |
| OLM and ZIP | OLM attachment files and MIME attachments from EML, EMLX, and MBOX members of ZIP archives. |
| Standalone TNEF | Data properties of by-value attachments. |

The CLI reports `unavailable` if it cannot read an attachment, and `failed` if the length or hash differs from the recorded value or the export cannot read it. Server-referenced attachments, decryption, OLE objects, reconstructing standalone MSG files from embedded Outlook items, and parsing EDB and NSF are outside this coverage. Use `mailviewer-engine.exe --help` for commands that save file ranges or MSG streams directly.

## Verification

The test extracted 96 attachments from 75 messages in public and synthetic samples. Results were checked against Python's MIME parser for EML, olefile for MSG, independent reference values for PST and OST, and the original ZIP members for OLM. The test also confirmed that `verify` detects changed or missing attachments. [Test fixtures](../tests/fixtures/README.md)
