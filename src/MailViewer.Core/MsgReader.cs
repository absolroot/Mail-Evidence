using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MimeKit.Text;
using MimeKit.Utils;
using OpenMcdf;

namespace MailViewer.Core;

internal static class MsgReader
{
    public const string BoundaryRule = "msg-cfb-mapi-v1";
    private const string PropertyStreamName = "__properties_version1.0";
    private const string PropertyStreamPrefix = "__substg1.0_";
    private const string AttachmentStoragePrefix = "__attach_version1.0_#";
    private const string RecipientStoragePrefix = "__recip_version1.0_#";
    private const uint HeaderAnsi = 0x007D001E;
    private const uint HeaderUnicode = 0x007D001F;
    private const uint AttachData = 0x37010102;
    private const uint AttachObject = 0x3701000D;
    private const int MaximumTextBytes = 4 * 1024 * 1024;
    private const int MaximumMetadataBytes = 1024 * 1024;
    private const int IOBufferSize = 128 * 1024;
    private static readonly UnicodeEncoding Unicode = new(false, false, true);
    private static readonly byte[] CompoundSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    static MsgReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool HasSignature(SafeFileHandle handle)
    {
        Span<byte> prefix = stackalloc byte[8];
        return RandomAccess.Read(handle, prefix, 0) == prefix.Length && prefix.SequenceEqual(CompoundSignature);
    }

