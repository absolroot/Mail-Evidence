using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MimeKit.Text;
using MimeKit.Utils;
using XstReader;

namespace MailViewer.Core;

internal static class PstReader
{
    public const string BoundaryRule = "pst-unicode23-property-v1";
    private const string OstBoundaryRule = "ost-unicode36-4k-property-v1";
    private const int AnsiEncodingOffset = 461;
    private const int UnicodeEncodingOffset = 513;
    private const int AnsiHeaderCrcEnd = 479;
    private const int UnicodeHeaderCrcOffset = 524;
    public const long MaximumFileLength = 2L * 1024 * 1024 * 1024;
    private const int MetadataLimit = 1024 * 1024;
    private const int TextLimit = 4 * 1024 * 1024;
    private const int AttachmentLimit = 128 * 1024 * 1024;
    private const int FolderLimit = 10_000;
    private const int MessageLimit = 250_000;
    private const ushort Unicode = 0x001f;
    private const ushort Ansi = 0x001e;
    private const ushort Binary = 0x0102;
    private const ushort TransportHeaders = 0x007d;
    private const ushort AttachData = 0x3701;
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);

    static PstReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static MailFormat? FindFormat(SafeFileHandle handle)
    {
        Span<byte> prefix = stackalloc byte[10];
        if (RandomAccess.Read(handle, prefix, 0) != prefix.Length || !prefix[..4].SequenceEqual("!BDN"u8))
            return null;
        return prefix[8..].SequenceEqual("SO"u8) ? MailFormat.Ost
            : prefix[8..].SequenceEqual("SM"u8) ? MailFormat.Pst
            : null;
    }

    public static string BoundaryRuleForOst(StoreLayoutInfo layout)
    {
        if (layout is { Version: 36, BlockEncoding: 0 })
            return OstBoundaryRule;
        string width = layout.Version is 14 or 15 ? "ansi" : "unicode";
        return FormattableString.Invariant($"ost-{width}{layout.Version}-512-encoding{layout.BlockEncoding}-property-v1");
    }

    public static StoreLayoutInfo ValidateHeader(SafeFileHandle handle, long length, MailFormat format, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ost = format == MailFormat.Ost;
        if (length > MaximumFileLength)
            throw new InvalidDataException($"{(ost ? "OST" : "PST")} 파일 크기가 2 GiB 판독 한도를 넘습니다.");
        using var input = new SourceRangeStream(handle, new ByteRange(0, length), ct);
        Span<byte> header = stackalloc byte[UnicodeHeaderCrcOffset + sizeof(uint)];
        input.ReadExactly(header[..12]);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        if (!header[..4].SequenceEqual("!BDN"u8) || !header.Slice(8, 2).SequenceEqual(ost ? "SO"u8 : "SM"u8) ||
            (!ost && version != 23))
            throw new InvalidDataException(ost ? "OST 파일 시그니처가 일치하지 않습니다."
                : "Unicode 23 형식의 PST를 열 수 있습니다.");
        int pageSize = version switch { 14 or 15 or 21 or 23 => 512, 36 => 4096, _ => 0 };
        if (pageSize == 0)
            throw new UnsupportedOstLayoutException("ost-unsupported-version", new(version, 0, null),
                $"OST 파일의 NDB 버전 {version}을 지원하지 않습니다.");
        bool ansi = version is 14 or 15;
        int encodingOffset = ansi ? AnsiEncodingOffset : UnicodeEncodingOffset;
        int headerReadEnd = !ost ? UnicodeEncodingOffset + 1 : ansi ? AnsiHeaderCrcEnd : header.Length;
        input.ReadExactly(header[12..headerReadEnd]);
        byte encoding = header[encodingOffset];
        var layout = new StoreLayoutInfo(version, pageSize, encoding);
        if (ost)
        {
            ReadOnlySpan<byte> crcData = ansi ? header[8..AnsiHeaderCrcEnd] : header[8..UnicodeHeaderCrcOffset];
            int crcOffset = ansi ? 4 : UnicodeHeaderCrcOffset;
            if (NDB.CalculateWeakCrc(crcData) != BinaryPrimitives.ReadUInt32LittleEndian(header[crcOffset..]))
                throw new InvalidDataException("OST 파일 헤더의 CRC가 일치하지 않습니다.");
            if (encoding > 2 || (pageSize == 4096 && encoding != 0))
                throw new UnsupportedOstLayoutException("ost-unsupported-encoding", layout,
                    encoding == 0x10
                        ? "Windows Information Protection으로 보호된 OST 파일은 열 수 없습니다."
                        : $"OST 버전 {version}의 블록 인코딩 0x{encoding:X2}을 지원하지 않습니다.");
        }
        else if (encoding is not (0 or 1))
            throw new InvalidDataException("이 PST의 블록 인코딩 방식은 지원하지 않습니다.");
        return layout;
    }

    public static void Parse(SourceInfo source, SafeFileHandle handle, List<MailRecord> records,
        List<PstFolder> folders, List<ReadIssue> issues, CancellationToken ct)
    {
        var database = OpenDatabase(source, handle, ct);
        var format = source.Format == MailFormat.Ost ? "OST" : "PST";
        var issuePrefix = format.ToLowerInvariant();
        using var input = database.GetReadStream();
        var tables = new PstTables(database, input, ct);
        var pending = new Stack<(uint Id, uint? Parent, string ParentPath, int Depth)>();
        var seenFolders = new HashSet<uint>();
        var seenMessages = new HashSet<uint>();
        pending.Push(((uint)EnidSpecial.NID_ROOT_FOLDER, null, "", 0));
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            if (seenFolders.Count >= FolderLimit)
            {
                issues.Add(new ReadIssue(source.Id, $"{issuePrefix}-folder-limit", $"{format} 폴더 판독 한도에 도달했습니다."));
                break;
            }
            if (folder.Depth > 100 || !seenFolders.Add(folder.Id))
            {
                AddFolderIssue("folder-tree", "폴더 계층이 반복되었거나 깊이 한도를 넘습니다.", folder.Parent);
                continue;
            }
            var name = $"0x{folder.Id:X8}";
            var declaredCount = 0u;
            Node? folderNode;
            try
            {
                folderNode = tables.FindNode(folder.Id);
            }
            catch (Exception error) when (IsReadError(error))
            {
                AddFolderIssue("folder-node", $"폴더 NID {name}의 원본을 읽지 못했습니다. {error.Message}", folder.Parent);
                continue;
            }
            if (folderNode is null || folderNode.Type != EnidType.NORMAL_FOLDER)
            {
                AddFolderIssue("folder-node", $"폴더 NID {name}의 원본을 찾을 수 없습니다.", folder.Parent);
                continue;
            }
            try
            {
                var properties = tables.ReadProperties(folderNode);
                name = NonBlank(ReadString(properties, 0x3001, GetEncoding(properties))) ?? name;
                declaredCount = InlineValue(properties, 0x3602);
            }
            catch (Exception error) when (IsReadError(error))
            {
                AddFolderIssue("folder-properties", $"폴더 속성을 읽지 못했습니다. {name} · {error.Message}", folder.Id);
            }

            var path = folder.ParentPath + "/" + name;
            folders.Add(new PstFolder(source.Id, folder.Id, folder.Parent, name, path));
            var folderNid = new NID(folder.Id);
            try
            {
                var hierarchy = tables.FindNode(NID.TypedNID(EnidType.HIERARCHY_TABLE, folderNid).dwValue);
                var childIds = tables.ReadRowIds(hierarchy);
                for (var i = childIds.Length - 1; i >= 0; i--)
                {
                    var child = new NID(childIds[i]);
                    if (child.nidType == EnidType.NORMAL_FOLDER)
                        pending.Push((child.dwValue, folder.Id, path, folder.Depth + 1));
                }
            }
            catch (Exception error) when (IsReadError(error))
            {
                AddFolderIssue("hierarchy", $"하위 폴더 목록을 읽지 못했습니다. {path} · {error.Message}", folder.Id);
            }

            try
            {
                var contents = tables.FindNode(NID.TypedNID(EnidType.CONTENTS_TABLE, folderNid).dwValue);
                if (contents is null && declaredCount != 0)
                    AddFolderIssue("contents-missing", $"메시지 목록을 찾을 수 없습니다. {path}", folder.Id);
                foreach (var id in tables.ReadRowIds(contents))
                {
                    ct.ThrowIfCancellationRequested();
                    if (new NID(id).nidType != EnidType.NORMAL_MESSAGE)
                        continue;
                    if (seenMessages.Count >= MessageLimit)
                    {
                        issues.Add(new ReadIssue(source.Id, $"{issuePrefix}-message-limit", $"{format} 메시지 판독 한도에 도달했습니다."));
                        return;
                    }
                    if (!seenMessages.Add(id))
                    {
                        AddFolderIssue("message-repeat", $"메시지 NID가 여러 콘텐츠 행에 나타납니다. 0x{id:X8}", folder.Id);
                        continue;
                    }
                    var message = new PstMessageLocator(folder.Id, id, path);
                    try
                    {
                        var node = tables.FindNode(id) ?? throw new InvalidDataException("메시지 노드가 없습니다.");
                        if (ReadMessage(source, message, tables, tables.ReadProperties(node), issues, ct) is { } record)
                            records.Add(record);
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        issues.Add(new ReadIssue(source.Id, $"{issuePrefix}-message", $"메시지를 읽지 못했습니다. 0x{id:X8} · {error.Message}",
                            RecordId: RecordId(source, id), PstFolderNodeId: folder.Id));
                    }
                }
            }
            catch (Exception error) when (IsReadError(error))
            {
                AddFolderIssue("contents", $"메시지 목록을 읽지 못했습니다. {path} · {error.Message}", folder.Id);
            }
        }

        void AddFolderIssue(string code, string detail, uint? nodeId) =>
            issues.Add(new ReadIssue(source.Id, $"{issuePrefix}-{code}", detail, PstFolderNodeId: nodeId));
    }

    public static byte[] ReadRange(SourceInfo source, SafeFileHandle handle, PstPropertyLocator locator,
        ByteRange range, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(range.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(range.Length);
        if (range.Offset > locator.Length || range.Length > locator.Length - range.Offset ||
            locator.Length > AttachmentLimit)
            throw new ArgumentOutOfRangeException(nameof(range), "MAPI 속성의 논리 범위를 벗어납니다.");
        try
        {
            var database = OpenDatabase(source, handle, ct);
            using var input = database.GetReadStream();
            var tables = new PstTables(database, input, ct);
            var node = tables.FindNode(locator.Message.MessageNodeId)
                ?? throw new InvalidDataException("메시지 NID를 찾을 수 없습니다.");
            var context = tables.ReadProperties(node);
            if (locator.AttachmentNodeId is { } attachmentId)
            {
                var attachment = PstTables.FindSubnode(context.Subnodes, attachmentId)
                    ?? throw new InvalidDataException("첨부 NID를 찾을 수 없습니다.");
                context = tables.ReadProperties(attachment);
            }
            var property = context.Find((ushort)(locator.PropertyTag >> 16));
            if (property is null || property.Value.Tag != locator.PropertyTag)
                throw new InvalidDataException("MAPI 속성 태그가 확인한 값과 일치하지 않습니다.");
            return context.ReadRange(property.Value, locator.Length, range);
        }
        catch (XstException error)
        {
            throw new InvalidDataException($"MAPI 속성을 다시 읽지 못했습니다. {error.Message}", error);
        }
    }

    private static NDB OpenDatabase(SourceInfo source, SafeFileHandle handle, CancellationToken ct)
    {
        var database = new NDB(() => new SourceRangeStream(handle, new ByteRange(0, source.Length), ct));
        try
        {
            database.Initialise();
        }
        catch (XstException error)
        {
            throw new InvalidDataException($"노드 데이터베이스를 읽지 못했습니다. {error.Message}", error);
        }
        return database;
    }

    private static MailRecord? ReadMessage(SourceInfo source, PstMessageLocator location, PstTables tables,
        PstTables.PropertyContext context, List<ReadIssue> issues, CancellationToken ct)
    {
        var id = RecordId(source, location.MessageNodeId);
        var ost = source.Format == MailFormat.Ost;
        var (encoding, codepageKnown) = ResolveEncoding(context);
        var messageClass = SafeString(0x001a, null);
        if (messageClass is not null && !messageClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase) &&
            !messageClass.StartsWith("REPORT.IPM.Note", StringComparison.OrdinalIgnoreCase))
            return null;
        IReadOnlyList<MailHeader> headers = [];
        var headerState = HeaderEvidenceState.Missing;
        PstPropertyLocator? headerLocator = null;
        string? headerDetail = ost ? "이 OST 캐시에 저장된 전송 헤더 속성이 없습니다."
            : "저장된 전송 헤더 속성이 없습니다.";
        if (context.Find(TransportHeaders) is { } property)
        {
            try
            {
                if (property.Type is not (Ansi or Unicode))
                    throw new InvalidDataException("전송 헤더 문자열 형식을 지원하지 않습니다.");
                var bytes = context.ReadBytes(property, MetadataLimit);
                headerLocator = new PstPropertyLocator(location, property.Tag, bytes.LongLength);
                if (property.Type == Unicode && (bytes.Length & 1) != 0)
                    throw new InvalidDataException("전송 헤더의 UTF-16 바이트 길이가 올바르지 않습니다.");
                headerState = HeaderEvidenceState.Empty;
                headerDetail = "저장된 전송 헤더 값이 비어 있습니다.";
                if (bytes.Any(value => value != 0))
                {
                    headers = MsgReader.ParseHeaderRows(bytes, property.Type == Unicode ? Utf16 : encoding,
                        property.Type == Unicode ? 2 : 1);
                    headerState = HeaderEvidenceState.Present;
                    headerDetail = property.Type == Ansi && !codepageKnown
                        ? "ANSI 코드페이지 정보가 없어 Windows-1252로 표시합니다." : null;
                }
            }
            catch (Exception error) when (IsReadError(error))
            {
                headerState = HeaderEvidenceState.Unreadable;
                headerDetail = $"저장된 전송 헤더를 읽지 못했습니다. {error.Message}";
                AddIssue("header", headerDetail);
            }
        }

        var bodies = new List<MailBodyPart>(3);
        var hasBodyProperty = false;
        foreach (var (propertyId, mime) in new[] { ((ushort)0x1000, "text/plain"), ((ushort)0x1013, "text/html"), ((ushort)0x1009, "text/rtf") })
        {
            ct.ThrowIfCancellationRequested();
            if (context.Find(propertyId) is not { } bodyProperty)
                continue;
            hasBodyProperty = true;
            try
            {
                byte[] storedBody = context.ReadBytes(bodyProperty, TextLimit);
                string content;
                if (bodyProperty.Type is Ansi or Unicode)
                    content = (bodyProperty.Type == Unicode ? Utf16 : encoding).GetString(storedBody).TrimEnd('\0');
                else if (bodyProperty.Type == Binary && mime == "text/html")
                    content = MsgReader.ReadHtmlText(storedBody, encoding);
                else if (bodyProperty.Type == Binary && mime == "text/rtf")
                {
                    using var rtf = new MemoryStream(MsgRtf.Decompress(storedBody, TextLimit, ct));
                    using var text = new StringWriter(CultureInfo.InvariantCulture);
                    new RtfToText().Convert(rtf, text);
                    content = text.ToString();
                }
                else
                    throw new InvalidDataException("본문 증거의 속성 형식을 지원하지 않습니다.");
                var bodyLocator = new PstPropertyLocator(location, bodyProperty.Tag, storedBody.LongLength);
                var bodyLocation = new EvidenceLocation(new ByteRange(0, bodyLocator.Length), Property: bodyLocator);
                var (links, limited) = InvestigationArtifacts.ReadLinks(content, mime, $"0x{bodyProperty.Tag:X8}", bodyLocation, ct);
                if (limited)
                    AddIssue("link-observation-limit", $"{mime} 본문의 링크 수가 판독 한도를 초과했습니다.", SearchField.Body);
                HtmlBodySource? htmlSource = null;
                if (mime == "text/html" && !string.IsNullOrWhiteSpace(content))
                {
                    htmlSource = new(bodyLocation, CodePage: (bodyProperty.Type == Unicode ? Utf16 : encoding).CodePage);
                    content = new HtmlToText().Convert(content);
                }
                if (!string.IsNullOrWhiteSpace(content) || links.Count > 0 || htmlSource is not null)
                    bodies.Add(new MailBodyPart($"0x{bodyProperty.Tag:X8}", mime, content) { Links = links, HtmlSource = htmlSource });
            }
            catch (Exception error) when (IsReadError(error))
            {
                AddIssue("body", $"본문 속성 0x{bodyProperty.Tag:X8}을 읽지 못했습니다. {error.Message}", SearchField.Body);
            }
        }
        if (ost && !hasBodyProperty)
            AddIssue("body-not-stored", "이 OST 캐시에 저장된 본문 속성이 없습니다.", SearchField.Body);

        var attachments = ReadAttachments();
        var dateRaw = Header("Date") ?? "";
        var subject = ReadSubject();
        return new MailRecord(id, source.Id, source.Path, source.Format, default,
            new ByteRange(0, headerLocator?.Length ?? 0), null, null,
            subject,
            DecodeHeader("From") ?? SafeString(0x5d01, SearchField.From) ?? SafeString(0x0c1f, SearchField.From)
                ?? SafeString(0x0042, SearchField.From) ?? "",
            DecodeHeader("To") ?? SafeString(0x0e04, SearchField.To) ?? "",
            DecodeHeader("Cc") ?? SafeString(0x0e03, SearchField.Cc) ?? "",
            DecodeHeader("Bcc") ?? SafeString(0x0e02, SearchField.Bcc) ?? "",
            Header("Message-ID") ?? SafeString(0x1035, SearchField.MessageId) ?? "",
            dateRaw, MimeMessageReader.ParseTransportDate(headers, dateRaw), headers, bodies.AsReadOnly(), attachments,
            TransportHeaderState: headerState, TransportHeaderDetail: headerDetail, PstMessage: location,
            TransportHeaderProperty: headerLocator)
        {
            TransportHeaderCodePage = headerLocator is { } locator
                ? ((locator.PropertyTag & 0xFFFF) == Unicode ? Utf16 : encoding).CodePage : null,
            StoredFields = ReadStoredFields()
        };

        IReadOnlyList<StoredMailField> ReadStoredFields()
        {
            var fields = new List<StoredMailField>();
            foreach (var (propertyId, name) in MailPropertyNames.Addresses)
            {
                if (context.Find(propertyId) is not { } property || property.Type is not (Ansi or Unicode))
                    continue;
                try
                {
                    byte[] bytes = context.ReadBytes(property, MetadataLimit);
                    string value = (property.Type == Unicode ? Utf16 : encoding).GetString(bytes).TrimEnd('\0');
                    fields.Add(new StoredMailField(name, value,
                        Property: new PstPropertyLocator(location, property.Tag, bytes.LongLength)));
                }
                catch (Exception error) when (IsReadError(error))
                {
                    AddIssue("field", $"문자열 속성 0x{property.Tag:X8}을 읽지 못했습니다.");
                }
            }
            return fields.AsReadOnly();
        }

        string? Header(string name) =>
            headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

        string? DecodeHeader(string name) => Header(name) is { } value
            ? Rfc2047.DecodeText(Encoding.UTF8.GetBytes(value)) : null;

        string ReadSubject()
        {
            var stored = SafeString(0x0037, SearchField.Subject);
            if (stored is null)
                return DecodeHeader("Subject") ?? "";
            if (stored[0] != '\u0001')
                return stored;
            // MS-PST section 2.5.3.1.1.1. The sentinel and prefix length are metadata.
            if (stored.Length > 1 && stored[1] > 0 && stored[1] <= stored.Length - 1)
                return stored[2..];
            AddIssue("subject-prefix", "저장된 제목에서 접두부 길이를 확인하지 못했습니다.", SearchField.Subject);
            return DecodeHeader("Subject") ?? "";
        }

        string? SafeString(ushort propertyId, SearchField? searchField)
        {
            try { return NonBlank(ReadString(context, propertyId, encoding)); }
            catch (Exception error) when (IsReadError(error))
            {
                AddIssue("string", $"문자열 속성 0x{propertyId:X4}을 읽지 못했습니다. {error.Message}", searchField);
                return null;
            }
        }

        void AddIssue(string code, string detail, SearchField? field = null) =>
            issues.Add(new ReadIssue(source.Id, $"{(ost ? "ost" : "pst")}-{code}", detail, RecordId: id,
                Field: field, PstFolderNodeId: location.FolderNodeId));

        IReadOnlyList<MailAttachment> ReadAttachments()
        {
            var found = new List<MailAttachment>();
            uint[] ids;
            try
            {
                var table = PstTables.FindSubnode(context.Subnodes, (uint)EnidSpecial.NID_ATTACHMENT_TABLE);
                ids = tables.ReadRowIds(table);
                var declaresAttachments = (InlineValue(context, 0x0e07) & 0x10) != 0 ||
                    (ost && context.Find(0x0e1b) is { Type: 0x000b, Value: 1 });
                if (declaresAttachments && (table is null || (ost && ids.Length == 0)))
                    AddIssue("attachment-table", "메시지에 첨부 표시가 있지만 첨부 행을 확인할 수 없습니다.", SearchField.AttachmentName);
            }
            catch (Exception error) when (IsReadError(error))
            {
                AddIssue("attachment-table", $"첨부 테이블을 읽지 못했습니다. {error.Message}", SearchField.AttachmentName);
                return found.AsReadOnly();
            }
            for (var index = 0; index < ids.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var subId = ids[index];
                var nid = $"0x{subId:X8}";
                var attachmentId = $"{id}:attachment:{subId:X8}";
                var name = nid;
                var mediaType = "application/octet-stream";
                var locationText = $"NID {nid}";
                EvidenceLocation? nameLocation = null;
                string nameField = "";
                try
                {
                    if (new NID(subId).nidType != EnidType.ATTACHMENT)
                        throw new InvalidDataException("첨부 행이 첨부 노드를 가리키지 않습니다.");
                    var subnode = PstTables.FindSubnode(context.Subnodes, subId)
                        ?? throw new InvalidDataException("첨부 노드를 찾을 수 없습니다.");
                    var metadata = tables.ReadProperties(subnode);
                    foreach (var (propertyId, field) in new[] { ((ushort)0x3707, "PidTagAttachLongFilename"),
                                 ((ushort)0x3704, "PidTagAttachFilename"), ((ushort)0x3001, "PidTagDisplayName") })
                    {
                        if (metadata.Find(propertyId) is not { } nameProperty) continue;
                        if (nameProperty.Type is not (Ansi or Unicode))
                            throw new InvalidDataException("첨부 이름 속성의 유형이 올바르지 않습니다.");
                        byte[] nameBytes = metadata.ReadBytes(nameProperty, MetadataLimit);
                        var storedName = (nameProperty.Type == Unicode ? Utf16 : encoding).GetString(nameBytes).TrimEnd('\0');
                        if (string.IsNullOrWhiteSpace(storedName)) continue;
                        name = storedName;
                        var locator = new PstPropertyLocator(location, nameProperty.Tag, nameBytes.LongLength, subId, index);
                        nameLocation = new EvidenceLocation(new ByteRange(0, locator.Length), Property: locator);
                        nameField = field;
                        break;
                    }
                    mediaType = NonBlank(ReadString(metadata, 0x370e, encoding)) ?? mediaType;
                    if (metadata.Find(0x3705) is not null && InlineValue(metadata, 0x3705) != 1)
                        throw new NotSupportedException("파일 데이터를 저장하지 않는 첨부 방식이거나 지원하지 않는 첨부 종류입니다.");
                    if (metadata.Find(AttachData) is not { Type: Binary } content)
                        throw new InvalidDataException(ost ? "이 OST 캐시에 첨부 데이터 속성(0x37010102)이 없습니다."
                            : "첨부 데이터 속성(0x37010102)이 없습니다.");
                    var bytes = metadata.ReadBytes(content, AttachmentLimit);
                    var property = new PstPropertyLocator(location, content.Tag, bytes.LongLength, subId, index);
                    using var data = new MemoryStream(bytes, writable: false);
                    var analysis = InvestigationArtifacts.InspectAttachment(name, nameLocation, nameField, data);
                    found.Add(new MailAttachment(attachmentId, locationText, name, mediaType, "binary", null,
                        bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), PstProperty: property)
                        { Analysis = analysis });
                }
                catch (Exception error) when (IsReadError(error) || error is NotSupportedException)
                {
                    var detail = $"첨부 {locationText} · {error.Message}";
                    issues.Add(new ReadIssue(source.Id, "attachment-data", detail, RecordId: id,
                        PstFolderNodeId: location.FolderNodeId));
                    if (name == nid)
                        AddIssue("attachment-name", $"첨부 이름을 확인하지 못했습니다. {locationText}", SearchField.AttachmentName);
                    found.Add(new MailAttachment(attachmentId, locationText, name, mediaType, "binary", null, null, null, detail)
                        { Analysis = InvestigationArtifacts.InspectAttachment(name, nameLocation, nameField, null) });
                }
            }
            return found.AsReadOnly();
        }
    }

    private static string RecordId(SourceInfo source, uint nodeId) => $"{source.Id}:{nodeId:X8}";

    private static uint InlineValue(PstTables.PropertyContext context, ushort id) =>
        context.Find(id) is { Type: 0x0003 } value ? value.Value : 0;

    private static Encoding GetEncoding(PstTables.PropertyContext context) => ResolveEncoding(context).Encoding;

    private static (Encoding Encoding, bool Confirmed) ResolveEncoding(PstTables.PropertyContext context)
    {
        var selected = new[] { InlineValue(context, 0x3ffd), InlineValue(context, 0x3fde) };
        foreach (var codepage in selected)
        {
            if (codepage is 0 or > int.MaxValue)
                continue;
            try { return (Encoding.GetEncoding((int)codepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), true); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return (Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), false);
    }

    private static string? ReadString(PstTables.PropertyContext context, ushort id, Encoding encoding, int limit = MetadataLimit)
    {
        if (context.Find(id) is not { } property)
            return null;
        if (property.Type is not (Ansi or Unicode))
            throw new InvalidDataException("문자열 속성의 유형이 올바르지 않습니다.");
        return (property.Type == Unicode ? Utf16 : encoding).GetString(context.ReadBytes(property, limit)).TrimEnd('\0');
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsReadError(Exception error) =>
        error is XstException or IOException or InvalidDataException or ArgumentException or OverflowException;
}

internal sealed class UnsupportedOstLayoutException(string code, StoreLayoutInfo layout, string message) : Exception(message)
{
    public string Code { get; } = code;
    public StoreLayoutInfo Layout { get; } = layout;
}
