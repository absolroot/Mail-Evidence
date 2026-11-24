using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailViewer.Core;

public static class ExportService
{
    private const string ManifestName = "manifest.json";
    private const string Schema = "mail-viewer/export/1";
    private const int MaxManifestBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ExportResult Export(EvidenceSession session, ExportRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var recordById = session.Records.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var sourceById = session.Sources.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var records = request.RecordIds.Distinct(StringComparer.Ordinal).Select(RequireRecord).ToArray();
        var attachments = request.AttachmentIds?.Distinct().ToArray() ?? [];
        SearchResult? search = null;
        if (request.Scope == ExportScope.SearchResults)
        {
            if (request.Search is null || request.Kind == ExportKind.Attachments)
                throw new InvalidOperationException("확정한 검색 조건과 출력 형식을 확인해 주세요.");
            search = session.Search(request.Search, ct);
            if (!search.Hits.Select(x => x.Record.Id).ToHashSet(StringComparer.Ordinal).SetEquals(request.RecordIds))
                throw new InvalidOperationException("검색 결과가 달라졌습니다. 내보낼 범위를 다시 확인해 주세요.");
        }
        if (request.Kind == ExportKind.Attachments)
        {
            if (request.Scope != ExportScope.SelectedAttachments || attachments.Length == 0)
                throw new InvalidOperationException("저장할 첨부파일을 선택해 주세요.");
            records = attachments.Select(x => RequireRecord(x.RecordId)).DistinctBy(x => x.Id).ToArray();
        }
        else if (request.Scope == ExportScope.SelectedAttachments || records.Length == 0)
        {
            throw new InvalidOperationException("내보낼 메일이 없습니다.");
        }
        if (request.Kind == ExportKind.MessageBytes && records.Any(record => record.Format.IsFolderStore()))
            throw new InvalidOperationException("PST·OST 메시지는 근거 묶음 또는 CSV로 저장해 주세요.");
        if (request.Kind is ExportKind.PstEvidence or ExportKind.OstEvidence)
        {
            var format = request.Kind == ExportKind.PstEvidence ? MailFormat.Pst : MailFormat.Ost;
            if (records.Any(record => record.Format != format || record.PstMessage is null))
                throw new InvalidOperationException($"{format.ToString().ToUpperInvariant()} 근거 묶음에는 같은 형식의 메시지만 저장할 수 있습니다.");
        }

        using var destination = ExportDirectoryLease.Open(request.DestinationDirectory);
        var jobId = Guid.NewGuid();
        var folder = Path.Combine(destination.Path, $"mail-evidence-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{jobId:N}");
        var stage = Path.Combine(destination.Path, $".mail-viewer-{jobId:N}.partial");
        var journal = Path.Combine(JobsDirectory, $"{jobId:N}.json");
        var published = false;
        var receiptWritten = false;
        var items = new List<ExportItem>();
        var written = new List<string>();
        Directory.CreateDirectory(stage);

        try
        {
            if (request.Kind == ExportKind.Csv)
            {
                var path = Reserve("messages.csv");
                WriteCsv(path, records, ct);
                items.Add(Describe(path, "analysis-csv", null, ct));
            }
            else if (request.Kind == ExportKind.MessageBytes)
            {
                for (var i = 0; i < records.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var record = records[i];
                    var (name, nature) = record.Format switch
                    {
                        MailFormat.Eml => ("original.eml", "eml-original"),
                        MailFormat.Msg => ("original.msg", "msg-original"),
                        MailFormat.Mbox => ("mbox-range.bin", "mbox-raw-range"),
                        _ => throw new InvalidDataException("지원하지 않는 원본 형식입니다.")
                    };
                    var path = Reserve($"{i + 1:D5}-{name}");
                    WriteNewFile(path, session.ReadRange(record.SourceId, record.MessageRange, ct));
                    var item = Describe(path, nature, record.Id, ct);
                    if (record.Format != MailFormat.Mbox && !item.Sha256.Equals(sourceById[record.SourceId].Sha256,
                            StringComparison.OrdinalIgnoreCase))
                        throw new IOException("저장한 파일이 확인한 원본 SHA-256과 다릅니다.");
                    items.Add(item with { InputRange = record.MessageRange });
                }
            }
            else if (request.Kind is ExportKind.PstEvidence or ExportKind.OstEvidence)
            {
                var kind = request.Kind == ExportKind.PstEvidence ? "pst" : "ost";
                for (var i = 0; i < records.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var record = records[i];
                    var source = sourceById[record.SourceId];
                    var metadataPath = Reserve($"{i + 1:D5}-{kind}-message.json");
                    WriteNewFile(metadataPath, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        Source = source,
                        Message = RecordEntry.FromRecord(record),
                        record.Attachments
                    }, Json));
                    items.Add(Describe(metadataPath, $"{kind}-message-metadata", record.Id, ct));

                    if (record.TransportHeaderProperty is not { } header ||
                        record.TransportHeaderState is not (HeaderEvidenceState.Present or HeaderEvidenceState.Empty))
                        continue;
                    var headerPath = Reserve($"{i + 1:D5}-transport-header.bin");
                    WriteNewFile(headerPath, session.ReadPstPropertyRange(record.SourceId, header,
                        new ByteRange(0, header.Length), ct));
                    items.Add(Describe(headerPath, $"{kind}-transport-header-property", record.Id, ct)
                        with { PstProperty = header });
                }
            }
            else
            {
                for (var i = 0; i < attachments.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var selection = attachments[i];
                    var record = RequireRecord(selection.RecordId);
                    var attachment = record.Attachments.FirstOrDefault(x => x.Id == selection.AttachmentId)
                        ?? throw new InvalidOperationException("선택한 첨부파일을 찾을 수 없습니다.");
                    var path = Reserve($"{i + 1:D5}-{SafeName(attachment.Name)}");
                    var nature = attachment.PstProperty is not null ?
                        (record.Format == MailFormat.Ost ? "ost-attachment-property" : "pst-attachment-property")
                        : attachment.CompoundStream is not null ? "msg-attachment-stream" : "decoded-attachment";
                    var item = WriteVerifiedAttachment(session, record.Id, attachment, path, nature, ct);
                    items.Add(item with
                    {
                        AttachmentId = attachment.Id, PartPath = attachment.PartPath,
                        InputRange = attachment.EncodedRange, OriginalName = attachment.Name,
                        TransferEncoding = attachment.TransferEncoding,
                        CompoundStream = attachment.CompoundStream,
                        PstProperty = attachment.PstProperty
                    });
                }
            }

