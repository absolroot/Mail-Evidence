# Anomaly samples

Open all 10 EML files together. **All mail** shows 10 messages and **Anomalies** shows eight. Select a finding to see its evidence, then open the source bytes to check the underlying data.

| File | What to try |
| --- | --- |
| `01-duplicate-header.eml` | Two Date header lines produce one finding. |
| `02-reply-domain.eml` | The From and Reply-To addresses use different domains. Search from the Reply-To address context menu or search by Message-ID from **Edit** to find message 08 as well. |
| `03-relay-clock.eml` | Compare two relay timestamps that are out of order by two minutes. |
| `04-link-domain.eml` | Compare the displayed link with its destination. Check that a long URL can be read and copied in a narrow window. |
| `05-double-extension.eml` | Inspect a filename with an executable extension after a document extension. The attachment contains plain text. |
| `06-direction-control.eml` | Inspect the direction control character and the filename as stored in MIME. |
| `07-pe-header.eml` | Compare a PDF filename with a PE header. The synthetic attachment contains only the header bytes. |
| `08-ordinary.eml` | Check the zero finding count, the recorded authentication results in **Message information**, and two link addresses on the same registrable domain. |
| `09-unavailable.eml` | Check the zero finding count and the notice that some checks could not be completed. The attachment is two bytes long. |
| `10-same-attachment.eml` | The attachment has the same SHA-256 as the one in message 07 under a different filename. Select the hash in **Edit → Search by message address, URL, hash or ID…** to find both messages. |

Filter by **Attachment filename disguise** to show messages 05 and 06. Exporting the search results saves those two messages. The eight shown in the mailbox tab count all messages with findings across the open sources. To see several findings in one message, open the [attachment boundary samples](../attachment-boundaries/README.md).

The addresses, bodies, and attachments are synthetic test data.
