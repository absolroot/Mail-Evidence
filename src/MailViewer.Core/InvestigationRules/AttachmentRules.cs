namespace MailViewer.Core;

internal static class AttachmentRules
{
    internal static void Evaluate(MailRecord record, InvestigationEvaluation result, CancellationToken ct)
    {
        foreach (var attachment in record.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            EvidenceLocation? data = result.CollectEvidence ? AttachmentLocation(attachment) : null;
            if (result.CollectEvidence && attachment.Sha256 is { } hash)
                result.Add(new("SHA-256", $"{attachment.Name} · {hash}", data, new(InvestigationKeyKind.AttachmentSha256, hash)));
            if (attachment.Analysis is not { } analysis)
            {
                if (attachment.Issue is not null)
                    result.Unavailable |= InvestigationClue.DisguisedAttachmentName | InvestigationClue.AttachmentPeMismatch;
                continue;
            }
            if (analysis.DoubleExtension || analysis.DirectionControl)
            {
                int finding = result.BeginFinding(InvestigationClue.DisguisedAttachmentName);
                if (result.CollectEvidence)
                {
                    if (analysis.DoubleExtension)
                        result.Add(new("Double extension", attachment.Name, analysis.NameLocation,
                            Clue: InvestigationClue.DisguisedAttachmentName, Finding: finding));
                    if (analysis.DirectionControl)
                        result.Add(new("Direction controls", EscapeDirectionControls(attachment.Name), analysis.NameLocation,
                            Clue: InvestigationClue.DisguisedAttachmentName, Finding: finding) { RawValue = attachment.Name });
                }
            }
            if (analysis.PeHeader is { } pe)
            {
                int finding = result.BeginFinding(InvestigationClue.AttachmentPeMismatch);
                if (result.CollectEvidence)
                {
                    result.Add(new(analysis.NameField, attachment.Name, analysis.NameLocation,
                        Clue: InvestigationClue.AttachmentPeMismatch, Finding: finding));
                    var location = data is { Stream: not null } or { Property: not null }
                        ? data with { Range = pe } : data;
                    string format = analysis.PeOptionalMagic == 0x20b ? "PE32+" : "PE32";
                    result.Add(new("Decoded PE header", $"{format} · 50 45 00 00 · 0x{pe.Offset:X}", location,
                        Clue: InvestigationClue.AttachmentPeMismatch, Finding: finding)
                        { DecodedAttachmentId = attachment.Id, DecodedPeHeader = pe });
                }
            }
            if (analysis.PeUnreadable) result.Unavailable |= InvestigationClue.AttachmentPeMismatch;
            if (analysis.BoundaryLimited) result.Unavailable |= InvestigationClue.AttachmentBoundaryMismatch;
            if (analysis.Boundary is { } boundary)
            {
                int finding = result.BeginFinding(InvestigationClue.AttachmentBoundaryMismatch);
                if (result.CollectEvidence)
                    result.Add(new("Attachment signature", attachment.Name, null,
                        Clue: InvestigationClue.AttachmentBoundaryMismatch, Finding: finding)
                        { DecodedAttachmentId = attachment.Id, Boundary = boundary });
            }
        }
    }

    private static EvidenceLocation? AttachmentLocation(MailAttachment attachment) =>
        attachment.EncodedRange is { } bytes ? new(bytes) :
        attachment.CompoundStream is { } stream ? new(new ByteRange(0, stream.Length), Stream: stream) :
        attachment.PstProperty is { } property ? new(new ByteRange(0, property.Length), Property: property) : null;

    private static string EscapeDirectionControls(string text) => string.Concat(text.Select(c =>
        c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' ? $@"\u{(int)c:X4}" : c.ToString()));
}
