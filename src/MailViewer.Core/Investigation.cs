namespace MailViewer.Core;

[Flags]
public enum InvestigationClue
{
    None = 0, DuplicateHeader = 1, ReplyDomainMismatch = 2, ReceivedTimeReversal = 4,
    LinkDomainMismatch = 8, DisguisedAttachmentName = 16, AttachmentPeMismatch = 32, ReplyAddressChanged = 64,
    AttachmentBoundaryMismatch = 128
}

public enum InvestigationKeyKind { Address, Host, Url, AttachmentSha256, MessageId, RecordId }

public sealed record InvestigationKey(InvestigationKeyKind Kind, string Value);
public sealed record InvestigationObservation(string Field, string Value, EvidenceLocation? Location,
    InvestigationKey? Key = null, InvestigationClue Clue = InvestigationClue.None, int Finding = 0)
{
    public string? SourceNote { get; init; }
    public string? EvidenceRecordId { get; init; }
    public string? RawValue { get; init; }
    public string? DecodedAttachmentId { get; init; }
    public ByteRange? DecodedPeHeader { get; init; }
    public AttachmentBoundary? Boundary { get; init; }
}
public sealed record InvestigationReport(IReadOnlyList<InvestigationObservation> Observations,
    InvestigationClue Unavailable);
