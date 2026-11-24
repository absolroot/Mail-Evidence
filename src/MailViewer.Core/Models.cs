namespace MailViewer.Core;

public enum MailFormat
{
    Eml, Mbox, Msg, Pst, Ost, Emlx, Mmdf, Babyl, Dbx, Mbx, Olm, Zip, Tnef, Cms, MailboxIndex, Ics, Vcf, Edb, Nsf
}

public static class MailFormatExtensions
{
    public static bool IsFolderStore(this MailFormat format) => format is MailFormat.Pst or MailFormat.Ost;
}

public enum HeaderEvidenceState { Present, Missing, Empty, Unreadable }

public readonly record struct ByteRange(long Offset, long Length)
{
    public long End => checked(Offset + Length);
    public override string ToString() => $"0x{Offset:X} · {Length:N0} bytes";
}

public sealed record CompoundStreamLocator(
    IReadOnlyList<string> StoragePath, string StreamName, uint PropertyTag, long Length)
{
    public string DisplayPath => "/" + string.Join("/", StoragePath.Append(StreamName));
    public string TagHex => $"0x{PropertyTag:X8}";
}

public sealed record PstFolder(string SourceId, uint NodeId, uint? ParentNodeId, string Name, string Path)
{
    public bool IsLogicalPath { get; init; }
}

public sealed record PstMessageLocator(uint FolderNodeId, uint MessageNodeId, string FolderPath)
{
    public string FolderNid => $"0x{FolderNodeId:X8}";
    public string MessageNid => $"0x{MessageNodeId:X8}";
}

public sealed record PstPropertyLocator(
    PstMessageLocator Message, uint PropertyTag, long Length,
    uint? AttachmentNodeId = null, int? AttachmentIndex = null)
{
    public string TagHex => $"0x{PropertyTag:X8}";
    public string? AttachmentNid => AttachmentNodeId is { } nid ? $"0x{nid:X8}" : null;
}

public sealed record StoreLayoutInfo(ushort Version, int PageSize, byte? BlockEncoding);

