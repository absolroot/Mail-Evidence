using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;
using MimeContentType = MimeKit.ContentType;
using ParserFormat = MimeKit.MimeFormat;

namespace MailViewer.Core;

internal static partial class MimeMessageReader
{
    internal const long MaximumMessageLength = 128L * 1024 * 1024;
    private const int MaximumTextPartLength = 4 * 1024 * 1024;
    private const int MaximumIndexedText = 8 * 1024 * 1024;

    public static MailRecord? Parse(SourceInfo source, SafeFileHandle handle, ByteRange messageRange,
        ByteRange? envelope, ByteRange? separator, List<ReadIssue> issues, CancellationToken ct)
    {
        var recordId = $"{source.Id}:{messageRange.Offset.ToString(CultureInfo.InvariantCulture)}";
        if (messageRange.Length > MaximumMessageLength)
        {
            issues.Add(new ReadIssue(source.Id, "message-too-large", "128 MiB를 초과해 메시지를 건너뛰었습니다.", messageRange));
            return null;
        }

        using var stream = new SourceRangeStream(handle, messageRange);
        var parser = new LocationParser(stream);
        using var message = parser.ParseMessage(ct);
        if (parser.HeaderLength <= 0 || parser.HeaderLength > messageRange.Length)
        {
            issues.Add(new ReadIssue(source.Id, "missing-header-separator", "헤더와 본문의 경계를 확인할 수 없습니다.", messageRange));
            return null;
        }

        var rawHeaders = message.Headers.Concat(message.Body?.Headers ?? [])
            .Where(header => header.Offset is >= 0)
            .OrderBy(header => header.Offset)
            .ToArray();
        if (rawHeaders.Length == 0)
        {
            issues.Add(new ReadIssue(source.Id, "missing-headers", "확인할 수 있는 메시지 헤더가 없습니다.", messageRange));
            return null;
        }

        var headers = new List<MailHeader>(rawHeaders.Length);
        foreach (var header in rawHeaders)
        {
            var begin = header.Offset!.Value;
            var end = begin + header.RawField.Length + 1 + header.RawValue.Length;
            if (begin >= parser.HeaderLength || end < begin || end > parser.HeaderLength)
                continue;

            var raw = Encoding.UTF8.GetString(header.RawValue).TrimEnd('\r', '\n');
            headers.Add(new MailHeader(header.Field, raw, new ByteRange(messageRange.Offset + begin, end - begin)));
        }

        var dateRaw = headers.FirstOrDefault(header => header.Name.Equals("Date", StringComparison.OrdinalIgnoreCase))?
            .Value.TrimStart(' ', '\t') ?? "";
        var date = ParseTransportDate(headers, dateRaw);
        var messageId = headers.FirstOrDefault(header => header.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase))?
            .Value.Trim() ?? "";

        var bodies = new List<MailBodyPart>();
        var attachments = new List<MailAttachment>();
        var indexedText = 0;
        if (message.Body is not null)
            ReadPart(message.Body, "1");

        foreach (var violation in parser.Violations)
        {
            if (!IsStructuralViolation(violation.Violation))
                continue;

            var offset = messageRange.Offset + Math.Clamp(violation.StreamOffset, 0, messageRange.Length);
            var field = violation.Violation is MimeComplianceViolation.InvalidHeader or MimeComplianceViolation.IncompleteHeader
                ? (SearchField?)null
                : SearchField.Body;
            issues.Add(new ReadIssue(source.Id, $"mime-{violation.Violation}",
                "메시지 구조 중 일부를 규격에 맞게 판독할 수 없습니다.", new ByteRange(offset, 0), recordId, field));
        }

        return new MailRecord(recordId, source.Id, source.Path, source.Format, messageRange,
            new ByteRange(messageRange.Offset, parser.HeaderLength), envelope, separator,
            message.Subject ?? "", message.From.ToString(), message.To.ToString(), message.Cc.ToString(),
            message.Bcc.ToString(), messageId, dateRaw, date, headers.AsReadOnly(), bodies.AsReadOnly(), attachments.AsReadOnly());

