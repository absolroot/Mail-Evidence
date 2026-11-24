using System.Text.Json;
using MailViewer.Core;

namespace MailViewer.Verification;

internal static partial class Program
{
    private static void AddPstChecks(List<(string Name, Action Test)> cases, string root, string output)
    {
        var fixtures = Path.Combine(root, "tests", "fixtures");
        var oracle = JsonSerializer.Deserialize<PstOracle>(
            File.ReadAllText(Path.Combine(fixtures, "pst-oracle.json")), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException("Independent PST oracle is missing.");
        var tikaPath = Path.Combine(root, "tests", "fixtures", "external", oracle.Tika.File);
        var attachmentPath = Path.Combine(fixtures, "pst", oracle.Attachment.File);

        cases.Add(("public Tika PST folders, duplicate ordered headers, exact properties and search",
            () => TestPstHeaders(tikaPath, oracle.Tika)));
        cases.Add(("public libpff PST folder scope and original attachment property bytes",
            () => TestPstAttachmentScopes(tikaPath, attachmentPath, oracle)));
        cases.Add(("PST evidence and CSV export preserve scope, NIDs and original property bytes",
            () => TestPstExport(tikaPath, attachmentPath, oracle, output)));
        cases.Add(("truncated PST stays isolated and verified original remains read-only",
            () => TestMalformedPst(tikaPath, oracle.Tika, output)));
    }

    private static void TestPstHeaders(string path, PstSourceOracle oracle)
    {
        using var session = EvidenceSession.Open([path]);
        var source = ValidatePstSource(session, path, oracle);
        var records = PstRecords(session, source.Id, oracle);
        var expectedFolder = oracle.Folders.Single(folder =>
            folder.Name == "Début du fichier de données Outlook");
        var folder = session.PstFolders.Single(x => x.SourceId == source.Id && x.NodeId == expectedFolder.Nid);
        var expectedChild = oracle.Folders.Single(x => x.Name == "Éléments supprimés");
        var child = session.PstFolders.Single(x => x.SourceId == source.Id && x.NodeId == expectedChild.Nid);
        Equal(folder.NodeId, child.ParentNodeId, "Tika describes the actual folder hierarchy");

        var subjectHits = session.Search(new SearchRequest("Feature Generators", SearchField.Subject,
            SourceId: source.Id, PstFolderNodeId: folder.NodeId));
        Require(subjectHits.Hits.Select(x => x.Record.PstMessage!.MessageNodeId).Order()
            .SequenceEqual([2097188u, 2097252u]), "scoped PST subject hits");
        Equal(records[2097220].Id, session.Search(new SearchRequest("java.lang.VerifyError", SearchField.Body,
            SourceId: source.Id, PstFolderNodeId: folder.NodeId)).Hits.Single().Record.Id, "stored body search");

        var received = 0;
        foreach (var expected in oracle.Messages)
        {
            var record = records[expected.MessageNid];
            var header = expected.Header ?? throw new InvalidDataException("Tika mail has no independent header.");
            var location = record.TransportHeaderProperty ??
                throw new InvalidDataException("Tika mail has no recorded property locator.");
            Equal(HeaderEvidenceState.Present, record.TransportHeaderState, "stored transport header exists");
            Equal(header.Tag, location.PropertyTag, "native Unicode header type and MAPI property");
            Equal(header.Length, location.Length, "original property length");
            Equal(record.PstMessage, location.Message, "header belongs to the same message and folder");
            var bytes = Convert.FromBase64String(header.Base64);
            SameBytes(bytes, session.ReadPstPropertyRange(record.SourceId, location, new ByteRange(0, header.Length)),
                "independent raw transport property");
            Equal(header.Sha256, Hash(bytes), "independent transport property checksum");
            var rawFields = header.Fields ?? throw new InvalidDataException("Independent field positions missing.");
            Require(record.Headers.Select(x => x.Name).SequenceEqual(rawFields.Select(x => x.Name),
                StringComparer.OrdinalIgnoreCase), "stored header field order and duplicates");
            for (var i = 0; i < record.Headers.Count; i++)
            {
                var row = record.Headers[i];
                Equal(rawFields[i].Offset, row.Range.Offset, $"relative position of {row.Name}");
                Require(row.Range.Length > 0 && row.Range.End <= header.Length, "header row lies within original property");
                SameBytes(bytes.AsSpan((int)row.Range.Offset, (int)row.Range.Length),
                    session.ReadPstPropertyRange(record.SourceId, location, row.Range), $"original bytes of {row.Name}");
            }
            var count = record.Headers.Count(x => x.Name.Equals("Received", StringComparison.OrdinalIgnoreCase));
            Equal(expected.ReceivedCount, count, "duplicate Received count per source message");
            received += count;
        }
        Equal(50, received, "six original messages retain a combined fifty Received fields");
        Equal(oracle.Sha256, FileHash(path), "Tika PST stays unchanged");
    }

    private static void TestPstAttachmentScopes(string tikaPath, string attachmentPath, PstOracle oracle)
    {
        using var session = EvidenceSession.Open([tikaPath, attachmentPath]);
        var tika = ValidatePstSource(session, tikaPath, oracle.Tika);
        var source = ValidatePstSource(session, attachmentPath, oracle.Attachment);
        var records = PstRecords(session, source.Id, oracle.Attachment);
        var first = oracle.Attachment.Messages[0];
        var second = oracle.Attachment.Messages[1];
        Equal(first.Subject, second.Subject, "fixture has ambiguous equal subjects across folders");
        var inboxQuery = new SearchRequest("1.jpg", SearchField.AttachmentName, SourceId: source.Id,
            PstFolderNodeId: first.FolderNid);
        Equal(records[first.MessageNid].Id, session.Search(inboxQuery).Hits.Single().Record.Id, "Inbox scoped attachment hit");
        Equal(records[second.MessageNid].Id,
            session.Search(inboxQuery with { PstFolderNodeId = second.FolderNid }).Hits.Single().Record.Id,
            "Recovered_Group2 scoped attachment hit");
        var containerNid = oracle.Attachment.Folders.Single(folder => folder.Name == "Top of Personal Folders").Nid;
        var container = inboxQuery with { PstFolderNodeId = containerNid };
        Equal(0, session.Search(container).Hits.Count, "a container query excludes descendants by default");
        Equal(2, session.Search(container with { IncludePstSubfolders = true }).Hits.Count, "explicit descendants include both folders");
        var tikaFolder = oracle.Tika.Messages[0].FolderNid;
        Equal(containerNid, tikaFolder, "fixture has overlapping numeric folder NIDs in separate sources");
        Equal(0, session.Search(inboxQuery with
            { SourceId = tika.Id, PstFolderNodeId = tikaFolder, IncludePstSubfolders = true }).Hits.Count,
            "a numeric folder NID can never cross PST source identity");
        Equal(2, session.Search(new SearchRequest(first.Subject, SearchField.Subject, SourceId: source.Id)).Hits.Count,
            "same subject across folders yields both records when explicitly searching the whole source");

        foreach (var message in oracle.Attachment.Messages)
        {
            var record = records[message.MessageNid];
            Equal(HeaderEvidenceState.Missing, record.TransportHeaderState, "no header is invented for mailbox welcome mail");
            Equal(message.Attachments.Count, record.Attachments.Count, "source attachment table count");
            foreach (var expected in message.Attachments)
            {
                var current = record.Attachments.Single(a => a.Name == expected.Name);
                var data = expected.Property ?? throw new InvalidDataException("Independent attachment data missing.");
                var location = current.PstProperty ?? throw new InvalidDataException("Attachment has no PST property locator.");
                Equal(record.PstMessage, location.Message, "attachment property is scoped to its source message");
                Equal(expected.AttachmentNid, location.AttachmentNodeId, "attachment subnode NID");
                Equal(expected.Index, location.AttachmentIndex, "attachment table index");
                Equal(data.Tag, location.PropertyTag, "PidTagAttachDataBinary");
                Equal(data.Length, location.Length, "native attachment property byte length");
                Equal(data.Sha256, current.Sha256, "independent attachment SHA-256");
                Require(current.EncodedRange is null && current.CompoundStream is null,
                    "PST attachment has no fabricated physical offset or compound stream");
                var bytes = Convert.FromBase64String(data.Base64);
                SameBytes(bytes, session.ReadAttachment(record.Id, current.Id), "exact original attachment data");
                CheckStoredAttachmentName(session, record, current);
                SameBytes(bytes.AsSpan(500, 43),
                    session.ReadPstPropertyRange(source.Id, location, new ByteRange(500, 43)),
                    "property-relative attachment hex range across byte 512");
            }
        }
        Equal(oracle.Attachment.Sha256, FileHash(attachmentPath), "attachment PST stays unchanged");
    }

    private static void TestPstExport(string tikaPath, string attachmentPath, PstOracle oracle, string output)
    {
        using var session = EvidenceSession.Open([tikaPath, attachmentPath]);
        var tika = ValidatePstSource(session, tikaPath, oracle.Tika);
        var attachments = ValidatePstSource(session, attachmentPath, oracle.Attachment);
        var expected = oracle.Tika.Messages[0];
        var target = session.Records.Single(x => x.SourceId == tika.Id && x.PstMessage?.MessageNodeId == expected.MessageNid);
        var query = new SearchRequest(target.Subject, SearchField.Subject, SourceId: tika.Id, PstFolderNodeId: expected.FolderNid);
        Equal(target.Id, session.Search(query).Hits.Single().Record.Id, "one exact frozen Tika search for export");

        Throws<InvalidOperationException>(() => ExportService.Export(session,
            new ExportRequest(output, ExportKind.MessageBytes, ExportScope.SelectedMessages, [target.Id])),
            "individual PST mail cannot be exported as a fabricated original physical message");
        var saved = ExportForTest(session, new ExportRequest(output, ExportKind.PstEvidence, ExportScope.SearchResults,
            [target.Id], Search: query));
        ValidatePackage(saved);
        var property = saved.Items.Single(x => x.Nature == "pst-transport-header-property");
        var header = expected.Header ?? throw new InvalidDataException("Independent expected header missing.");
        Equal(target.TransportHeaderProperty, property.PstProperty, "original logical property locator in header export");
        Equal(default(ByteRange?), property.InputRange, "header evidence does not invent a physical byte range");
        SameBytes(Convert.FromBase64String(header.Base64),
            File.ReadAllBytes(Path.Combine(saved.PackageDirectory, property.RelativePath)), "exported original transport property");
        using (var manifest = OpenManifest(saved))
        {
            var record = manifest.RootElement.GetProperty("records").EnumerateArray().Single();
            Equal("pst-property-offset", record.GetProperty("headerRangeKind").GetString(), "manifest header offset kind");
            Equal(expected.MessageNid, record.GetProperty("pstMessage").GetProperty("messageNodeId").GetUInt32(),
                "manifest original message NID");
            Equal(expected.FolderNid, manifest.RootElement.GetProperty("search").GetProperty("pstFolderNodeId").GetUInt32(),
                "frozen folder scope was retained in manifest");
        }

        var report = ExportForTest(session, new ExportRequest(output, ExportKind.Csv, ExportScope.SearchResults,
            [target.Id], Search: query));
        ValidatePackage(report);
        using (var csv = OpenCsv(Path.Combine(report.PackageDirectory, report.Items.Single().RelativePath)))
        {
            var columns = csv.ReadFields() ?? throw new InvalidDataException("PST CSV header missing.");
            var row = csv.ReadFields() ?? throw new InvalidDataException("PST CSV row missing.");
            Equal(target.Id, row[Array.FindIndex(columns, c => NormalizeColumn(c) == "recordid")], "CSV scoped record ID");
            Require(csv.EndOfData, "CSV excludes messages outside the frozen search");
        }
        var first = oracle.Attachment.Messages[0];
        var attachmentMail = session.Records.Single(x =>
            x.SourceId == attachments.Id && x.PstMessage?.MessageNodeId == first.MessageNid);
        var savedWithoutHeader = ExportForTest(session, new ExportRequest(output, ExportKind.PstEvidence,
            ExportScope.SelectedMessages, [attachmentMail.Id]));
        ValidatePackage(savedWithoutHeader);
        Equal("pst-message-metadata", savedWithoutHeader.Items.Single().Nature, "no invented header for missing property");
        var attachment = attachmentMail.Attachments.Single(x => x.Name == "1.jpg");
        var savedAttachment = ExportForTest(session, new ExportRequest(output, ExportKind.Attachments,
            ExportScope.SelectedAttachments, [attachmentMail.Id], [new AttachmentSelection(attachmentMail.Id, attachment.Id)]));
        ValidatePackage(savedAttachment);
        var item = savedAttachment.Items.Single();
        Equal("pst-attachment-property", item.Nature, "PST attachment preserves a native property");
        Equal(attachment.PstProperty, item.PstProperty, "attachment property locator in export");
        Equal(default(ByteRange?), item.InputRange, "attachment exported without fabricated physical offsets");
        var data = first.Attachments.Single(a => a.Name == attachment.Name).Property!;
        Equal(data.Sha256, item.Sha256, "saved attachment checksum independent oracle");
        var outputPath = Path.Combine(savedAttachment.PackageDirectory, item.RelativePath);
        var bytes = File.ReadAllBytes(outputPath);
        SameBytes(Convert.FromBase64String(data.Base64), bytes, "saved PST attachment bytes");
        bytes[^1] ^= 1;
        File.WriteAllBytes(outputPath, bytes);
        Require(!ExportService.Verify(savedAttachment.PackageDirectory).Success, "result verification detects a same-length change");
        Equal(oracle.Tika.Sha256, FileHash(tikaPath), "Tika PST stays unchanged after exports");
        Equal(oracle.Attachment.Sha256, FileHash(attachmentPath), "attachment PST stays unchanged after exports");
    }

    private static void TestMalformedPst(string path, PstSourceOracle oracle, string output)
    {
        var broken = Path.Combine(output, "truncated-source.pst");
        File.WriteAllBytes(broken, File.ReadAllBytes(path)[..1024]);
        using var session = EvidenceSession.Open([broken, path]);
        var source = ValidatePstSource(session, path, oracle);
        Equal(oracle.Messages.Count, session.Records.Count, "malformed PST does not contaminate independent records");
        Equal(source.Id, session.Records.Select(x => x.SourceId).Distinct().Single(), "healthy source retained");
        Require(session.Issues.Count > 0, "truncated PST reported");
        if (OperatingSystem.IsWindows())
        {
            Throws<IOException>(() =>
            {
                using var _ = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }, "retained source handle blocks writes to the verified PST");
        }
    }

    private static SourceInfo ValidatePstSource(EvidenceSession session, string path, PstSourceOracle oracle)
    {
        var source = session.Sources.Single(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        Equal(MailFormat.Pst, source.Format, "PST source format");
        Equal(oracle.Length, source.Length, "PST byte length");
        Equal(oracle.Sha256, source.Sha256, "PST SHA-256 against independent oracle");
        return source;
    }

    private static Dictionary<uint, MailRecord> PstRecords(EvidenceSession session, string source, PstSourceOracle oracle)
    {
        var map = session.Records.Where(x => x.SourceId == source)
            .ToDictionary(x => x.PstMessage?.MessageNodeId ?? throw new InvalidDataException("PST message NID missing."));
        Equal(oracle.Messages.Count, map.Count, "expected message population");
        foreach (var message in oracle.Messages)
        {
            var record = map[message.MessageNid];
            Equal(MailFormat.Pst, record.Format, "PST record format");
            Equal(message.Subject, record.Subject, "stored message subject");
            Equal(message.FolderNid, record.PstMessage!.FolderNodeId, "parent folder NID");
        }
        return map;
    }

    private sealed record PstOracle(PstSourceOracle Tika, PstSourceOracle Attachment);
    private sealed record PstSourceOracle(string File, long Length, string Sha256,
        IReadOnlyList<PstFolderOracle> Folders, IReadOnlyList<PstMessageOracle> Messages);
    private sealed record PstFolderOracle(uint Nid, string Name);
    private sealed record PstMessageOracle(uint MessageNid, uint FolderNid, string Subject,
        PstPropertyOracle? Header, int ReceivedCount, IReadOnlyList<PstAttachmentOracle> Attachments);
    private sealed record PstAttachmentOracle(string Name, uint AttachmentNid, int Index, PstPropertyOracle? Property);
    private sealed record PstPropertyOracle(uint Tag, long Length, string Sha256, string Base64,
        IReadOnlyList<PstHeaderFieldOracle>? Fields);
    private sealed record PstHeaderFieldOracle(string Name, long Offset);
}
