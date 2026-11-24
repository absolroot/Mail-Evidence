# Attachment signatures and boundaries

When you open a message, the Windows app compares the format implied by the attachment filename with the decoded bytes. **Anomalies** shows mismatched start signatures, invalid internal structure, missing end markers, and extra bytes after the end. To find messages with these results, select **Attachment signature or boundary difference** in the search filters.

## Bytes checked

| File format | Start signature | End structure or boundary |
| --- | --- | --- |
| PNG | `89 50 4E 47 0D 0A 1A 0A` | Reads chunks in sequence and checks for a zero-length `IEND` with its CRC. The complete end chunk is `00 00 00 00 49 45 4E 44 AE 42 60 82`. |
| JPEG, JFIF | `FF D8 FF` | Follows markers and segment lengths to find `FF D9`, skipping the same bytes inside a segment. If MPF is present, the check allows data after the first image. |
| GIF | `GIF87a`, `GIF89a` | Follows blocks and sub-blocks to find the `3B` trailer. |
| PDF | `%PDF-n.n` within the first 1,024 bytes | Looks for `startxref`, a number, and `%%EOF` in that order within the last 128 KiB. PDF whitespace may follow. |
| ZIP, DOCX, XLSX, PPTX, DOCM, XLSM, PPTM, HWPX, ODT, ODS, ODP, EPUB | `50 4B 03 04`, or `50 4B 05 06` for an empty ZIP | Finds the end-of-central-directory record `50 4B 05 06` and accounts for its declared comment length. |
| WebP, WAVE | `RIFF` at offset 0 and `WEBP` or `WAVE` at offset 8 | Uses the four-byte length at offset 4 to check the container boundary. For `RF64`, only the start structure is checked. |
| HWP | `D0 CF 11 E0 A1 B1 1A E1` or legacy `HWP Document File V` | Start signature only. |
| TIFF, BigTIFF | `49 49 2A 00`, `4D 4D 00 2A`, `49 49 2B 00`, `4D 4D 00 2B` | Start signature only. |
| BMP, 7z, GZIP, RAR | BMP `42 4D`. 7z `37 7A BC AF 27 1C`. GZIP `1F 8B 08`. RAR `52 61 72 21 1A 07` followed by `00` or `01 00`. | Start signature only. |

The formats come from the [SEARCH signature list](https://filesig.search.org/). Boundary checks draw on the [PNG](https://www.w3.org/TR/png/), [JPEG](https://www.itu.int/itu-t/recommendations/rec.aspx?id=2633&lang=en), [GIF](https://www.w3.org/Graphics/GIF/spec-gif89a.txt), [PDF](https://pdf-issues.pdfa.org/32000-2-2020/clause07.html), [ZIP](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT), and [WebP](https://developers.google.com/speed/webp/docs/riff_container) specifications.

DOCX and HWPX use the common ZIP structure check. The app checks the extensions in the table. It reports the check as unavailable when the attachment exceeds 8 MiB or its bytes cannot be read.

## Inspect the evidence in hex

Select a finding in **Anomalies** to see the bytes and offset that triggered it. Choose **Open full attachment hex** to jump to the start of the affected range. The view shows the actual offset and outlines the affected hex and ASCII bytes in red. If an end marker is missing, it shows the end-of-file offset and the expected marker.

The offset is relative to the decoded attachment. Expand **Source location** to see the encoded MIME range, MSG stream, or PST or OST property, along with the attachment SHA-256. The range annotation and jump controls draw on [ImHex's byte-range bookmarks](https://docs.werwolv.net/imhex/views/bookmarks).

Use the [three sample messages](../tests/fixtures/attachment-boundaries/README.md) to compare a mismatched signature, an invalid length field, a missing end marker, trailing bytes, and valid variants.