        void ReadPart(MimeEntity entity, string partPath, bool incompleteParent = false)
        {
            ct.ThrowIfCancellationRequested();
            var type = entity.ContentType.MimeType;
            var name = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
            if (entity.IsAttachment || entity is MessagePart
                || !string.IsNullOrWhiteSpace(name)
                || entity is MimePart and not TextPart && !string.IsNullOrEmpty(entity.ContentId))
            {
                var attachmentName = string.IsNullOrWhiteSpace(name)
                    ? entity is MessagePart ? $"message-{partPath}.eml" : $"attachment-{partPath}"
                    : name;
                var encodingLabel = entity.Headers["Content-Transfer-Encoding"]?.Trim().ToLowerInvariant() ?? "7bit";
                var hasRange = parser.Payloads.TryGetValue(entity.ContentType, out var relative)
                    && relative.Offset >= 0 && relative.End <= messageRange.Length;
                ByteRange? encoded = hasRange ? new ByteRange(messageRange.Offset + relative.Offset, relative.Length) : null;
                byte[]? bytes = null;
                string? issue = null;
                if (incompleteParent)
                    issue = "상위 MIME 구간이 끝나지 않아 첨부파일의 전체 바이트를 확정할 수 없습니다.";
                else if (encoded is not { } location)
                    issue = "첨부파일의 원본 데이터 범위를 확인할 수 없습니다.";
                else if (parser.InvalidTransfer.Contains(entity.ContentType))
                    issue = "첨부파일의 전송 인코딩이 손상되어 해시와 추출 결과를 확정할 수 없습니다.";
                else
                {
                    try
                    {
                        bytes = DecodeAttachment(handle, source.Format, location, encodingLabel, ct);
                    }
                    catch (Exception e) when (e is FormatException or NotSupportedException)
                    {
                        issue = e.Message;
                    }
                }

                var nameField = entity.ContentDisposition?.FileName is not null ? "Content-Disposition" : "Content-Type";
                var nameHeader = entity.Headers.FirstOrDefault(header =>
                    header.Field.Equals(nameField, StringComparison.OrdinalIgnoreCase));
                EvidenceLocation? nameLocation = null;
                if (!string.IsNullOrWhiteSpace(name) && nameHeader?.Offset is >= 0)
                {
                    long offset = nameHeader.Offset.Value;
                    long length = nameHeader.RawField.Length + 1L + nameHeader.RawValue.Length;
                    if (offset <= messageRange.Length - length)
                        nameLocation = new(new ByteRange(messageRange.Offset + offset, length));
                }
                using var decodedData = bytes is null ? null : new MemoryStream(bytes, writable: false);
                var analysis = InvestigationArtifacts.InspectAttachment(attachmentName, nameLocation, nameField, decodedData);
                attachments.Add(new MailAttachment($"{recordId}:{partPath}", partPath, attachmentName, type, encodingLabel,
                    encoded, bytes?.LongLength, bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes)), issue)
                    { Analysis = analysis, ContentId = (entity as MimePart)?.ContentId });
                if (issue is not null)
                    issues.Add(new ReadIssue(source.Id, "attachment-data", issue, encoded, recordId));
                return;
            }

            if (entity is Multipart multipart)
            {
                var incomplete = parser.IncompleteMultiparts.Contains(entity.ContentType);
                if (incomplete)
                {
                    issues.Add(new ReadIssue(source.Id, "mime-incomplete-multipart",
                        $"MIME 구간 {partPath}의 닫는 경계가 없어서 본문과 첨부파일이 일부 누락되었을 수 있습니다.",
                        messageRange, recordId, SearchField.Body));
                }
                if (type.Equals("multipart/encrypted", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ReadIssue(source.Id, "body-encrypted",
                        "암호화된 본문은 이번 분석의 본문 검색 범위에 포함되지 않습니다.", messageRange, recordId, SearchField.Body));
                    return;
                }
                for (var index = 0; index < multipart.Count; index++)
                    ReadPart(multipart[index], $"{partPath}.{index + 1}", incompleteParent || incomplete);
                return;
            }

            if (entity is not TextPart text || (!text.IsPlain && !text.IsHtml))
                return;

            if (!parser.Payloads.TryGetValue(entity.ContentType, out var data)
                || data.Offset < 0 || data.End > messageRange.Length)
            {
                issues.Add(new ReadIssue(source.Id, "body-range", $"본문 파트 {partPath}의 원본 범위를 확인할 수 없습니다.",
                    messageRange, recordId, SearchField.Body));
                return;
            }

            if (data.Length > MaximumTextPartLength || indexedText >= MaximumIndexedText)
            {
                issues.Add(new ReadIssue(source.Id, "body-limit", $"본문 파트 {partPath}가 색인 크기 한도를 초과했습니다.",
                    new ByteRange(messageRange.Offset + data.Offset, data.Length), recordId, SearchField.Body));
                return;
            }

            string bodyEncodingLabel = text.Headers["Content-Transfer-Encoding"]?.Trim().ToLowerInvariant() ?? "7bit";
            if (parser.InvalidTransfer.Contains(entity.ContentType)
                || !TryTransferEncoding(bodyEncodingLabel, out _))
            {
                issues.Add(new ReadIssue(source.Id, "body-encoding", $"본문 파트 {partPath}의 전송 인코딩을 확인할 수 없습니다.",
                    new ByteRange(messageRange.Offset + data.Offset, data.Length), recordId, SearchField.Body));
                return;
            }

            try
            {
                string value;
                if (source.Format == MailFormat.Mbox && text.Content is { } content)
                {
                    var unescaped = RemoveMboxEscapes(ReadBytes(handle, new ByteRange(messageRange.Offset + data.Offset, data.Length), ct));
                    using var memory = new MemoryStream(unescaped, writable: false);
                    using var decoded = new MimeContent(memory, content.Encoding);
                    text.Content = decoded;
                    try { value = text.Text; }
                    finally { text.Content = content; }
                }
                else
                {
                    value = text.Text;
                }

                var sourceLocation = new EvidenceLocation(new ByteRange(messageRange.Offset + data.Offset, data.Length));
                var (links, limited) = incompleteParent ? (Array.Empty<ObservedLink>(), false) :
                    InvestigationArtifacts.ReadLinks(value, type, partPath, sourceLocation, ct);
                if (limited)
                    issues.Add(new ReadIssue(source.Id, "link-observation-limit",
                        $"본문 파트 {partPath}의 링크 수가 판독 한도를 초과했습니다.", sourceLocation.Range, recordId, SearchField.Body));
                if (text.IsHtml)
                    value = new HtmlToText().Convert(value);
                if (value.Length > MaximumIndexedText - indexedText)
                {
                    issues.Add(new ReadIssue(source.Id, "body-limit", $"본문 파트 {partPath}가 색인 크기 한도를 초과했습니다.",
                        new ByteRange(messageRange.Offset + data.Offset, data.Length), recordId, SearchField.Body));
                    return;
                }
                indexedText += value.Length;
                bodies.Add(new MailBodyPart(partPath, type, value)
                {
                    Links = links,
                    HtmlSource = text.IsHtml ? new(sourceLocation, bodyEncodingLabel, text.ContentType.Charset) : null
                });
            }
            catch (Exception e) when (e is FormatException or NotSupportedException or ArgumentException)
            {
                issues.Add(new ReadIssue(source.Id, "body-decode", $"본문 파트 {partPath}를 텍스트로 읽을 수 없습니다.",
                    new ByteRange(messageRange.Offset + data.Offset, data.Length), recordId, SearchField.Body));
            }
        }
    }

    internal static string ReadHtmlBody(SafeFileHandle handle, MailFormat format, HtmlBodySource source, CancellationToken ct)
    {
        byte[] decoded = DecodeAttachment(handle, format, source.Location.Range, source.TransferEncoding!, ct);
        using var stream = new MemoryStream(decoded, writable: false);
        using var content = new MimeContent(stream, ContentEncoding.Binary);
        var part = new TextPart("html") { Content = content };
        if (!string.IsNullOrWhiteSpace(source.Charset)) part.ContentType.Charset = source.Charset;
        return part.Text;
    }

    public static byte[] DecodeAttachment(
        SafeFileHandle handle, MailFormat format, ByteRange encoded, string label, CancellationToken ct)
    {
        if (!TryTransferEncoding(label, out var encoding))
            throw new NotSupportedException("지원하지 않는 첨부파일 전송 인코딩입니다.");

        using Stream input = format == MailFormat.Mbox && encoding != ContentEncoding.Base64
            ? new MemoryStream(RemoveMboxEscapes(ReadBytes(handle, encoded, ct)), writable: false)
            : new SourceRangeStream(handle, encoded);
        using var content = new MimeContent(input, encoding);
        using var output = new MemoryStream();
        content.DecodeTo(output, ct);
        return output.ToArray();
    }

    private static bool TryTransferEncoding(string label, out ContentEncoding encoding)
    {
        ContentEncoding? match = label switch
        {
            "7bit" => ContentEncoding.SevenBit,
            "8bit" => ContentEncoding.EightBit,
            "binary" => ContentEncoding.Binary,
            "base64" => ContentEncoding.Base64,
            "quoted-printable" => ContentEncoding.QuotedPrintable,
            "uuencode" or "x-uuencode" => ContentEncoding.UUEncode,
            _ => null,
        };
        encoding = match.GetValueOrDefault();
        return match.HasValue;
    }

    internal static byte[] ReadBytes(SafeFileHandle handle, ByteRange range, CancellationToken ct)
    {
        if (range.Length > MaximumMessageLength)
            throw new ArgumentOutOfRangeException(nameof(range), "한 번에 읽는 원본 범위는 128 MiB 이하여야 합니다.");
        using var input = new SourceRangeStream(handle, range);
        var bytes = new byte[checked((int)range.Length)];
        var read = 0;
        while (read < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            read += input.Read(bytes, read, Math.Min(bytes.Length - read, 1024 * 1024));
        }
        return bytes;
    }

    private static byte[] RemoveMboxEscapes(byte[] bytes)
    {
        // A Unix mboxrd escape is a property of the wire line, before MIME transfer decoding.
        var read = 0;
        var write = 0;
        while (read < bytes.Length)
        {
            var line = bytes.AsSpan(read);
            var end = line.IndexOf((byte)'\n');
            var length = end < 0 ? line.Length : end + 1;
            var prefix = 0;
            while (prefix < length && line[prefix] == '>')
                prefix++;
            var skip = prefix > 0 && line[prefix..length].StartsWith("From "u8) ? 1 : 0;
            line[skip..length].CopyTo(bytes.AsSpan(write));
            read += length;
            write += length - skip;
        }
        return write == bytes.Length ? bytes : bytes.AsSpan(0, write).ToArray();
    }

    private static bool IsStructuralViolation(MimeComplianceViolation code) => code is
        MimeComplianceViolation.InvalidHeader or MimeComplianceViolation.IncompleteHeader
        or MimeComplianceViolation.InvalidContentType or MimeComplianceViolation.MissingMultipartBoundaryParameter
        or MimeComplianceViolation.InvalidMultipartBoundaryParameter or MimeComplianceViolation.MissingMultipartBoundary
        or MimeComplianceViolation.MimeDepthLimitExceeded;

    private static bool IsTransferViolation(MimeComplianceViolation code) => code is
        MimeComplianceViolation.InvalidBase64Character or MimeComplianceViolation.InvalidBase64Padding
        or MimeComplianceViolation.IncompleteBase64Quantum or MimeComplianceViolation.Base64CharactersAfterPadding
        or MimeComplianceViolation.InvalidQuotedPrintableEncoding or MimeComplianceViolation.InvalidUUEncodedContent
        or MimeComplianceViolation.InvalidQuotedPrintableSoftBreak;

    [GeneratedRegex(@"(?:[+-][0-9]{4}|\b(?:UTC|GMT|UT|[ECMP][SD]T|JST|KST|Z))\s*(?:\([^)]*\)\s*)*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DateZonePattern();

    internal static DateTimeOffset? ParseTransportDate(IReadOnlyList<MailHeader> headers, string dateRaw)
    {
        var uniqueDate = headers.Count(header => header.Name.Equals("Date", StringComparison.OrdinalIgnoreCase)) == 1;
        return uniqueDate && DateZonePattern().IsMatch(dateRaw) && DateUtils.TryParse(dateRaw, out var dateValue)
            ? dateValue
            : null;
    }

    private sealed class LocationParser : MimeParser, IMimeComplianceLogger
    {
        private ByteRange? pendingContent;
        private MimeContentType? activePart;
        private readonly Stack<bool> multipartClosed = new();
        private bool sawHeaderSeparator;
        public long HeaderLength { get; private set; }
        public Dictionary<MimeContentType, ByteRange> Payloads { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<MimeContentType> InvalidTransfer { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<MimeContentType> IncompleteMultiparts { get; } = new(ReferenceEqualityComparer.Instance);
        public List<MimeComplianceIssue> Violations { get; } = [];

        public LocationParser(Stream source) : base(CreateOptions(), source, ParserFormat.Entity, MimeContentStorage.Source)
        {
            ComplianceLogger = this;
            ComplianceOptions = new MimeComplianceOptions
            {
                Context = MimeComplianceContext.Storage,
                EnabledValidators = MimeComplianceValidators.Base64 | MimeComplianceValidators.QuotedPrintable
                    | MimeComplianceValidators.UUEncode,
            };
        }

        private static ParserOptions CreateOptions()
        {
            var options = ParserOptions.Default.Clone();
            options.MaxMimeDepth = 32;
            options.MaxHeaderLength = 128 * 1024;
            return options;
        }

        protected override void OnBodySeparator(long beginOffset, int lineNumber, long endOffset, CancellationToken ct)
        {
            base.OnBodySeparator(beginOffset, lineNumber, endOffset, ct);
            if (!sawHeaderSeparator)
            {
                HeaderLength = endOffset;
                sawHeaderSeparator = true;
            }
        }

        protected override void OnMimePartBegin(MimeContentType contentType, long beginOffset, int beginLineNumber, CancellationToken ct)
        {
            pendingContent = null;
            activePart = contentType;
            base.OnMimePartBegin(contentType, beginOffset, beginLineNumber, ct);
        }

        protected override void OnMimePartContentEnd(long beginOffset, int beginLineNumber, long endOffset,
            int lines, NewLineFormat? newLineFormat, CancellationToken ct)
        {
            pendingContent = new ByteRange(beginOffset, Math.Max(0, endOffset - beginOffset));
            base.OnMimePartContentEnd(beginOffset, beginLineNumber, endOffset, lines, newLineFormat, ct);
        }

        protected override void OnMimePartEnd(MimeContentType contentType, long beginOffset, int beginLineNumber,
            long headersEndOffset, long endOffset, int lines, CancellationToken ct)
        {
            Payloads[contentType] = pendingContent ?? new ByteRange(headersEndOffset, 0);
            activePart = null;
            base.OnMimePartEnd(contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, ct);
        }

        protected override void OnMessagePartEnd(MimeContentType contentType, long beginOffset, int beginLineNumber,
            long headersEndOffset, long endOffset, int lines, CancellationToken ct)
        {
            Payloads[contentType] = new ByteRange(headersEndOffset, Math.Max(0, endOffset - headersEndOffset));
            base.OnMessagePartEnd(contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, ct);
        }

        protected override void OnMultipartBegin(MimeContentType contentType, long beginOffset, int beginLineNumber, CancellationToken ct)
        {
            multipartClosed.Push(false);
            base.OnMultipartBegin(contentType, beginOffset, beginLineNumber, ct);
        }

        protected override void OnMultipartEndBoundaryEnd(long beginOffset, int lineNumber, long endOffset, CancellationToken ct)
        {
            if (multipartClosed.TryPop(out _))
                multipartClosed.Push(true);
            base.OnMultipartEndBoundaryEnd(beginOffset, lineNumber, endOffset, ct);
        }

        protected override void OnMultipartEnd(MimeContentType contentType, long beginOffset, int beginLineNumber,
            long headersEndOffset, long endOffset, int lines, CancellationToken ct)
        {
            if (multipartClosed.TryPop(out var closed) && !closed)
                IncompleteMultiparts.Add(contentType);
            base.OnMultipartEnd(contentType, beginOffset, beginLineNumber, headersEndOffset, endOffset, lines, ct);
        }

        public void Log(in MimeComplianceIssue issue)
        {
            if (IsTransferViolation(issue.Violation) && activePart is not null)
                InvalidTransfer.Add(activePart);
            else if (IsStructuralViolation(issue.Violation) && Violations.Count < 128)
                Violations.Add(issue);
        }
    }
}
