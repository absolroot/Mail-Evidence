# Reply-To change samples

Open all three EML files together. **All mail** shows three messages and **Anomalies** shows one.

| File | What to check |
| --- | --- |
| [01-original.eml](01-original.eml) | The related messages list shows the two messages that reference this one. |
| [02-change.eml](02-change.eml) | The text after `+` in the Reply-To address differs between two messages from the same sender. In **Anomalies**, compare the two addresses and open their headers. |
| [03-follow-up.eml](03-follow-up.eml) | The Reply-To domains differ only in letter casing. The comparison treats them as the same address. |

Exporting the anomaly results to CSV creates `reply-address-changes.json` with both Reply-To addresses, reference headers, and source locations.
