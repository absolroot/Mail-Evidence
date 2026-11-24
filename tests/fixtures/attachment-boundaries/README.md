# Attachment boundary samples

Open the three synthetic EML files together. **All mail** shows three messages and **Anomalies** shows two.

| Message | What to check |
| --- | --- |
| [01-normal.eml](01-normal.eml) | Valid PNG, JPEG, GIF, PDF, ZIP, and BIN examples. The `FF D9` bytes within a JPEG segment, the ZIP comment, and the whitespace at the end of the PDF are valid. |
| [02-mixed.eml](02-mixed.eml) | GIF signature at `0x0`, invalid PNG chunk length at `0x21`, missing PNG `IEND` at the end of the file (`0x46`), and 4,620 extra bytes starting at `0x2B1` after the JPEG end marker. |
| [03-container-boundaries.eml](03-container-boundaries.eml) | Extra bytes after the PDF and ZIP endings. Also includes a valid ZIP with a comment. |

Select a finding in **Anomalies** and choose **Open full attachment hex** to jump to the corresponding offset in the decoded attachment.
