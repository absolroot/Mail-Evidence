using System.Text;
using System.Text.Json;
using MailViewer.Core;

namespace MailViewer.Verification;

internal static partial class Program
{
    private static void AddMsgChecks(List<(string Name, Action Test)> cases, string root, string output)
    {
        var json = File.ReadAllText(Path.Combine(root, "tests", "fixtures", "msg-oracle.json"));
        var independent = JsonSerializer.Deserialize<Dictionary<string, MsgOracle>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("Independent MSG oracle is missing.");

        string Input(string key) => Path.GetFullPath(Path.Combine(root, independent[key].Source));

        cases.Add(("public ANSI MSG search, exact transport header and raw time zone",
            () => TestAnsiMsg(Input("tikaAnsi"), independent["tikaAnsi"])));
        cases.Add(("public Unicode MSG search, original header and by-value PDF stream",
            () => TestUnicodeMsg(Input("poiUnicode"), independent["poiUnicode"])));
        cases.Add(("public MSG duplicate folded headers, missing header and HTML-only search",
            () => TestMsgVariations(Input("poiRepeated"), independent["poiRepeated"],
                Input("poiMissing"), independent["poiMissing"], Input("poiHtml"), independent["poiHtml"])));
        cases.Add(("MSG native file and attachment exports, CSV, CFB manifest and result verification",
            () => TestMsgExport(Input("tikaAnsi"), independent["tikaAnsi"],
                Input("poiUnicode"), independent["poiUnicode"], output)));
        cases.Add(("corrupted MSG isolation and valid MSG retained in read-only session",
            () => TestMalformedMsg(Input("tikaAnsi"), independent["tikaAnsi"], output)));
    }