public sealed record DiskImageOrigin(
    string ImagePath, long VolumeOffset, string FilePath,
    [property: System.Text.Json.Serialization.JsonNumberHandling(
        System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
    ulong FileReference, string ManifestPath, string ExtractedSha256);

public sealed record SourceInfo(
    string Id, string Path, MailFormat Format, long Length, string Sha256, string BoundaryRule,
    string? SelectedPath = null)
{
    public StoreLayoutInfo? StoreLayout { get; init; }
    public string? DetectedFormat { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MailIndexSummary? IndexSummary { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DiskImageOrigin? ImageOrigin { get; init; }
}

public sealed record MailIndexSummary(int FolderCount, int EntryCount, int SequenceCount, IReadOnlyList<string> Names);

public sealed record ReadIssue(
    string SourceId, string Code, string Detail, ByteRange? Range = null,
    string? RecordId = null, SearchField? Field = null, uint? PstFolderNodeId = null)
{
    public StoreLayoutInfo? StoreLayout { get; init; }
    public string? SourcePath { get; init; }
}

public sealed record MailHeader(string Name, string Value, ByteRange Range);

public sealed record MailBodyPart(string PartPath, string MediaType, string Text)
{
    public IReadOnlyList<ObservedLink> Links { get; init; } = [];
    internal HtmlBodySource? HtmlSource { get; init; }
    internal string? EngineHtml { get; init; }
}

internal sealed record HtmlBodySource(EvidenceLocation Location, string? TransferEncoding = null, string? Charset = null, int CodePage = 1252);

public sealed record MailAttachment(
    string Id, string PartPath, string Name, string MediaType, string TransferEncoding,
    ByteRange? EncodedRange, long? DecodedLength, string? Sha256, string? Issue = null,
    CompoundStreamLocator? CompoundStream = null, IReadOnlyList<string>? CompoundStoragePath = null,
    PstPropertyLocator? PstProperty = null)
{
    public AttachmentObservation? Analysis { get; init; }
    internal string? ContentId { get; init; }
    public ContainerEvidence? ContainerLocation { get; init; }
    internal EngineAttachment? EngineAttachment { get; init; }
}

public sealed record MailRecord(
    string Id, string SourceId, string SourcePath, MailFormat Format,
    ByteRange MessageRange, ByteRange HeaderRange, ByteRange? EnvelopeRange, ByteRange? SeparatorRange,
    string Subject, string From, string To, string Cc, string Bcc, string MessageId,
    string DateRaw, DateTimeOffset? Date,
    IReadOnlyList<MailHeader> Headers, IReadOnlyList<MailBodyPart> BodyParts,
    IReadOnlyList<MailAttachment> Attachments,
    CompoundStreamLocator? TransportHeaderStream = null,
    HeaderEvidenceState? TransportHeaderState = null, string? TransportHeaderDetail = null,
    PstMessageLocator? PstMessage = null, PstPropertyLocator? TransportHeaderProperty = null)
{
    public PstFolder? LogicalFolder { get; init; }
    public uint? FolderNodeId => PstMessage?.FolderNodeId ?? LogicalFolder?.NodeId;
    public string? FolderPath => PstMessage?.FolderPath ?? LogicalFolder?.Path;
    public ContainerEvidence? ContainerMessage { get; init; }
    public ContainerEvidence? ContainerHeaders { get; init; }
    public bool CanReadMessage => MessageRange.Length > 0 || ContainerMessage is { Length: > 0, CanRead: true };
    public int? TransportHeaderCodePage { get; init; }
    public IReadOnlyList<StoredMailField> StoredFields { get; init; } = [];
}

public sealed record StoredMailField(string Name, string Value, CompoundStreamLocator? Stream = null,
    PstPropertyLocator? Property = null);

public enum SearchField { Any, Subject, From, To, Cc, Bcc, Body, AttachmentName, MessageId, SourcePath }

public sealed record MailSourceGroup(string? DirectoryPath, IReadOnlyList<string> SourceIds);

public sealed record SearchRequest(
    string Query = "", SearchField Field = SearchField.Any,
    string? SourceId = null, DateTimeOffset? FromDateInclusive = null,
    DateTimeOffset? ToDateExclusive = null, bool? HasAttachment = null,
    string? From = null, string? To = null, string? Subject = null,
    uint? PstFolderNodeId = null, bool IncludePstSubfolders = false)
{
    public InvestigationClue Clues { get; init; }
    public bool AnomaliesOnly { get; init; }
    public InvestigationKey? Exact { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MailSourceGroup? SourceGroup { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? BookmarkRecordIds { get; init; }
}

public sealed record SearchMatch(SearchField Field, string? PartPath);
public sealed record SearchHit(MailRecord Record, IReadOnlyList<SearchMatch> Matches);
public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits, IReadOnlyList<ReadIssue> Issues, int DateUnknownCount, int ExaminedCount)
{
    public int InvestigationUnavailableCount { get; init; }
}

public enum ExportKind { MessageBytes, Attachments, Csv, PstEvidence, OstEvidence }
public enum ExportScope { SelectedMessages, SelectedAttachments, SearchResults }

public sealed record AttachmentSelection(string RecordId, string AttachmentId);

public sealed record ExportRequest(
    string DestinationDirectory, ExportKind Kind, ExportScope Scope,
    IReadOnlyList<string> RecordIds, IReadOnlyList<AttachmentSelection>? AttachmentIds = null,
    SearchRequest? Search = null, IReadOnlyList<ReadIssue>? SearchIssues = null,
    int? ExaminedCount = null, int? DateUnknownCount = null, string? DateTimeZone = null);

public sealed record ExportItem(
    string RelativePath, string Nature, long Length, string Sha256, string? RecordId,
    string? AttachmentId = null, string? PartPath = null, ByteRange? InputRange = null,
    string? OriginalName = null, string? TransferEncoding = null,
    CompoundStreamLocator? CompoundStream = null, PstPropertyLocator? PstProperty = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ContainerEvidence? ContainerLocation { get; init; }
}

public sealed record ExportResult(string PackageDirectory, Guid JobId, IReadOnlyList<ExportItem> Items);

public sealed record VerificationResult(
    string PackageDirectory, bool Success, Guid? JobId, IReadOnlyList<ExportItem> Items,
    IReadOnlyList<string> Errors);