            var sourceIds = records.Select(x => x.SourceId).ToHashSet(StringComparer.Ordinal);
            if (request.Scope != ExportScope.SelectedAttachments)
            {
                var comparisons = new List<ReplyHistory>();
                foreach (var record in records)
                {
                    ct.ThrowIfCancellationRequested();
                    var comparison = session.CompareReplyHistory(record.Id, ct);
                    if (comparison is not { Status: ReplyHistoryStatus.Changed, Earlier: { } earlier }) continue;
                    sourceIds.Add(earlier.SourceId);
                    comparisons.Add(comparison);
                }
                if (comparisons.Count > 0)
                {
                    var path = Reserve("reply-address-changes.json");
                    var evidence = new ReplyHistoryExport("mail-viewer/reply-address-history/1",
                        ConversationIndex.WindowDays, session.Sources.Select(source => source.Id).ToArray(),
                        comparisons.ToArray());
                    foreach (var source in session.Sources) sourceIds.Add(source.Id);
                    using (var stream = new FileStream(path, FileMode.CreateNew))
                        JsonSerializer.Serialize(stream, evidence, Json);
                    items.Add(Describe(path, "reply-address-change-evidence", null, ct));
                }
            }
            var searchScope = search is not null ? request.Search : null;
            var groupedSources = searchScope?.SourceGroup?.SourceIds.ToHashSet(StringComparer.Ordinal);
            var bookmarkSources = searchScope?.BookmarkRecordIds?.Where(recordById.ContainsKey)
                .Select(id => recordById[id].SourceId).ToHashSet(StringComparer.Ordinal);
            var sources = session.Sources.Where(source => sourceIds.Contains(source.Id) ||
                (searchScope is not null && (searchScope.SourceId is null || searchScope.SourceId == source.Id) &&
                 (groupedSources is null || groupedSources.Contains(source.Id)) &&
                 (bookmarkSources is null || bookmarkSources.Contains(source.Id)))).ToArray();
            var manifest = new PackageManifest(
                Schema, typeof(ExportService).Assembly.GetName().Version?.ToString() ?? "1.0",
                jobId, DateTimeOffset.UtcNow, request.Scope, request.Kind,
                search is null ? null : request.Search, request.DateTimeZone,
                search?.ExaminedCount, search?.DateUnknownCount, search?.Issues.ToArray() ?? [],
                sources, records.Select(RecordEntry.FromRecord).ToArray(), items.ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            if (bytes.Length > MaxManifestBytes)
                throw new IOException("내보내기 기록이 너무 큽니다. 범위를 나누어 저장해 주세요.");
            var manifestPath = Reserve(ManifestName);
            WriteNewFile(manifestPath, bytes);
            var verified = Verify(stage, ct);
            if (!verified.Success)
                throw new IOException(string.Join(Environment.NewLine, verified.Errors));
            Directory.CreateDirectory(JobsDirectory);
            WriteNewFile(journal, JsonSerializer.SerializeToUtf8Bytes(
                new JobReceipt(jobId, folder, Digest(bytes)), Json));
            receiptWritten = true;

            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, folder);
            published = true;
            return new ExportResult(folder, jobId, items.AsReadOnly());
        }
        finally
        {
            if (!published)
            {
                if (receiptWritten)
                    TryDeleteOwnedFile(journal);
                TryCleanStage(stage, written);
            }
        }

        MailRecord RequireRecord(string id) => recordById.TryGetValue(id, out var record)
            ? record
            : throw new InvalidOperationException("선택한 메일을 현재 자료에서 찾을 수 없습니다.");

        string Reserve(string name)
        {
            var path = Path.Combine(stage, name);
            written.Add(path);
            return path;
        }
    }

    public static VerificationResult Verify(string packageDirectory, CancellationToken ct = default)
    {
        var errors = new List<string>();
        PackageManifest? manifest = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            using var package = ExportDirectoryLease.Open(packageDirectory);
            var manifestPath = Path.Combine(package.Path, ManifestName);
            using (var stream = OpenEvidence(manifestPath))
            {
                if (stream.Length is <= 0 or > MaxManifestBytes)
                    throw new InvalidDataException("내보내기 기록 크기가 올바르지 않습니다.");
                manifest = JsonSerializer.Deserialize<PackageManifest>(stream, Json);
            }
            if (manifest?.Schema != Schema || manifest.Items is null || manifest.Items.Length == 0 ||
                manifest.JobId == Guid.Empty)
                throw new InvalidDataException("지원하는 내보내기 기록을 찾을 수 없습니다.");

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
            foreach (var item in manifest.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (item is null || !IsFlatFileName(item.RelativePath) || !expected.Add(item.RelativePath))
                {
                    errors.Add("내보내기 기록에 잘못되거나 중복된 파일 이름이 있습니다.");
                    continue;
                }

                try
                {
                    var actual = Describe(Path.Combine(package.Path, item.RelativePath), item.Nature,
                        item.RecordId, ct);
                    if (actual.Length != item.Length ||
                        !actual.Sha256.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"파일 크기 또는 SHA-256이 다릅니다. {item.RelativePath}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"파일을 검증할 수 없습니다. {item.RelativePath}");
                }
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(package.Path))
                if (!expected.Contains(Path.GetFileName(path)))
                    errors.Add($"내보내기 기록에 없는 항목입니다. {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            errors.Add(ex.Message);
        }

        return new VerificationResult(packageDirectory, errors.Count == 0, manifest?.JobId,
            manifest?.Items ?? [], errors.AsReadOnly());
    }

    public static IReadOnlyList<VerificationResult> RecoverRecentExports(CancellationToken ct = default)
    {
        if (!Directory.Exists(JobsDirectory))
            return [];
        var recovered = new List<VerificationResult>();
        foreach (var path in Directory.EnumerateFiles(JobsDirectory, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(20))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var receiptStream = OpenEvidence(path);
                if (receiptStream.Length is <= 0 or > 64 * 1024)
                    continue;
                var receipt = JsonSerializer.Deserialize<JobReceipt>(receiptStream, Json);
                if (receipt is null)
                    continue;
                var result = Verify(receipt.PackageDirectory, ct);
                if (result.Success)
                {
                    using var package = ExportDirectoryLease.Open(receipt.PackageDirectory);
                    var digest = Describe(Path.Combine(package.Path, ManifestName), "manifest", null, ct).Sha256;
                    if (receipt.JobId != result.JobId ||
                        !digest.Equals(receipt.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                        result = result with { Success = false, Errors = ["작업 기록과 저장한 매니페스트가 다릅니다."] };
                }
                recovered.Add(result);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                recovered.Add(new VerificationResult(path, false, null, [], [ex.Message]));
            }
        }
        return recovered.AsReadOnly();
    }

    private static string JobsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForensicMailViewer", "jobs");

    private static FileStream OpenEvidence(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("일반 파일만 읽을 수 있습니다.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public static string SuggestedAttachmentFileName(string name) => SafeName(name);

    public static void SaveAttachment(EvidenceSession session, string recordId, MailAttachment attachment,
        string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(filePath);
        if (!IsFlatFileName(Path.GetFileName(target)))
            throw new ArgumentException("올바른 파일 이름을 지정해 주세요.", nameof(filePath));
        string directory = Path.GetDirectoryName(target)!;
        string stage = Path.Combine(directory, $".mail-viewer-{Guid.NewGuid():N}.partial");
        try
        {
            WriteVerifiedAttachment(session, recordId, attachment, stage, "attachment", ct);
            ct.ThrowIfCancellationRequested();
            File.Move(stage, target);
        }
        finally
        {
            TryDeleteOwnedFile(stage);
        }
    }

    private static ExportItem WriteVerifiedAttachment(EvidenceSession session, string recordId,
        MailAttachment attachment, string path, string nature, CancellationToken ct)
    {
        WriteNewFile(path, session.ReadAttachment(recordId, attachment.Id, ct));
        var item = Describe(path, nature, recordId, ct);
        if (item.Length != attachment.DecodedLength ||
            !item.Sha256.Equals(attachment.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("저장한 첨부파일의 크기 또는 SHA-256이 확인한 값과 다릅니다.");
        return item;
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    private static ExportItem Describe(string path, string nature, string? id, CancellationToken ct)
    {
        using var stream = OpenEvidence(path);
        var digest = SHA256.HashDataAsync(stream, ct).AsTask().GetAwaiter().GetResult();
        return new ExportItem(Path.GetFileName(path), nature, stream.Length,
            Convert.ToHexStringLower(digest), id);
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsFlatFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." &&
        Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) == -1 &&
        name[^1] is not '.' and not ' ';

    private static string SafeName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.EnumerateRunes().Take(90).Select(rune =>
            rune.IsAscii && invalidChars.Contains((char)rune.Value) ? "_" : rune.ToString())).Trim(' ', '.');
        return safe.Length == 0 ? "attachment.bin" : safe;
    }

    private static void WriteCsv(string path, IReadOnlyList<MailRecord> records, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" };
        const string columns = "RecordId,SourcePath,Offset,Length,Subject,From,To,Cc,Bcc,MessageId,DateRaw,DateUtc,Attachments";
        bool includeOst = records.Any(record => record.Format == MailFormat.Ost);
        bool includePstOnly = !includeOst && records.Any(record => record.Format == MailFormat.Pst);
        writer.WriteLine(includeOst ? columns + ",StoreFormat,StoreFolderPath,StoreFolderNid,StoreMessageNid,StoreTransportHeaderTag"
            : includePstOnly ? columns + ",PstFolderPath,PstFolderNid,PstMessageNid,PstTransportHeaderTag" : columns);
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            string[] values =
            [
                record.Id, record.SourcePath,
                record.Format.IsFolderStore() ? "" : record.MessageRange.Offset.ToString(CultureInfo.InvariantCulture),
                record.Format.IsFolderStore() ? "" : record.MessageRange.Length.ToString(CultureInfo.InvariantCulture),
                record.Subject, record.From,
                record.To, record.Cc, record.Bcc, record.MessageId, record.DateRaw,
                record.Date?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "",
                record.Attachments.Count.ToString(CultureInfo.InvariantCulture)
            ];
            if (includeOst)
                values = [.. values, record.Format.ToString()];
            if (includePstOnly || includeOst)
                values =
                [
                    .. values, record.PstMessage?.FolderPath ?? "", record.PstMessage?.FolderNid ?? "",
                    record.PstMessage?.MessageNid ?? "", record.TransportHeaderProperty?.TagHex ?? ""
                ];
            writer.WriteLine(string.Join(',', values.Select(CsvCell)));
        }
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string CsvCell(string value)
    {
        const string formulaStarts = "=+-@＝＋－＠";
        var trimmed = value.TrimStart();
        if ((value.Length != 0 && value[0] is '\t' or '\r' or '\n') ||
            (trimmed.Length != 0 && formulaStarts.Contains(trimmed[0])))
            value = "[text] " + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static void TryDeleteOwnedFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryCleanStage(string stage, List<string> written)
    {
        try
        {
            using (ExportDirectoryLease.Open(stage))
                foreach (var file in written)
                    TryDeleteOwnedFile(file);
            Directory.Delete(stage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record ReplyHistoryExport(string Schema, int WindowDays, string[] ComparedSourceIds,
        ReplyHistory[] Comparisons);

    private sealed record JobReceipt(Guid JobId, string PackageDirectory, string ManifestSha256);

    private sealed record RecordEntry(
        string Id, string SourceId, ByteRange? MessageRange, ByteRange? HeaderRange,
        ByteRange? EnvelopeRange, ByteRange? SeparatorRange, string Subject, string From,
        string To, string Cc, string Bcc, string MessageId, string DateRaw, DateTimeOffset? Date,
        int AttachmentCount, MailFormat Format, CompoundStreamLocator? TransportHeaderStream,
        HeaderEvidenceState? TransportHeaderState, string? TransportHeaderDetail,
        PstMessageLocator? PstMessage, PstPropertyLocator? TransportHeaderProperty)
    {
        public static RecordEntry FromRecord(MailRecord r) => new(r.Id, r.SourceId,
            r.Format.IsFolderStore() ? null : r.MessageRange,
            r.Format.IsFolderStore() && r.TransportHeaderProperty is null ? null : r.HeaderRange,
            r.EnvelopeRange, r.SeparatorRange, r.Subject, r.From, r.To, r.Cc, r.Bcc, r.MessageId, r.DateRaw,
            r.Date, r.Attachments.Count, r.Format, r.TransportHeaderStream, r.TransportHeaderState, r.TransportHeaderDetail,
            r.PstMessage, r.TransportHeaderProperty);

        public string HeaderRangeKind => Format switch
        {
            MailFormat.Pst => TransportHeaderProperty is null ? "unavailable" : "pst-property-offset",
            MailFormat.Ost => TransportHeaderProperty is null ? "unavailable" : "ost-property-offset",
            MailFormat.Msg => TransportHeaderStream is null ? "unavailable" : "compound-stream-offset",
            _ => "file-offset"
        };
    }

    private sealed record PackageManifest(
        string Schema, string EngineVersion, Guid JobId, DateTimeOffset CreatedUtc, ExportScope Scope,
        ExportKind Kind, SearchRequest? Search, string? DateTimeZone, int? ExaminedCount, int? DateUnknownCount,
        ReadIssue[] SearchIssues, SourceInfo[] Sources, RecordEntry[] Records, ExportItem[] Items)
    {
        public string CsvCellPolicy => "Quoted UTF-8 CSV. Formula-like fields have a [text] prefix. Record values are in this manifest.";
        public string MessageBytesPolicy => "EML and MSG are full copies. MBOX message bytes exclude the separately recorded envelope and separator ranges.";
        public string PstEvidencePolicy => "PST message metadata identifies a logical item. Available transport headers and selected attachments are copied as property bytes.";
        public string OstEvidencePolicy => "OST message metadata identifies a logical item. Available transport headers and selected attachments are copied as logical property bytes after any NDB decoding or decompression.";
    }
}