    public static MailRecord? Parse(SourceInfo source, SafeFileHandle handle, List<ReadIssue> issues,
        CancellationToken ct)
    {
        var messageRange = new ByteRange(0, source.Length);
        var recordId = $"{source.Id}:0";
        if (source.Length > MimeMessageReader.MaximumMessageLength)
        {
            issues.Add(new ReadIssue(source.Id, "message-too-large", "128 MiB를 초과해 MSG를 건너뛰었습니다.", messageRange));
            return null;
        }

        try
        {
            using var sourceStream = new SourceRangeStream(handle, messageRange, ct);
            using var root = RootStorage.Open(sourceStream, StorageModeFlags.StrictValidation);
            var properties = ReadProperties(root, 32, ct);
            var encoding = ResolveEncoding(properties);
            var messageClass = ReadString(root, 0x001A, encoding, ct);
            if (messageClass is not null && !IsMailClass(messageClass))
            {
                issues.Add(new ReadIssue(source.Id, "msg-item-type",
                    "메일 MSG만 지원합니다. 이 Outlook 항목은 다른 종류입니다.", messageRange));
                return null;
            }

            var header = ReadTransportHeader(root, properties, encoding, ct);
            var hasAnsiStrings = !encoding.FromProperty && root.EnumerateEntries().Any(entry
                => entry.Type == EntryType.Stream
                    && entry.Name.StartsWith(PropertyStreamPrefix, StringComparison.OrdinalIgnoreCase)
                    && entry.Name.EndsWith("001E", StringComparison.OrdinalIgnoreCase));
            if (hasAnsiStrings)
                issues.Add(new ReadIssue(source.Id, "msg-codepage-fallback", encoding.Note, RecordId: recordId));
            var compoundChildren = root.EnumerateEntries().Where(entry => entry.Type == EntryType.Storage)
                .Select(entry => entry.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            var recipientFields = new List<StoredMailField>();
            var recipients = ReadRecipients();
            var bodies = ReadBodies();
            var attachments = ReadAttachments();

            string? Header(string name) => HeaderValue(header.Rows, name);
            var dateRaw = Header("Date") ?? "";
            var subject = ReadSearchProperty(0x0037, SearchField.Subject) ?? DecodeHeader(Header("Subject")) ?? "";
            var from = Header("From") is { } fromHeader
                ? DecodeHeader(fromHeader) ?? ""
                : Address(ReadSearchProperty(0x0042, SearchField.From) ?? ReadSearchProperty(0x0C1A, SearchField.From),
                    ReadSearchProperty(0x5D02, SearchField.From) ?? ReadSearchProperty(0x5D01, SearchField.From)
                        ?? ReadSearchProperty(0x0065, SearchField.From) ?? ReadSearchProperty(0x0C1F, SearchField.From));
            var to = Recipients(1, 0x0E04, SearchField.To, "To");
            var cc = Recipients(2, 0x0E03, SearchField.Cc, "Cc");
            var bcc = Recipients(3, 0x0E02, SearchField.Bcc, "Bcc");
            var messageId = ReadSearchProperty(0x1035, SearchField.MessageId) ?? Header("Message-ID") ?? "";

            return new MailRecord(recordId, source.Id, source.Path, MailFormat.Msg, messageRange,
                new ByteRange(0, header.Locator?.Length ?? 0), null, null, subject, from, to, cc, bcc, messageId,
                dateRaw, MimeMessageReader.ParseTransportDate(header.Rows, dateRaw), header.Rows, bodies, attachments,
                header.Locator, header.State, header.Detail)
            {
                TransportHeaderCodePage = header.Locator is { } locator
                    ? ((locator.PropertyTag & 0xFFFF) == 0x001F ? Unicode : encoding.Value).CodePage : null,
                StoredFields = ReadStoredFields()
            };

            IReadOnlyList<StoredMailField> ReadStoredFields()
            {
                var fields = new List<StoredMailField>();
                foreach (var (propertyId, name) in MailPropertyNames.Addresses)
                {
                    try
                    {
                        if (ReadStoredText(root, [], propertyId, name) is { } field)
                            fields.Add(field);
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        issues.Add(new ReadIssue(source.Id, "msg-property-read",
                            $"문자열 속성 0x{propertyId:X4}을 읽지 못했습니다.", RecordId: recordId));
                    }
                }
                fields.AddRange(recipientFields);
                return fields.AsReadOnly();
            }

            StoredMailField? ReadStoredText(Storage storage, IReadOnlyList<string> path, ushort propertyId, string name)
            {
                if (StringTag(storage, propertyId) is not { } tag)
                    return null;
                var streamName = StreamName(tag);
                using var stream = storage.OpenStream(streamName);
                var bytes = ReadAll(stream, MaximumMetadataBytes, ct);
                var textEncoding = (tag & 0xFFFF) == 0x001F ? Unicode : encoding.Value;
                string value = textEncoding.GetString(bytes).TrimEnd('\0');
                return new StoredMailField(name, value, new CompoundStreamLocator(path, streamName, tag, stream.Length));
            }

            string? ReadSearchProperty(ushort propertyId, SearchField field)
            {
                try
                {
                    return NonBlank(ReadString(root, propertyId, encoding, ct));
                }
                catch (Exception error) when (IsReadError(error))
                {
                    issues.Add(new ReadIssue(source.Id, "msg-property", $"MSG 속성 0x{propertyId:X4}을 읽을 수 없습니다.",
                        RecordId: recordId, Field: field));
                    return null;
                }
            }

            string Recipients(uint type, ushort displayProperty, SearchField field, string fieldName)
            {
                var addresses = recipients.Where(recipient => recipient.Type == type)
                    .Select(recipient => recipient.Address).ToArray();
                if (addresses.Length > 0)
                    return string.Join(", ", addresses);
                return DecodeHeader(Header(fieldName)) ?? ReadSearchProperty(displayProperty, field) ?? "";
            }

            List<(uint Type, string Address)> ReadRecipients()
            {
                var recipients = new List<(uint, string)>();
                foreach (var path in compoundChildren.Where(name => name.StartsWith(RecipientStoragePrefix,
                             StringComparison.OrdinalIgnoreCase)))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var storage = root.OpenStorage(path);
                        var recipientProperties = ReadProperties(storage, 8, ct);
                        var entryType = recipientProperties.GetValueOrDefault(0x0C150003u) & 0xFFFFu;
                        string role = entryType switch { 1 => "To", 2 => "Cc", 3 => "Bcc", _ => "Recipient" };
                        var nameField = ReadStoredText(storage, [path], 0x3001, $"{role} · display name");
                        var smtpField = ReadStoredText(storage, [path], 0x39FE, $"{role} · SMTP address");
                        var emailField = NonBlank(smtpField?.Value) is null
                            ? ReadStoredText(storage, [path], 0x3003, $"{role} · address") : null;
                        string? name = NonBlank(nameField?.Value);
                        string? email = NonBlank(smtpField?.Value) ?? NonBlank(emailField?.Value);
                        if (name is not null || email is not null)
                            recipients.Add((entryType, Address(name, email)));
                        foreach (StoredMailField? field in new[] { nameField, smtpField, emailField })
                            if (field is not null)
                                recipientFields.Add(field);
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        foreach (var field in new[] { SearchField.To, SearchField.Cc, SearchField.Bcc })
                            issues.Add(new ReadIssue(source.Id, "msg-recipient", $"수신자 저장소 {path}를 읽을 수 없습니다.",
                                RecordId: recordId, Field: field));
                    }
                }
                return recipients;
            }

            IReadOnlyList<MailBodyPart> ReadBodies()
            {
                var found = new List<MailBodyPart>(3);
                ReadBody("text/plain", () =>
                {
                    var tag = StringTag(root, 0x1000);
                    return tag is { } actual
                        ? (ReadString(root, 0x1000, encoding, ct, MaximumTextBytes), actual)
                        : (null, 0u);
                });
                ReadBody("text/html", () =>
                {
                    const uint binaryHtml = 0x10130102;
                    var textTag = StringTag(root, 0x1013);
                    if (TryOpenStream(root, StreamName(binaryHtml), out var stream))
                    {
                        using (stream)
                        {
                            var bytes = ReadAll(stream, MaximumTextBytes, ct);
                            return (ReadHtmlText(bytes, ResolveInternetEncoding(properties, encoding)), binaryHtml);
                        }
                    }
                    return textTag is { } actual
                        ? (ReadString(root, 0x1013, encoding, ct, MaximumTextBytes), actual)
                        : (null, 0u);
                });
                ReadBody("text/rtf", () =>
                {
                    const uint rtfTag = 0x10090102;
                    if (!TryOpenStream(root, StreamName(rtfTag), out var stream))
                        return (null, 0u);
                    using (stream)
                    {
                        var compressed = ReadAll(stream, MaximumTextBytes, ct);
                        using var rtf = new MemoryStream(MsgRtf.Decompress(compressed, MaximumTextBytes, ct));
                        using var result = new StringWriter(CultureInfo.InvariantCulture);
                        new RtfToText().Convert(rtf, result);
                        return (result.ToString(), rtfTag);
                    }
                });
                return found.AsReadOnly();

                void ReadBody(string mediaType, Func<(string? Text, uint Tag)> read)
                {
                    try
                    {
                        var (text, tag) = read();
                        if (string.IsNullOrWhiteSpace(text))
                            return;
                        var streamName = StreamName(tag);
                        using var bodySource = root.OpenStream(streamName);
                        var locator = new CompoundStreamLocator([], streamName, tag, bodySource.Length);
                        var location = new EvidenceLocation(new ByteRange(0, locator.Length), Stream: locator);
                        var (links, limited) = InvestigationArtifacts.ReadLinks(text, mediaType, locator.DisplayPath, location, ct);
                        if (limited)
                            issues.Add(new ReadIssue(source.Id, "link-observation-limit", $"{mediaType} 본문의 링크 수가 판독 한도를 초과했습니다.",
                                RecordId: recordId, Field: SearchField.Body));
                        HtmlBodySource? htmlSource = null;
                        if (mediaType == "text/html")
                        {
                            int page = (tag & 0xffff) switch
                            {
                                0x001f => Unicode.CodePage,
                                0x0102 => ResolveInternetEncoding(properties, encoding).CodePage,
                                _ => encoding.Value.CodePage
                            };
                            htmlSource = new(location, CodePage: page);
                            text = new HtmlToText().Convert(text);
                        }
                        if (!string.IsNullOrWhiteSpace(text) || links.Count > 0 || htmlSource is not null)
                            found.Add(new MailBodyPart(locator.DisplayPath, mediaType, text) { Links = links, HtmlSource = htmlSource });
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        issues.Add(new ReadIssue(source.Id, "msg-body", $"{mediaType} 본문 속성을 읽을 수 없습니다.",
                            RecordId: recordId, Field: SearchField.Body));
                    }
                }
            }

            IReadOnlyList<MailAttachment> ReadAttachments()
            {
                var found = new List<MailAttachment>();
                foreach (var path in compoundChildren.Where(name => name.StartsWith(AttachmentStoragePrefix,
                             StringComparison.OrdinalIgnoreCase)))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var storage = root.OpenStorage(path);
                        var entryProperties = ReadProperties(storage, 8, ct);
                        var method = entryProperties.GetValueOrDefault(0x37050003u);
                        var nameProperty = ReadName(0x3707, "PidTagAttachLongFilename")
                            ?? ReadName(0x3704, "PidTagAttachFilename")
                            ?? ReadName(0x3001, "PidTagDisplayName");
                        var name = NonBlank(nameProperty?.Value) ?? path;
                        var nameLocation = nameProperty?.Stream is { } nameStream
                            ? new EvidenceLocation(new ByteRange(0, nameStream.Length), Stream: nameStream) : null;
                        AttachmentObservation? AnalyzeName(Stream? data) =>
                            InvestigationArtifacts.InspectAttachment(name, nameLocation, nameProperty?.Name ?? "", data);
                        var media = ReadAttachmentText(0x370E) ?? "application/octet-stream";
                        var id = $"{recordId}/{path}";
                        if (storage.TryOpenStorage(StreamName(AttachObject), out _))
                        {
                            IReadOnlyList<string> embeddedPath = Array.AsReadOnly(new[] { path, StreamName(AttachObject) });
                            var reason = method == 5
                                ? "중첩 MSG 저장소입니다. 독립 MSG 파일 바이트로 저장할 수 없습니다."
                                : "첨부 개체 저장소입니다. 파일 스트림으로 저장할 수 없습니다.";
                            found.Add(new MailAttachment(id, "/" + string.Join("/", embeddedPath), name,
                                method == 5 ? "application/vnd.ms-outlook" : media, "CFB",
                                null, null, null, reason, CompoundStoragePath: embeddedPath) { Analysis = AnalyzeName(null) });
                            continue;
                        }

                        if (TryOpenStream(storage, StreamName(AttachData), out var stream))
                        {
                            using (stream)
                            {
                                var locator = new CompoundStreamLocator(Array.AsReadOnly(new[] { path }),
                                    StreamName(AttachData), AttachData, stream.Length);
                                var analysis = AnalyzeName(stream);
                                var digest = Hash(stream, ct);
                                found.Add(new MailAttachment(id, locator.DisplayPath, name, media, "CFB",
                                    null, stream.Length, digest, CompoundStream: locator) { Analysis = analysis });
                            }
                            continue;
                        }

                        found.Add(new MailAttachment(id, "/" + path, name, media, "CFB",
                            null, null, null, "저장할 수 있는 첨부 파일 스트림을 찾지 못했습니다.",
                            CompoundStoragePath: Array.AsReadOnly(new[] { path })) { Analysis = AnalyzeName(null) });

                        StoredMailField? ReadName(ushort propertyId, string field)
                        {
                            try
                            {
                                var stored = ReadStoredText(storage, [path], propertyId, field);
                                return NonBlank(stored?.Value) is null ? null : stored;
                            }
                            catch (Exception error) when (IsReadError(error))
                            {
                                issues.Add(new ReadIssue(source.Id, "msg-attachment-property",
                                    $"첨부 저장소 {path}의 속성 0x{propertyId:X4}을 읽을 수 없습니다.",
                                    RecordId: recordId, Field: SearchField.AttachmentName));
                                return null;
                            }
                        }

                        string? ReadAttachmentText(ushort propertyId)
                        {
                            try { return NonBlank(ReadString(storage, propertyId, encoding, ct)); }
                            catch (Exception error) when (IsReadError(error))
                            {
                                issues.Add(new ReadIssue(source.Id, "msg-attachment-property",
                                    $"첨부 저장소 {path}의 속성 0x{propertyId:X4}을 읽을 수 없습니다.",
                                    RecordId: recordId, Field: SearchField.AttachmentName));
                                return null;
                            }
                        }
                    }
                    catch (Exception error) when (IsReadError(error))
                    {
                        issues.Add(new ReadIssue(source.Id, "msg-attachment", $"첨부 저장소 {path}를 읽을 수 없습니다.",
                            RecordId: recordId, Field: SearchField.AttachmentName));
                        found.Add(new MailAttachment($"{recordId}/{path}", "/" + path, path,
                            "application/octet-stream", "CFB", null, null, null, "첨부 저장소를 읽을 수 없습니다.",
                            CompoundStoragePath: Array.AsReadOnly(new[] { path })));
                    }
                }
                return found.AsReadOnly();
            }
        }
        catch (Exception error) when (IsReadError(error))
        {
            issues.Add(new ReadIssue(source.Id, "msg-structure", "MSG의 CFB 또는 MAPI 속성 구조를 읽을 수 없습니다.",
                messageRange));
            return null;
        }
    }

    public static byte[] ReadRange(SourceInfo source, SafeFileHandle handle, CompoundStreamLocator locator,
        ByteRange range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(locator);
        if (locator.StreamName != StreamName(locator.PropertyTag) || locator.StoragePath.Count > 8)
            throw new ArgumentException("CFB 스트림 식별자가 올바르지 않습니다.", nameof(locator));
        ArgumentOutOfRangeException.ThrowIfNegative(range.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(range.Length);
        if (range.Offset > locator.Length || range.Length > locator.Length - range.Offset ||
            range.Length > MimeMessageReader.MaximumMessageLength)
            throw new ArgumentOutOfRangeException(nameof(range), "요청한 바이트가 CFB 스트림 범위를 벗어납니다.");

        try
        {
            using var sourceStream = new SourceRangeStream(handle, new ByteRange(0, source.Length), ct);
            using var root = RootStorage.Open(sourceStream, StorageModeFlags.StrictValidation);
            Storage storage = root;
            foreach (var name in locator.StoragePath)
                storage = storage.OpenStorage(name);
            using var stream = storage.OpenStream(locator.StreamName);
            if (stream.Length != locator.Length)
                throw new InvalidDataException("CFB 스트림 길이가 분석 시 확인한 값과 다릅니다.");
            stream.Position = range.Offset;
            return ReadBytes(stream, checked((int)range.Length), ct);
        }
        catch (Exception error) when (IsReadError(error))
        {
            throw new InvalidDataException("MSG 원본에서 CFB 스트림을 다시 읽을 수 없습니다.", error);
        }
    }

    private static HeaderData ReadTransportHeader(Storage root, IReadOnlyDictionary<uint, uint> properties,
        MsgEncoding encoding, CancellationToken ct)
    {
        var ansi = root.ContainsEntry(StreamName(HeaderAnsi));
        var unicode = root.ContainsEntry(StreamName(HeaderUnicode));
        if (ansi && unicode)
            return new HeaderData(HeaderEvidenceState.Unreadable,
                "ANSI와 유니코드 전송 헤더가 함께 있어 원본 표현을 확정할 수 없습니다.", null, []);
        if (!ansi && !unicode)
        {
            if (properties.Keys.Any(tag => (tag >> 16) == 0x007D))
                return new HeaderData(HeaderEvidenceState.Unreadable,
                    "MAPI 전송 헤더 속성이 가리키는 CFB 문자열 스트림을 읽을 수 없습니다.", null, []);
            return new HeaderData(HeaderEvidenceState.Missing, "저장된 전송 헤더 속성이 없습니다.", null, []);
        }

        var tag = unicode ? HeaderUnicode : HeaderAnsi;
        var streamName = StreamName(tag);
        CompoundStreamLocator? locator = null;
        try
        {
            using var stream = root.OpenStream(streamName);
            locator = new CompoundStreamLocator(Array.Empty<string>(), streamName, tag, stream.Length);
            var bytes = ReadAll(stream, MaximumMetadataBytes, ct);
            if (bytes.All(value => value == 0))
                return new HeaderData(HeaderEvidenceState.Empty, "저장된 전송 헤더 값이 비어 있습니다.", locator, []);
            var rows = ParseHeaderRows(bytes, unicode ? Unicode : encoding.Value, unicode ? 2 : 1);
            return new HeaderData(HeaderEvidenceState.Present, !unicode && !encoding.FromProperty ? encoding.Note : null,
                locator, rows);
        }
        catch (Exception error) when (IsReadError(error))
        {
            return new HeaderData(HeaderEvidenceState.Unreadable,
                "저장된 전송 헤더의 문자열 또는 헤더 경계를 읽을 수 없습니다.", locator, []);
        }
    }

    internal static IReadOnlyList<MailHeader> ParseHeaderRows(byte[] bytes, Encoding encoding, int characterBytes)
    {
        var count = bytes.Length;
        if (count % characterBytes != 0)
            throw new InvalidDataException("전송 헤더의 문자열 바이트 길이가 올바르지 않습니다.");
        while (count >= characterBytes && ReadCodeUnit(count - characterBytes) == 0)
            count -= characterBytes;
        var rows = new List<MailHeader>();
        var lineStart = 0;
        var fieldStart = 0;
        var fieldEnd = 0;
        var valueEnd = 0;
        var colon = -1;

        while (lineStart < count)
        {
            var nextLine = lineStart;
            while (nextLine < count && ReadCodeUnit(nextLine) is not ('\r' or '\n'))
                nextLine += characterBytes;
            var lineEnd = nextLine;
            if (nextLine < count && ReadCodeUnit(nextLine) == '\r')
                nextLine += characterBytes;
            if (nextLine < count && ReadCodeUnit(nextLine) == '\n')
                nextLine += characterBytes;
            if (lineEnd == lineStart)
                break;

            var isContinuation = ReadCodeUnit(lineStart) is ' ' or '\t';
            if (!isContinuation)
            {
                CommitField();
                fieldStart = lineStart;
                colon = -1;
                for (var position = lineStart; position < lineEnd; position += characterBytes)
                {
                    var unit = ReadCodeUnit(position);
                    if (unit == ':')
                    {
                        colon = position;
                        break;
                    }
                    if (unit is < 33 or > 126)
                        break;
                }
                if (colon <= lineStart)
                    throw new InvalidDataException("전송 헤더의 필드 이름이나 구분 기호가 올바르지 않습니다.");
            }
            else if (colon == -1)
                throw new InvalidDataException("접힌 전송 헤더의 첫 줄을 찾을 수 없습니다.");

            fieldEnd = nextLine;
            valueEnd = lineEnd;
            lineStart = nextLine;
        }

        CommitField();
        if (rows.Count == 0)
            throw new InvalidDataException("전송 헤더에서 필드를 확인할 수 없습니다.");
        return rows.AsReadOnly();

        int ReadCodeUnit(int index) => characterBytes == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index, 2))
            : bytes[index];

        void CommitField()
        {
            if (colon == -1)
                return;
            var valueOffset = colon + characterBytes;
            var name = encoding.GetString(bytes, fieldStart, colon - fieldStart);
            var value = encoding.GetString(bytes, valueOffset, valueEnd - valueOffset);
            rows.Add(new MailHeader(name, value, new ByteRange(fieldStart, fieldEnd - fieldStart)));
        }
    }

    private static Dictionary<uint, uint> ReadProperties(Storage storage, int headerLength, CancellationToken ct)
    {
        using var stream = storage.OpenStream(PropertyStreamName);
        var bytes = ReadAll(stream, MaximumMetadataBytes, ct);
        if (bytes.Length < headerLength || (bytes.Length - headerLength) % 16 != 0)
            throw new InvalidDataException("MAPI 속성 테이블의 길이가 올바르지 않습니다.");
        var result = new Dictionary<uint, uint>();
        for (var offset = headerLength; offset < bytes.Length; offset += 16)
        {
            var propertyTag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8, 4));
            if (propertyTag != 0)
                result.TryAdd(propertyTag, value);
        }
        return result;
    }

    private static MsgEncoding ResolveEncoding(IReadOnlyDictionary<uint, uint> properties)
    {
        var messageCodePage = properties.GetValueOrDefault(0x3FFD0003u);
        var internetCodePage = properties.GetValueOrDefault(0x3FDE0003u);
        var encoding = GetCodePage(messageCodePage) ?? GetCodePage(internetCodePage);
        return encoding is not null
            ? new MsgEncoding(encoding, true, "")
            : new MsgEncoding(GetCodePage(1252)!, false,
                "ANSI 코드 페이지 확인이 어려워 Windows-1252로 표시했습니다. 판단할 때 원본 바이트를 확인하세요.");
    }

    private static Encoding ResolveInternetEncoding(IReadOnlyDictionary<uint, uint> properties, MsgEncoding messageEncoding) =>
        GetCodePage(properties.GetValueOrDefault(0x3FDE0003u)) ?? messageEncoding.Value;

    private static Encoding? GetCodePage(uint id)
    {
        if (id is 0 or > int.MaxValue || id is 1200 or 1201 or 12000 or 12001 or 65000)
            return null;
        try { return Encoding.GetEncoding((int)id, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
    }

    internal static string ReadHtmlText(byte[] bytes, Encoding fallback)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3).TrimEnd('\0');
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
            return Unicode.GetString(bytes, 2, bytes.Length - 2).TrimEnd('\0');
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
            return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2).TrimEnd('\0');
        return fallback.GetString(bytes).TrimEnd('\0');
    }

    private static uint? StringTag(Storage storage, ushort propertyId)
    {
        var tag = (uint)propertyId << 16;
        var unicode = tag | 0x001F;
        var ansi = tag | 0x001E;
        if (storage.ContainsEntry(StreamName(unicode)))
            return unicode;
        return storage.ContainsEntry(StreamName(ansi)) ? ansi : null;
    }

    private static string? ReadString(Storage storage, ushort propertyId, MsgEncoding encoding, CancellationToken ct,
        int maxBytes = MaximumMetadataBytes)
    {
        if (StringTag(storage, propertyId) is not { } tag)
            return null;
        using var stream = storage.OpenStream(StreamName(tag));
        var bytes = ReadAll(stream, maxBytes, ct);
        return ((tag & 0xFFFF) == 0x001F ? Unicode : encoding.Value).GetString(bytes).TrimEnd('\0');
    }

    private static string? HeaderValue(IReadOnlyList<MailHeader> rows, string name) =>
        rows.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim();

    private static string? DecodeHeader(string? value) =>
        value?.Contains("=?", StringComparison.Ordinal) == true
            ? Rfc2047.DecodeText(Encoding.UTF8.GetBytes(value))
            : value;

    private static string Address(string? name, string? mail)
    {
        if (name is null)
            return mail ?? "";
        return mail is null || name.Equals(mail, StringComparison.OrdinalIgnoreCase) ? name : $"{name} <{mail}>";
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryOpenStream(Storage storage, string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CfbStream? stream)
    {
        if (storage.TryOpenStream(name, out stream))
            return true;
        if (storage.ContainsEntry(name))
            throw new InvalidDataException("CFB 항목이 스트림 형식이 아닙니다.");
        return false;
    }

    private static string StreamName(uint tag) => $"{PropertyStreamPrefix}{tag:X8}";

    private static bool IsMailClass(string messageClass) =>
        messageClass.Equals("IPM.Note", StringComparison.OrdinalIgnoreCase)
        || messageClass.StartsWith("IPM.Note.", StringComparison.OrdinalIgnoreCase)
        || messageClass.StartsWith("REPORT.IPM.Note.", StringComparison.OrdinalIgnoreCase);

    private static byte[] ReadAll(Stream stream, int limit, CancellationToken ct)
    {
        if (stream.Length > limit)
            throw new InvalidDataException("MSG 속성 스트림이 판독 상한을 넘습니다.");
        return ReadBytes(stream, checked((int)stream.Length), ct);
    }

    private static byte[] ReadBytes(Stream stream, int length, CancellationToken ct)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var offset = 0;
        while (offset < length)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(bytes.AsSpan(offset, Math.Min(IOBufferSize, length - offset)));
            if (read == 0)
                throw new EndOfStreamException("MSG 속성 스트림이 중간에서 끊겼습니다.");
            offset += read;
        }
        return bytes;
    }

    private static string Hash(Stream stream, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(IOBufferSize);
        try
        {
            long remaining = stream.Length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var read = stream.Read(buffer.AsSpan(0, (int)Math.Min(remaining, buffer.Length)));
                if (read == 0)
                    throw new EndOfStreamException("MSG 첨부 스트림이 중간에서 끊겼습니다.");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsReadError(Exception error) => error is IOException or InvalidDataException
        or FormatException or ArgumentException or OverflowException or NotSupportedException
        or InvalidOperationException or KeyNotFoundException or FileFormatException;

    private sealed record HeaderData(HeaderEvidenceState State, string? Detail, CompoundStreamLocator? Locator,
        IReadOnlyList<MailHeader> Rows);

    private sealed record MsgEncoding(Encoding Value, bool FromProperty, string Note);
}
