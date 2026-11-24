using System.Collections.Concurrent;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MailViewer.Core;

public sealed class EvidenceSession : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> handles;
    private readonly Dictionary<string, SourceInfo> sourcesById;
    private readonly Dictionary<string, MailRecord> recordsById;
    private readonly ConcurrentDictionary<string, InvestigationSummary> investigationSummaries = new();
    private sealed record InvestigationDetail(string RecordId, InvestigationReport Report);
    private InvestigationDetail? lastInvestigation;
    private readonly ConversationIndex conversation;
    private sealed record ReplyComparison(string RecordId, ReplyHistory History);
    private ReplyComparison? lastReplyComparison;
    private bool disposed;

    private EvidenceSession(List<SourceInfo> sources, List<MailRecord> records, List<PstFolder> pstFolders, List<ReadIssue> issues,
        Dictionary<string, SafeFileHandle> handles, CancellationToken ct)
    {
        Sources = sources.AsReadOnly();
        Records = records.AsReadOnly();
        PstFolders = pstFolders.AsReadOnly();
        Issues = issues.AsReadOnly();
        this.handles = handles;
        sourcesById = sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        recordsById = records.ToDictionary(record => record.Id, StringComparer.Ordinal);
        conversation = new ConversationIndex(Records, ct);
    }

    private int anomalousMailCount = -1;
    public int? AnomalousMailCount => Volatile.Read(ref anomalousMailCount) is var count && count >= 0 ? count : null;
    public bool HasKnownAnomaly(string recordId) =>
        investigationSummaries.TryGetValue(recordId, out var summary) && summary.Findings != InvestigationClue.None;
    public const long MaximumFolderStoreLength = PstReader.MaximumFileLength;
    public IReadOnlyList<SourceInfo> Sources { get; }
    public IReadOnlyList<MailRecord> Records { get; }
    public IReadOnlyList<PstFolder> PstFolders { get; }
    public IReadOnlyList<ReadIssue> Issues { get; }

    public static EvidenceSession Open(IReadOnlyList<string> paths, CancellationToken ct = default) =>
        Open(paths, null, ct);

    public static EvidenceSession Open(IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, DiskImageOrigin>? imageOrigins, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sources = new List<SourceInfo>();
        var records = new List<MailRecord>();
        var pstFolders = new List<PstFolder>();
        var issues = new List<ReadIssue>();
        var handles = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
        var openedPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        try
        {
            foreach (var input in paths)
            {
                ct.ThrowIfCancellationRequested();
                var path = input;
                SafeFileHandle? handle = null;
                SourceInfo? source = null;
                try
                {
                    path = Path.GetFullPath(input);
                    if (!openedPaths.Add(path))
                        continue;

                    EnsureLocal(path);
                    handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
                    var actualPath = GetActualPath(path, handle);
                    var length = RandomAccess.GetLength(handle);
                    var format = FindFormat(actualPath, handle);
                    if (format is null)
                    {
                        issues.Add(new ReadIssue(PathIdentity(path), "unsupported-format",
                            $"{path}: 지원하는 EML, MBOX, MSG, PST 또는 OST 형식으로 확인되지 않았습니다."));
                        continue;
                    }

                    var storeLayout = format.Value.IsFolderStore()
                        ? PstReader.ValidateHeader(handle, length, format.Value, ct) : null;
                    var sha256 = HashFile(handle, length, ct);
                    DiskImageOrigin? imageOrigin = imageOrigins?.GetValueOrDefault(path);
                    if (imageOrigin is not null && !string.Equals(sha256, imageOrigin.ExtractedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The selected file does not match the SHA-256 recorded during image extraction.");
                    var sourceId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{actualPath}\n{sha256}")));
                    if (handles.ContainsKey(sourceId))
                        continue;
                    source = new SourceInfo(sourceId, actualPath, format.Value, length, sha256,
                        format switch
                        {
                            MailFormat.Mbox => MboxReader.BoundaryRule,
                            MailFormat.Msg => MsgReader.BoundaryRule,
                            MailFormat.Pst => PstReader.BoundaryRule,
                            MailFormat.Ost => PstReader.BoundaryRuleForOst(storeLayout!),
                            _ => "eml-entire-file-v1"
                        }, path) { StoreLayout = storeLayout, ImageOrigin = imageOrigin };

                    var sourceRecords = new List<MailRecord>();
                    var sourceFolders = new List<PstFolder>();
                    if (format == MailFormat.Eml)
                    {
                        ParseRecord(source, handle, new ByteRange(0, length), null, null, sourceRecords, issues, ct);
                    }
                    else if (format == MailFormat.Msg)
                    {
                        if (MsgReader.Parse(source, handle, issues, ct) is { } msg)
                            sourceRecords.Add(msg);
                    }
                    else if (format.Value.IsFolderStore())
                    {
                        PstReader.Parse(source, handle, sourceRecords, sourceFolders, issues, ct);
                    }
                    else
                    {
                        foreach (var entry in MboxReader.Scan(source, handle, issues, ct))
                            ParseRecord(source, handle, entry.Message, entry.Envelope, entry.Separator, sourceRecords, issues, ct);
                    }

                    if (RandomAccess.GetLength(handle) != length)
                        throw new IOException("분석 중 원본 파일의 길이가 달라졌습니다.");
                    sources.Add(source);
                    records.AddRange(sourceRecords);
                    pstFolders.AddRange(sourceFolders);
                    handles.Add(source.Id, handle);
                    handle = null;
                }
                catch (UnsupportedOstLayoutException e)
                {
                    issues.Add(new ReadIssue(PathIdentity(path), e.Code, $"{path}: {e.Message}")
                    {
                        SourcePath = path,
                        StoreLayout = e.Layout
                    });
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    issues.Add(new ReadIssue(source?.Id ?? PathIdentity(path), "source-read", $"{path}: {e.Message}")
                    {
                        SourcePath = path,
                        StoreLayout = source?.StoreLayout
                    });
                }
                finally
                {
                    handle?.Dispose();
                }
            }

            ct.ThrowIfCancellationRequested();
            return new EvidenceSession(sources, records, pstFolders, issues, handles, ct);
        }
        catch
        {
            foreach (var fileHandle in handles.Values)
                fileHandle.Dispose();
            throw;
        }
    }

    // The original sessions retain ownership if preparing the combined search fails or is canceled.
    public (EvidenceSession Session, SearchResult Result) Append(
        EvidenceSession additional, SearchRequest request, CancellationToken ct = default, bool countAnomalies = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(additional);
        ObjectDisposedException.ThrowIf(additional.disposed, additional);
        if (ReferenceEquals(this, additional))
            throw new ArgumentException("A session cannot be appended to itself.", nameof(additional));

        var addedIds = additional.sourcesById.Keys.Where(id => !sourcesById.ContainsKey(id))
            .ToHashSet(StringComparer.Ordinal);
        var mergedHandles = new Dictionary<string, SafeFileHandle>(handles, StringComparer.Ordinal);
        foreach (string id in addedIds)
            mergedHandles.Add(id, additional.handles[id]);
        var merged = new EvidenceSession(
            Sources.Concat(additional.Sources.Where(source => addedIds.Contains(source.Id))).ToList(),
            Records.Concat(additional.Records.Where(record => addedIds.Contains(record.SourceId))).ToList(),
            PstFolders.Concat(additional.PstFolders.Where(folder => addedIds.Contains(folder.SourceId))).ToList(),
            Issues.Concat(additional.Issues.Where(issue => !sourcesById.ContainsKey(issue.SourceId))).ToList(),
            mergedHandles, ct);
        foreach (var item in investigationSummaries)
            ReuseSummary(item);
        foreach (var item in additional.investigationSummaries)
            ReuseSummary(item);
        SearchResult result = merged.Search(request, ct);
        if (countAnomalies) merged.PrepareAnomalyCount(ct);
        ct.ThrowIfCancellationRequested();

        handles.Clear();
        foreach (string id in addedIds)
            additional.handles.Remove(id);
        Dispose();
        additional.Dispose();
        return (merged, result);

        void ReuseSummary(KeyValuePair<string, InvestigationSummary> item)
        {
            ct.ThrowIfCancellationRequested();
            if (merged.recordsById.TryGetValue(item.Key, out var record) && !merged.conversation.HasReferences(record))
                merged.investigationSummaries.TryAdd(item.Key, item.Value);
        }
    }

    public void PrepareAnomalyCount(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int count = 0;
        foreach (var record in Records)
        {
            ct.ThrowIfCancellationRequested();
            if (SummarizeInvestigation(record, ct).Findings != InvestigationClue.None) count++;
        }
        ct.ThrowIfCancellationRequested();
        Volatile.Write(ref anomalousMailCount, count);
    }

    public InvestigationReport Inspect(string recordId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref lastInvestigation) is { } cached && cached.RecordId == recordId)
            return cached.Report;

        var record = recordsById[recordId];
        var history = conversation.HasReferences(record) ? CompareReplyHistory(recordId, ct) : null;
        var report = InvestigationRules.Inspect(record, history, ct);
        ct.ThrowIfCancellationRequested();
        Volatile.Write(ref lastInvestigation, new(recordId, report));
        return report;
    }

    private InvestigationSummary SummarizeInvestigation(MailRecord record, CancellationToken ct)
    {
        if (investigationSummaries.TryGetValue(record.Id, out var cached)) return cached;

        var summary = InvestigationRules.Summarize(record, ct);
        if (conversation.HasReferences(record))
            summary = summary with { Findings = summary.Findings | ReplyHistoryRule.Flag(conversation.Compare(record, ct)) };
        ct.ThrowIfCancellationRequested();
        return investigationSummaries.GetOrAdd(record.Id, summary);
    }

    public ConversationHistory GetConversation(string recordId, CancellationToken ct = default) =>
        conversation.Open(recordsById[recordId], CompareReplyHistory(recordId, ct), ct);

    public int GetRelatedMessageCount(string recordId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return conversation.CountRelated(recordsById[recordId]);
    }

    public ReplyHistory CompareReplyHistory(string recordId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref lastReplyComparison) is { } cached && cached.RecordId == recordId)
            return cached.History;

        var history = conversation.Compare(recordsById[recordId], ct);
        ct.ThrowIfCancellationRequested();
        Volatile.Write(ref lastReplyComparison, new(recordId, history));
        return history;
    }

    public MailRecord GetRecord(string recordId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return recordsById[recordId];
    }

    public SearchResult Search(SearchRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var folderScope = ResolvePstFolderScope(request, ct);
        var groupedSources = request.SourceGroup?.SourceIds.ToHashSet(StringComparer.Ordinal);
        var bookmarks = request.BookmarkRecordIds?.ToHashSet(StringComparer.Ordinal);
        var bookmarkSources = bookmarks?.Where(recordsById.ContainsKey)
            .Select(id => recordsById[id].SourceId).ToHashSet(StringComparer.Ordinal);

        bool ContainsSource(string sourceId) =>
            (request.SourceId is null || string.Equals(request.SourceId, sourceId, StringComparison.Ordinal)) &&
            (groupedSources is null || groupedSources.Contains(sourceId));
        var query = Normalize(request.Query.Trim());
        var from = Normalize(request.From?.Trim() ?? "");
        var to = Normalize(request.To?.Trim() ?? "");
        var subject = Normalize(request.Subject?.Trim() ?? "");
        var dateFiltered = request.FromDateInclusive.HasValue || request.ToDateExclusive.HasValue;
        var hits = new List<SearchHit>();
        var examined = 0;
        var dateUnknown = 0;
        var investigationUnavailable = 0;
        var recordKey = request.Exact is { Kind: InvestigationKeyKind.RecordId } ? request.Exact : null;
        var observationKey = recordKey is null ? request.Exact : null;
        bool filterFindings = request.AnomaliesOnly || request.Clues != InvestigationClue.None;
        bool inspect = observationKey is not null || filterFindings;
        var partialRecords = inspect ? Issues.Where(issue => issue.RecordId is not null)
            .ToLookup(issue => issue.RecordId!) : null;

        foreach (var record in Records)
        {
            ct.ThrowIfCancellationRequested();
            if (!ContainsSource(record.SourceId) || bookmarks is not null && !bookmarks.Contains(record.Id))
                continue;
            if (folderScope is not null && (record.PstMessage is not { } pst || !folderScope.Contains(pst.FolderNodeId)))
                continue;
            examined++;
            if (recordKey is not null && record.Id != recordKey.Value) continue;
            if (request.HasAttachment.HasValue && request.HasAttachment.Value != (record.Attachments.Count > 0))
                continue;
            if (!Contains(record.From, from) || !Contains(record.To, to) || !Contains(record.Subject, subject))
                continue;

            var matches = query.Length == 0 ? Array.Empty<SearchMatch>() : FindMatches(record, request.Field, query, ct);
            if (query.Length > 0 && matches.Length == 0)
                continue;
            if (dateFiltered)
            {
                if (record.Date is not { } date)
                {
                    dateUnknown++;
                    continue;
                }
                if (date < request.FromDateInclusive || date >= request.ToDateExclusive)
                    continue;
            }

            if (inspect)
            {
                var summary = filterFindings ? SummarizeInvestigation(record, ct) : default;
                if ((request.AnomaliesOnly && summary.Unavailable != InvestigationClue.None) ||
                    (summary.Unavailable & request.Clues) != 0 || partialRecords!.Contains(record.Id))
                    investigationUnavailable++;
                if (request.AnomaliesOnly && summary.Findings == InvestigationClue.None)
                    continue;
                if (request.Clues != InvestigationClue.None && (summary.Findings & request.Clues) == 0)
                    continue;
                if (observationKey is { } key && !InvestigationObservations.Matches(record, key, ct))
                    continue;
            }
            hits.Add(new SearchHit(record, Array.AsReadOnly(matches)));
        }

        var issues = Issues.Where(issue =>
            ContainsSource(issue.SourceId)
            && (bookmarkSources is null || bookmarkSources.Contains(issue.SourceId) &&
                (issue.RecordId is null || bookmarks!.Contains(issue.RecordId)))
            && (folderScope is null || issue.PstFolderNodeId is not { } folderId || folderScope.Contains(folderId))
            && (inspect || AffectsSearch(issue, request, query.Length > 0))).ToArray();
        ct.ThrowIfCancellationRequested();
        return new SearchResult(hits.AsReadOnly(), Array.AsReadOnly(issues), dateUnknown, examined)
            { InvestigationUnavailableCount = investigationUnavailable };
    }

    public byte[] ReadRange(string sourceId, ByteRange range, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        var source = sourcesById[sourceId];
        ArgumentOutOfRangeException.ThrowIfNegative(range.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(range.Length);
        if (range.Offset > source.Length || range.Length > source.Length - range.Offset)
            throw new ArgumentOutOfRangeException(nameof(range), "요청한 바이트 범위가 원본 파일을 벗어납니다.");
        return MimeMessageReader.ReadBytes(handles[source.Id], range, ct);
    }

    public string? ReadHtmlBody(string recordId, string partPath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        var record = recordsById[recordId];
        var source = record.BodyParts.FirstOrDefault(part => part.PartPath == partPath)?.HtmlSource;
        if (source is null) return null;
        if (source.Location.Range.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("HTML 본문이 미리보기 한도를 초과했습니다.");

        if (source.TransferEncoding is not null)
            return MimeMessageReader.ReadHtmlBody(handles[record.SourceId], record.Format, source, ct);

        Encoding encoding = Encoding.GetEncoding(source.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        if (source.Location.Stream is { } stream)
            return ReadStoreHtml(ReadCompoundRange(record.SourceId, stream, source.Location.Range, ct), stream.PropertyTag, encoding);
        if (source.Location.Property is { } property)
            return ReadStoreHtml(ReadPstPropertyRange(record.SourceId, property, source.Location.Range, ct), property.PropertyTag, encoding);
        return null;
    }

    private static string ReadStoreHtml(byte[] bytes, uint tag, Encoding encoding) =>
        (tag & 0xffff) == 0x0102 ? MsgReader.ReadHtmlText(bytes, encoding) : encoding.GetString(bytes).TrimEnd('\0');

    public byte[]? ReadInlineBodyImage(string recordId, string contentId, int maximumBytes, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        var candidates = recordsById[recordId].Attachments.Where(attachment =>
            string.Equals(attachment.ContentId, contentId, StringComparison.Ordinal)).Take(2).ToArray();
        if (candidates is not [{ DecodedLength: > 0, Issue: null, Sha256: not null } image] ||
            image.DecodedLength > maximumBytes || !image.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return null;
        return ReadAttachment(recordId, image.Id, ct);
    }

    public byte[] ReadAttachment(string recordId, string attachmentId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        var record = recordsById[recordId];
        var attachment = record.Attachments.FirstOrDefault(item => item.Id == attachmentId)
            ?? throw new KeyNotFoundException("선택한 첨부파일을 찾지 못했습니다.");
        if (attachment.Issue is not null || attachment.DecodedLength is null || attachment.Sha256 is null)
            throw new InvalidDataException(attachment.Issue ?? "첨부파일의 원본 위치를 확인할 수 없습니다.");
        byte[] bytes;
        if (record.Format == MailFormat.Msg && attachment.CompoundStream is { } locator)
            bytes = ReadCompoundRange(record.SourceId, locator, new ByteRange(0, locator.Length), ct);
        else if (record.Format.IsFolderStore() && attachment.PstProperty is { } pstProperty)
            bytes = ReadPstPropertyRange(record.SourceId, pstProperty, new ByteRange(0, pstProperty.Length), ct);
        else if (attachment.EncodedRange is { } range)
            bytes = MimeMessageReader.DecodeAttachment(handles[record.SourceId], record.Format, range, attachment.TransferEncoding, ct);
        else
            throw new InvalidDataException("첨부파일의 원본 위치를 확인할 수 없습니다.");
        if (bytes.LongLength != attachment.DecodedLength
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), attachment.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("첨부파일을 다시 읽은 결과가 분석 때 기록한 길이 또는 해시와 다릅니다.");
        return bytes;
    }

    public byte[] ReadCompoundRange(string sourceId, CompoundStreamLocator locator, ByteRange range,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        var source = sourcesById[sourceId];
        if (source.Format != MailFormat.Msg)
            throw new ArgumentException("MSG에서만 CFB 스트림을 열 수 있습니다.", nameof(sourceId));
        return MsgReader.ReadRange(source, handles[source.Id], locator, range, ct);
    }

    public byte[] ReadPstPropertyRange(string sourceId, PstPropertyLocator locator, ByteRange range,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        var source = sourcesById[sourceId];
        if (!source.Format.IsFolderStore())
            throw new ArgumentException("PST·OST 원본에서만 MAPI 속성을 열 수 있습니다.", nameof(sourceId));
        var recordId = $"{source.Id}:{locator.Message.MessageNodeId:X8}";
        if (!recordsById.TryGetValue(recordId, out var record) || record.PstMessage != locator.Message ||
            (record.TransportHeaderProperty != locator && !record.Attachments.Any(item => item.PstProperty == locator)
             && !record.StoredFields.Any(item => item.Property == locator)
             && !record.Attachments.Any(item => item.Analysis?.NameLocation?.Property == locator)
             && !record.BodyParts.Any(part => part.HtmlSource?.Location.Property == locator
                 || part.Links.Any(link => link.Location.Property == locator))))
            throw new ArgumentException("확인한 메시지에 이 MAPI 속성 위치가 없습니다.", nameof(locator));
        return PstReader.ReadRange(source, handles[source.Id], locator, range, ct);
    }

    private HashSet<uint>? ResolvePstFolderScope(SearchRequest request, CancellationToken ct)
    {
        if (request.PstFolderNodeId is not { } id)
            return null;
        if (request.SourceId is null ||
            !PstFolders.Any(folder => folder.SourceId == request.SourceId && folder.NodeId == id))
            throw new ArgumentException("PST·OST 폴더 검색에는 해당 원본과 폴더 NID가 필요합니다.", nameof(request));
        var found = new HashSet<uint> { id };
        if (!request.IncludePstSubfolders)
            return found;
        var children = PstFolders.Where(folder => folder.SourceId == request.SourceId && folder.ParentNodeId.HasValue)
            .ToLookup(folder => folder.ParentNodeId!.Value, folder => folder.NodeId);
        var requested = new Queue<uint>();
        requested.Enqueue(id);
        while (requested.TryDequeue(out var current))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var nested in children[current])
                if (found.Add(nested))
                    requested.Enqueue(nested);
        }
        return found;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        foreach (var handle in handles.Values)
            handle.Dispose();
    }

    private static void ParseRecord(SourceInfo source, SafeFileHandle handle, ByteRange range, ByteRange? envelope,
        ByteRange? separator, List<MailRecord> records, List<ReadIssue> issues, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (MimeMessageReader.Parse(source, handle, range, envelope, separator, issues, ct) is { } record)
                records.Add(record);
        }
        catch (Exception e) when (e is FormatException or NotSupportedException or ArgumentException)
        {
            issues.Add(new ReadIssue(source.Id, "message-parse", $"메시지를 판독할 수 없습니다. {e.Message}", range));
        }
    }

    private static MailFormat? FindFormat(string path, SafeFileHandle handle)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".eml", StringComparison.OrdinalIgnoreCase))
            return MailFormat.Eml;
        if (extension.Equals(".msg", StringComparison.OrdinalIgnoreCase) && MsgReader.HasSignature(handle))
            return MailFormat.Msg;
        if (extension.Equals(".pst", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ost", StringComparison.OrdinalIgnoreCase))
            return PstReader.FindFormat(handle);
        if (extension.Equals(".mbox", StringComparison.OrdinalIgnoreCase)
            || (extension.Length == 0 && MboxReader.HasEnvelopePrefix(handle)))
            return MailFormat.Mbox;
        return null;
    }

    private static string HashFile(SafeFileHandle handle, long length, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                ct.ThrowIfCancellationRequested();
                var count = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                if (count == 0)
                    throw new EndOfStreamException("해시를 계산하는 동안 원본 파일의 길이가 달라졌습니다.");
                hash.AppendData(buffer, 0, count);
                offset += count;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureLocal(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = Path.GetPathRoot(path);
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(root).DriveType == DriveType.Network)
            throw new NotSupportedException("로컬 드라이브에 복사한 자료를 선택해 주세요.");
        if (path.AsSpan(root.Length).Contains(':'))
            throw new NotSupportedException("파일의 대체 데이터 스트림은 지원하지 않습니다.");

        for (string? entry = path; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException("재분석 지점이 포함되지 않은 로컬 경로에서 자료를 선택해 주세요.");
        }
    }

    private static string GetActualPath(string selectedPath, SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return selectedPath;

        var path = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length >= path.Capacity)
        {
            path.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        }
        if (length == 0)
            throw new IOException("열어 둔 원본의 실제 경로를 확인하지 못했습니다.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (length >= path.Capacity)
            throw new IOException("열어 둔 원본의 실제 경로가 판독 중 달라졌습니다.");

        var actualPath = path.ToString();
        if (actualPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("네트워크 경로에 있는 원본은 로컬 드라이브에 복사해 주세요.");
        if (actualPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            actualPath = actualPath[4..];
        if (!Path.IsPathFullyQualified(actualPath))
            throw new IOException("원본의 로컬 파일 경로를 확인할 수 없습니다.");
        return actualPath;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, StringBuilder filePath, uint length, uint flags);

    private static string PathIdentity(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));

    private static string Normalize(string value) =>
        value.IsNormalized() ? value : value.Normalize();

    private static bool Contains(string field, string term) =>
        term.Length == 0 || Normalize(field).Contains(term, StringComparison.OrdinalIgnoreCase);

    private static bool AffectsSearch(ReadIssue issue, SearchRequest request, bool hasQuery)
    {
        if (issue.Code == "attachment-data")
            return false;
        if (issue.Field is null)
            return true;
        return issue.Field switch
        {
            SearchField.Body => (hasQuery && request.Field is SearchField.Any or SearchField.Body)
                || (issue.Code.StartsWith("mime-", StringComparison.Ordinal)
                    && (request.HasAttachment.HasValue || (hasQuery && request.Field == SearchField.AttachmentName))),
            SearchField.AttachmentName => request.HasAttachment.HasValue || (hasQuery && request.Field == SearchField.AttachmentName),
            SearchField.Subject => !string.IsNullOrWhiteSpace(request.Subject)
                || (hasQuery && request.Field is SearchField.Any or SearchField.Subject),
            SearchField.From => !string.IsNullOrWhiteSpace(request.From)
                || (hasQuery && request.Field is SearchField.Any or SearchField.From),
            SearchField.To => !string.IsNullOrWhiteSpace(request.To)
                || (hasQuery && request.Field is SearchField.Any or SearchField.To),
            _ => hasQuery && request.Field == issue.Field,
        };
    }

    private static SearchMatch[] FindMatches(MailRecord record, SearchField field, string query, CancellationToken ct)
    {
        var matches = new List<SearchMatch>();
        Match(SearchField.Subject, record.Subject, inDefault: true);
        Match(SearchField.From, record.From, inDefault: true);
        Match(SearchField.To, record.To, inDefault: true);
        Match(SearchField.Cc, record.Cc);
        Match(SearchField.Bcc, record.Bcc);
        Match(SearchField.MessageId, record.MessageId);
        Match(SearchField.SourcePath, record.SourcePath);
        if (field is SearchField.Any or SearchField.Body)
        {
            foreach (var part in record.BodyParts)
            {
                ct.ThrowIfCancellationRequested();
                if (Contains(part.Text, query))
                    matches.Add(new SearchMatch(SearchField.Body, part.PartPath));
            }
        }
        if (field == SearchField.AttachmentName)
        {
            foreach (var item in record.Attachments)
            {
                ct.ThrowIfCancellationRequested();
                if (Contains(item.Name, query))
                    matches.Add(new SearchMatch(SearchField.AttachmentName, item.PartPath));
            }
        }
        return matches.ToArray();

        void Match(SearchField target, string value, bool inDefault = false)
        {
            if ((field == target || (field == SearchField.Any && inDefault)) && Contains(value, query))
                matches.Add(new SearchMatch(target, null));
        }
    }
}
