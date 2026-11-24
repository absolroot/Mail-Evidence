# Investigation rules

| Check | Implementation |
| --- | --- |
| Repeated single-instance headers, From and Reply-To domain differences, and reversed relay timestamps | [HeaderRules.cs](../src/MailViewer.Core/InvestigationRules/HeaderRules.cs) |
| Domain differences between displayed HTML URLs and their destinations | [LinkRules.cs](../src/MailViewer.Core/InvestigationRules/LinkRules.cs) |
| Double extensions, direction control characters, PE content under a document or image name, and attachment structure differences | [AttachmentRules.cs](../src/MailViewer.Core/InvestigationRules/AttachmentRules.cs) · [File signatures](attachment-signatures.md) |
| Changes to Reply-To within a conversation | [ReplyHistoryRule.cs](../src/MailViewer.Core/InvestigationRules/ReplyHistoryRule.cs) |
| Authentication results recorded in headers and lookups by address, URL, hash, or ID | [InvestigationObservations.cs](../src/MailViewer.Core/InvestigationRules/InvestigationObservations.cs) |

## Add a rule

Add a new bit to `InvestigationClue` and preserve the existing values. Record each hit with `BeginFinding` and each range that could not be read with `Unavailable`. Use the same number for evidence that belongs to one finding. Specify whether offsets refer to the decoded attachment or the original container.

Add the rule to the [core evaluation order](../src/MailViewer.Core/InvestigationRules/InvestigationRules.cs) and the [app's filters and details](../src/MailViewer.App/MainWindow.Investigation.cs). Verify filter matches, evidence locations, unavailable checks, and export coverage in the [core tests](../tests/MailViewer.Verification/InvestigationChecks.cs). The [EML samples](../tests/fixtures/anomaly-ux-complete/README.md) include ordinary messages and messages with data the app cannot fully check.