    private static void TestAnsiMsg(string path, MsgOracle oracle)
    {
        using var session = EvidenceSession.Open([path]);
        var record = ValidateMsgFile(session, path, oracle);
        Equal("Microsoft Outlook Express 6", record.Subject, "ANSI subject");
        AssertMsgHit(session, SearchField.Subject, "Express 6", record.Id);
        AssertMsgHit(session, SearchField.Body, "Synchronisation hors connexion", record.Id);
        Equal("Thu, 5 Apr 2007 09:26:06 -0700", record.DateRaw.Trim(), "Date keeps original header value");
        Equal(new DateTimeOffset(2007, 4, 5, 16, 26, 6, TimeSpan.Zero),
            record.Date?.ToUniversalTime(), "Date decoded UTC");
        var contentType = record.Headers.Single(x => x.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        Require(contentType.Value.Contains("text/html", StringComparison.OrdinalIgnoreCase), "original Content-Type recorded");
        Equal(222L, contentType.Range.Offset, "logical Content-Type stream offset");
        Require(record.Headers.Any(x => x.Name.Equals("X-MimeOLE", StringComparison.OrdinalIgnoreCase)),
            "stored X-MimeOLE is retained");
        Equal(0, record.Attachments.Count, "ANSI MSG attachment count");
    }

    private static void TestUnicodeMsg(string path, MsgOracle oracle)
    {
        using var session = EvidenceSession.Open([path]);
        var record = ValidateMsgFile(session, path, oracle);
        Equal("test email", record.Subject, "Unicode subject");
        AssertMsgHit(session, SearchField.Subject, "test email", record.Id);

        Equal(2, record.Attachments.Count, "PDF and embedded message are separate");
        var pdf = record.Attachments.Single(x => x.CompoundStream?.PropertyTag == 0x37010102);
        Equal("smbprn.00009008.KdcPjl.pdf", pdf.Name, "Unicode long filename");
        AssertMsgHit(session, SearchField.AttachmentName, "KdcPjl.pdf", record.Id);
        ValidateMsgAttachment(session, record, pdf, oracle.Attachment!);
        CheckStoredAttachmentName(session, record, pdf);

        var embedded = record.Attachments.Single(x => x.CompoundStoragePath is not null);
        Require(embedded.CompoundStoragePath!.SequenceEqual(oracle.EmbeddedStorage!), "embedded storage path");
        Require(embedded.CompoundStream is null && embedded.Sha256 is null,
            "embedded object reported as storage without invented standalone-file digest");
    }

    private static void TestMsgVariations(
        string repeatedPath, MsgOracle repeated, string missingPath, MsgOracle missing, string htmlPath, MsgOracle html)
    {
        using var session = EvidenceSession.Open([repeatedPath, missingPath, htmlPath]);
        var folded = ValidateMsgFile(session, repeatedPath, repeated);
        var received = folded.Headers.Where(x => x.Name.Equals("Received", StringComparison.OrdinalIgnoreCase)).ToArray();
        Equal(2, received.Length, "two independently observed Received headers");
        Require(received.Select(x => x.Range.Offset).SequenceEqual([134L, 380L]), "duplicate header order");
        foreach (var row in received)
        {
            var fieldBytes = session.ReadCompoundRange(folded.SourceId, folded.TransportHeaderStream!, row.Range);
            Require(Encoding.ASCII.GetString(fieldBytes).Contains("\r\n\t", StringComparison.Ordinal)
                || Encoding.ASCII.GetString(fieldBytes).Contains("\r\n ", StringComparison.Ordinal),
                "original folded continuation bytes come from the CFB stream");
        }

        var withoutHeader = ValidateMsgFile(session, missingPath, missing);
        Require(withoutHeader.TransportHeaderState == HeaderEvidenceState.Missing
            && withoutHeader.TransportHeaderStream is null && withoutHeader.Headers.Count == 0, "header absent state");
        var htmlOnly = ValidateMsgFile(session, htmlPath, html);
        AssertMsgHit(session, SearchField.Body, "HTML öäü", htmlOnly.Id);
        Require(htmlOnly.BodyParts.Any(x => x.Text.Contains("HTML öäü", StringComparison.Ordinal)),
            "HTML-only MSG yields text without executing HTML");
    }

    private static void TestMsgExport(
        string tikaPath, MsgOracle tika, string poiPath, MsgOracle poi, string output)
    {
        using var session = EvidenceSession.Open([tikaPath, poiPath]);
        var ansi = ValidateMsgFile(session, tikaPath, tika);
        var unicode = ValidateMsgFile(session, poiPath, poi);
        var saved = ExportForTest(session, new ExportRequest(output, ExportKind.MessageBytes,
            ExportScope.SelectedMessages, [ansi.Id, unicode.Id]));
        ValidatePackage(saved);
        Equal(2, saved.Items.Count, "one full MSG file per selected record");
        foreach (var (record, oracle) in new[] { (ansi, tika), (unicode, poi) })
        {
            var item = saved.Items.Single(x => x.RecordId == record.Id);
            Equal("msg-original", item.Nature, "original MSG output nature");
            Require(item.RelativePath.EndsWith("-original.msg", StringComparison.Ordinal), "original MSG file extension");
            Equal(oracle.FileSha256, item.Sha256, "preserves original compound file checksum");
            SameBytes(File.ReadAllBytes(record.SourcePath),
                File.ReadAllBytes(Path.Combine(saved.PackageDirectory, item.RelativePath)), "preserves all MSG container bytes");
        }
        using (var manifest = OpenManifest(saved))
        {
            var evidence = manifest.RootElement.GetProperty("records").EnumerateArray().ToArray();
            Equal(2, evidence.Length, "MSG manifest record count");
            foreach (var (record, oracle) in new[] { (ansi, tika), (unicode, poi) })
            {
                var entry = evidence.Single(x => x.GetProperty("id").GetString() == record.Id);
                Equal("present", entry.GetProperty("transportHeaderState").GetString(), "header evidence state in manifest");
                AssertManifestLocator(entry.GetProperty("transportHeaderStream"), oracle.Header!);
            }
        }

        var attachment = unicode.Attachments.Single(x => x.CompoundStream?.PropertyTag == 0x37010102);
        var attachmentPackage = ExportForTest(session, new ExportRequest(output, ExportKind.Attachments,
            ExportScope.SelectedAttachments, [unicode.Id], [new AttachmentSelection(unicode.Id, attachment.Id)]));
        ValidatePackage(attachmentPackage);
        var outputItem = attachmentPackage.Items.Single();
        Equal("msg-attachment-stream", outputItem.Nature, "CFB by-value stream export nature");
        Equal(default(ByteRange?), outputItem.InputRange, "no fabricated physical offset for the compound stream");
        Equal(poi.Attachment!.Sha256, outputItem.Sha256, "PDF stream and stored attachment checksums");
        SameBytes(Convert.FromBase64String(poi.Attachment.Base64),
            File.ReadAllBytes(Path.Combine(attachmentPackage.PackageDirectory, outputItem.RelativePath)),
            "exported PDF uses exactly the independent CFB stream bytes");
        using (var manifest = OpenManifest(attachmentPackage))
        {
            var item = manifest.RootElement.GetProperty("items").EnumerateArray().Single();
            AssertManifestLocator(item.GetProperty("compoundStream"), poi.Attachment);
        }

        var request = new SearchRequest("Express 6", SearchField.Subject);
        var hits = session.Search(request);
        Equal(1, hits.Hits.Count, "frozen MSG search result count");
        var report = ExportForTest(session, new ExportRequest(output, ExportKind.Csv, ExportScope.SearchResults,
            [ansi.Id], Search: request, SearchIssues: hits.Issues, ExaminedCount: hits.ExaminedCount));
        ValidatePackage(report);
        using var csv = OpenCsv(Path.Combine(report.PackageDirectory, report.Items.Single().RelativePath));
        var columns = csv.ReadFields() ?? throw new InvalidDataException("MSG CSV header missing.");
        var row = csv.ReadFields() ?? throw new InvalidDataException("MSG CSV row missing.");
        Equal(ansi.Id, row[Array.FindIndex(columns, c => NormalizeColumn(c) == "recordid")], "CSV record ID");
        Equal(ansi.Subject, row[Array.FindIndex(columns, c => NormalizeColumn(c) == "subject")], "CSV subject");
        Require(csv.EndOfData, "only the frozen MSG match was written");
        Equal(tika.FileSha256, FileHash(tikaPath), "ANSI source unchanged after exports");
        Equal(poi.FileSha256, FileHash(poiPath), "Unicode source unchanged after exports");
    }

    private static void TestMalformedMsg(string path, MsgOracle oracle, string output)
    {
        var brokenPath = Path.Combine(output, "truncated-compound.msg");
        var original = File.ReadAllBytes(path);
        File.WriteAllBytes(brokenPath, original.AsSpan(0, 512).ToArray());
        using var session = EvidenceSession.Open([brokenPath, path]);
        Equal(1, session.Records.Count, "truncated CFB does not replace the valid message");
        Require(session.Issues.Count > 0, "malformed CFB reported");
        var record = ValidateMsgFile(session, path, oracle);
        AssertMsgHit(session, SearchField.Subject, "Express", record.Id);
        if (OperatingSystem.IsWindows())
        {
            Throws<IOException>(() =>
            {
                using var _ = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }, "retained source handle blocks writes to the original MSG");
        }
    }

    private static MailRecord ValidateMsgFile(EvidenceSession session, string path, MsgOracle oracle)
    {
        var source = session.Sources.Single(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        var record = session.Records.Single(x => x.SourceId == source.Id && x.ContainerMessage is null);
        Equal(MailFormat.Msg, source.Format, "source recognized as MSG");
        Equal(MailFormat.Msg, record.Format, "record recognized as MSG");
        Equal(oracle.FileLength, source.Length, "actual source file length");
        Equal(oracle.FileSha256, source.Sha256, "source SHA-256 compared with olefile oracle input");
        Equal(new ByteRange(0, oracle.FileLength), record.MessageRange, "independent MSG whole-file range");

        var header = oracle.Header;
        if (header is null)
            return record;
        var locator = record.TransportHeaderStream ?? throw new InvalidDataException("Transport header locator missing.");
        Equal(HeaderEvidenceState.Present, record.TransportHeaderState, "transport header physically present");
        ValidateLocator(locator, header);
        var rawBytes = session.ReadCompoundRange(record.SourceId, locator, new ByteRange(0, header.Length));
        var independentlyRead = Convert.FromBase64String(header.Base64);
        SameBytes(independentlyRead, rawBytes, "raw CFB transport header matches independent complete stream");
        Equal(header.Sha256, Hash(rawBytes), "transport header SHA-256");
        Require(record.Headers.Select(x => x.Name).SequenceEqual(header.Fields!.Select(x => x.Name),
            StringComparer.OrdinalIgnoreCase), "forensic header order and names");
        for (var i = 0; i < record.Headers.Count; i++)
        {
            var row = record.Headers[i];
            var expected = header.Fields![i];
            Equal(expected.Offset, row.Range.Offset, $"{row.Name} stream-relative offset");
            Require(row.Range.Length > 0 && row.Range.End <= header.Length, $"{row.Name} lies within stream");
            var actualRow = session.ReadCompoundRange(record.SourceId, locator, row.Range);
            SameBytes(independentlyRead.AsSpan((int)row.Range.Offset, (int)row.Range.Length), actualRow,
                $"raw {row.Name} bytes equal independently read stream slice");
        }
        return record;
    }

    private static void ValidateMsgAttachment(
        EvidenceSession session, MailRecord record, MailAttachment attachment, MsgStreamOracle oracle)
    {
        var locator = attachment.CompoundStream ?? throw new InvalidDataException("Attachment stream locator missing.");
        ValidateLocator(locator, oracle);
        Equal(default(ByteRange?), attachment.EncodedRange, "CFB attachment has logical coordinates only");
        Equal(oracle.Length, attachment.DecodedLength, "PDF 13,539 bytes");
        Equal(oracle.Sha256, attachment.Sha256, "PDF hash from known by-value data");
        var expected = Convert.FromBase64String(oracle.Base64);
        SameBytes(expected, session.ReadAttachment(record.Id, attachment.Id), "by-value storage bytes read exactly");
        SameBytes(expected.AsSpan(4090, 43),
            session.ReadCompoundRange(record.SourceId, locator, new ByteRange(4090, 43)),
            "logical partial read crossing a stream sector boundary");
    }

    private static void ValidateLocator(CompoundStreamLocator actual, MsgStreamOracle expected)
    {
        Require(actual.StoragePath.SequenceEqual(expected.StoragePath), "CFB storage path");
        Equal(expected.StreamName, actual.StreamName, "CFB stream name");
        Equal(expected.PropertyTag, actual.PropertyTag, "MAPI property tag");
        Equal(expected.Length, actual.Length, "original CFB stream length");
    }

    private static void AssertManifestLocator(JsonElement json, MsgStreamOracle expected)
    {
        var actual = JsonSerializer.Deserialize<CompoundStreamLocator>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("CFB locator missing in manifest.");
        ValidateLocator(actual, expected);
    }

    private static void AssertMsgHit(EvidenceSession session, SearchField field, string query, string expectedId)
    {
        var hit = session.Search(new SearchRequest(query, field)).Hits.Single();
        Equal(expectedId, hit.Record.Id, $"MSG {field} search for {query}");
        Require(hit.Matches.Any(x => x.Field == field), "search hit has matching field provenance");
    }

    private static JsonDocument OpenManifest(ExportResult package) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package.PackageDirectory, "manifest.json")));

    private sealed record MsgOracle(
        string Source, long FileLength, string FileSha256, MsgStreamOracle? Header,
        MsgStreamOracle? Attachment, string[]? EmbeddedStorage);

    private sealed record MsgStreamOracle(
        string[] StoragePath, string StreamName, uint PropertyTag, long Length, string Sha256,
        string Base64, MsgFieldOracle[]? Fields);

    private sealed record MsgFieldOracle(string Name, long Offset);
}
