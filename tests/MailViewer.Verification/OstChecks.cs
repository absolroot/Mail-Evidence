using System.Buffers.Binary;
using System.Text.Json;
using MailViewer.Core;

namespace MailViewer.Verification;

internal static partial class Program
{
    private const uint OstInboxNid = 8578;
    private const uint OstMailboxSubtreeNid = 8514;
    private const uint OstTargetMessageNid = 2097380;
    private const string OstPdfName = "Clinometer Usage.pdf";

    private static void AddOstChecks(List<(string Name, Action Test)> cases, string root, string output,
        bool requireFixtures)
    {
        var fixtures = Path.Combine(root, "tests", "fixtures");
        var oracle = JsonSerializer.Deserialize<OstOracle>(File.ReadAllText(Path.Combine(fixtures, "ost-oracle.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Independent OST oracle is missing.");
        var primary = Path.Combine(fixtures, "ost", oracle.Primary.File);
        var secondary = Path.Combine(fixtures, "ost", oracle.Secondary.File);
        if (!File.Exists(primary) || !File.Exists(secondary))
        {
            const string acquisition = "Run python tests/fixtures/make-ost-oracle.py --fetch-only.";
            if (requireFixtures)
                cases.Add(("public OST fixtures available", () => throw new FileNotFoundException(acquisition)));
            else
                Console.WriteLine($"SKIP local public OST fixtures missing. {acquisition}");
            return;
        }

        cases.Add(("public OST folder hierarchy, isolated NIDs, direct and descendant search",
            () => TestOstFolderSearch(primary, secondary, oracle)));
        cases.Add(("two public OST files retain exact raw transport headers and attachment properties",
            () => TestOstProperties(primary, secondary, oracle)));
        cases.Add(("OST evidence, attachment and CSV exports verify and preserve frozen search",
            () => TestOstExports(primary, secondary, oracle, output)));
        cases.Add(("OST content signature, damaged 4 KiB index, unsupported version and original lock",
            () => TestOstSourceHandling(primary, oracle.Primary, Path.Combine(fixtures, "pst", "libyal-outlook.pst"), output)));
    }

    private static void TestOstFolderSearch(string primaryPath, string secondaryPath, OstOracle oracle)
    {
        using var session = EvidenceSession.Open([primaryPath, secondaryPath]);
        Require(session.Issues.Count == 0, "both real OST sources open without read issues");
        var first = ValidateOstSource(session, primaryPath, oracle.Primary);
        var second = ValidateOstSource(session, secondaryPath, oracle.Secondary);
        foreach (var (source, expected) in new[] { (first, oracle.Primary), (second, oracle.Secondary) })
        {
            var folders = session.PstFolders.Where(folder => folder.SourceId == source.Id)
                .ToDictionary(folder => folder.NodeId);
            Equal(expected.Folders.Count, folders.Count, "complete normal folder hierarchy");
            foreach (var folder in expected.Folders)
            {
                Equal(folder.ParentNid, folders[folder.Nid].ParentNodeId, "original parent folder NID");
                if (folder.Name is not null)
                    Equal(folder.Name, folders[folder.Nid].Name, "original folder name");
            }

            var messages = session.Records.Where(record => record.SourceId == source.Id)
                .ToDictionary(record => record.PstMessage!.MessageNodeId);
            Equal(expected.Messages.Count, messages.Count, "complete supported mail population");
            foreach (var message in expected.Messages)
            {
                var record = messages[message.MessageNid];
                Equal(MailFormat.Ost, record.Format, "native OST message format");
                Equal(message.FolderNid, record.PstMessage!.FolderNodeId, "true message folder NID");
                Equal(folders[message.FolderNid].Path, record.PstMessage.FolderPath, "true message folder path");
                Equal(message.Subject, record.Subject, "stored message subject preserves trailing spaces");
            }
        }

        var pdf = new SearchRequest(OstPdfName, SearchField.AttachmentName, SourceId: first.Id,
            PstFolderNodeId: OstInboxNid);
        var target = session.Search(pdf).Hits.Single().Record;
        Equal(OstTargetMessageNid, target.PstMessage!.MessageNodeId, "PDF attachment name finds the proper message");
        var secondaryPdf = session.Search(pdf with { SourceId = second.Id }).Hits;
        Require(secondaryPdf.Select(hit => hit.Record.PstMessage!.MessageNodeId).Order().SequenceEqual([2104324u, 2104516u]),
            "the same attachment name isolates two other messages in the second source");
        var parent = pdf with { PstFolderNodeId = OstMailboxSubtreeNid };
        Equal(0, session.Search(parent).Hits.Count, "direct parent scope excludes Inbox");
        Equal(target.Id, session.Search(parent with { IncludePstSubfolders = true }).Hits.Single().Record.Id,
            "including descendants reaches the same PDF message");
        var counterpart = session.Records.Single(record => record.SourceId == second.Id &&
            record.PstMessage!.MessageNodeId == OstTargetMessageNid);
        Require(target.Id != counterpart.Id, "equal folder and message NIDs in two sources do not identify the same record");
        Equal(0, session.Search(pdf with { SourceId = second.Id, Query = "single PDF", Field = SearchField.Subject }).Hits.Count,
            "the primary subject stays isolated even when source NIDs coincide");
        Equal(target.Id, session.Search(pdf with { Query = "for Windows", Field = SearchField.Body }).Hits.Single().Record.Id,
            "stored HTML body is searchable as visible text");
        Throws<ArgumentException>(() => session.Search(pdf with { SourceId = null }), "folder scope requires a source ID");
    }

    private static void TestOstProperties(string primaryPath, string secondaryPath, OstOracle oracle)
    {
        using var session = EvidenceSession.Open([primaryPath, secondaryPath]);
        foreach (var (path, expected) in new[] { (primaryPath, oracle.Primary), (secondaryPath, oracle.Secondary) })
        {
            var source = ValidateOstSource(session, path, expected);
            foreach (var message in expected.Messages)
            {
                var record = session.Records.Single(record => record.SourceId == source.Id &&
                    record.PstMessage!.MessageNodeId == message.MessageNid);
                if (message.Header is { } header)
                {
                    var location = record.TransportHeaderProperty!;
                    Equal(HeaderEvidenceState.Present, record.TransportHeaderState, "stored transport header is present");
                    Equal(record.PstMessage, location.Message, "header points to the selected message and folder");
                    Equal(header.Tag, location.PropertyTag, "transport header MAPI tag");
                    Equal(header.Length, location.Length, "original header property length");
                    Equal(header.Sha256, Hash(session.ReadPstPropertyRange(source.Id, location, new ByteRange(0, header.Length))),
                        "independent digest of the original header bytes");
                    Equal(header.Fields!.Count, record.Headers.Count, "original header field count");
                    foreach (var (field, actual) in header.Fields.Zip(record.Headers))
                    {
                        Equal(field.Name, actual.Name, "header field name and original order");
                        Equal(new ByteRange(field.Offset, field.Length), actual.Range, "original header field byte range");
                        Equal(field.Sha256, Hash(session.ReadPstPropertyRange(source.Id, location, actual.Range)),
                            "individual folded or repeated header field matches the independent digest");
                    }
                }
                else
                {
                    Equal(HeaderEvidenceState.Missing, record.TransportHeaderState, "absent transport header is identified");
                    Require(record.TransportHeaderProperty is null && record.Headers.Count == 0,
                        "absent header has no reconstructed rows or property locator");
                }

                Equal(message.Attachments.Count, record.Attachments.Count, "original attachment table count");
                foreach (var expectedAttachment in message.Attachments)
                {
                    var attachment = record.Attachments[expectedAttachment.Index];
                    var location = attachment.PstProperty!;
                    var data = expectedAttachment.Property!;
                    Equal(expectedAttachment.Name, attachment.Name, "original attachment name");
                    Equal(record.PstMessage, location.Message, "attachment belongs to this message and folder");
                    Equal(expectedAttachment.AttachmentNid, location.AttachmentNodeId, "original attachment NID");
                    Equal(expectedAttachment.Index, location.AttachmentIndex, "original attachment index");
                    Equal(data.Tag, location.PropertyTag, "original attachment MAPI tag");
                    Equal(data.Length, location.Length, "original attachment byte count");
                    Equal(data.Sha256, attachment.Sha256, "independently established attachment digest");
                    Require(attachment.EncodedRange is null && attachment.CompoundStream is null,
                        "an OST attachment exposes a property locator only");
                    Equal(data.Sha256, Hash(session.ReadAttachment(record.Id, attachment.Id)), "direct attachment bytes");
                    CheckStoredAttachmentName(session, record, attachment);
                    foreach (var range in data.Ranges!)
                        Equal(range.Sha256, Hash(session.ReadPstPropertyRange(source.Id, location,
                            new ByteRange(range.Offset, range.Length))), "original attachment subrange digest");
                }
            }
            Equal(expected.Sha256, FileHash(path), "original OST is unchanged after comparison");
        }

        var primary = session.Sources.Single(source => source.Sha256 == oracle.Primary.Sha256);
        var secondary = session.Sources.Single(source => source.Sha256 == oracle.Secondary.Sha256);
        var target = session.Records.Single(record => record.SourceId == primary.Id &&
            record.PstMessage!.MessageNodeId == OstTargetMessageNid);
        Throws<ArgumentException>(() => session.ReadPstPropertyRange(secondary.Id, target.TransportHeaderProperty!,
            new ByteRange(0, 8)), "primary property locator is rejected for the equal NID in the second source");
    }

    private static void TestOstExports(string primaryPath, string secondaryPath, OstOracle oracle, string output)
    {
        using var session = EvidenceSession.Open([primaryPath, secondaryPath]);
        var source = ValidateOstSource(session, primaryPath, oracle.Primary);
        var query = new SearchRequest("single PDF", SearchField.Subject, SourceId: source.Id,
            PstFolderNodeId: OstInboxNid, HasAttachment: true);
        var search = session.Search(query);
        var target = search.Hits.Single().Record;
        var expected = oracle.Primary.Messages.Single(message => message.MessageNid == OstTargetMessageNid);
        var evidenceRequest = new ExportRequest(output, ExportKind.OstEvidence, ExportScope.SearchResults,
            [target.Id], Search: query);
        Throws<InvalidOperationException>(() => ExportService.Export(session, evidenceRequest with { Kind = ExportKind.PstEvidence }),
            "OST cannot be saved with the PST evidence type");
        Throws<InvalidOperationException>(() => ExportService.Export(session, evidenceRequest with { Kind = ExportKind.MessageBytes }),
            "an individual OST row has no original physical mail file");

        var evidence = ExportForTest(session, evidenceRequest);
        ValidatePackage(evidence);
        var header = evidence.Items.Single(item => item.Nature == "ost-transport-header-property");
        Equal(target.TransportHeaderProperty, header.PstProperty, "saved header preserves its property locator");
        Equal(default(ByteRange?), header.InputRange, "no physical input offset for logical header bytes");
        Equal(expected.Header!.Sha256, FileHash(Path.Combine(evidence.PackageDirectory, header.RelativePath)),
            "saved header digest against independent parser");
        using (var manifest = OpenManifest(evidence))
        {
            var data = manifest.RootElement;
            Equal("ostEvidence", data.GetProperty("kind").GetString(), "manifest evidence type");
            Equal("ost", data.GetProperty("sources").EnumerateArray().Single().GetProperty("format").GetString(),
                "manifest source format");
            Equal(source.Sha256, data.GetProperty("sources").EnumerateArray().Single().GetProperty("sha256").GetString(),
                "manifest original source hash");
            var record = data.GetProperty("records").EnumerateArray().Single();
            Equal("ost-property-offset", record.GetProperty("headerRangeKind").GetString(), "header uses logical property offsets");
            Equal(expected.MessageNid, record.GetProperty("pstMessage").GetProperty("messageNodeId").GetUInt32(),
                "manifest message NID");
            Equal(expected.FolderNid, data.GetProperty("search").GetProperty("pstFolderNodeId").GetUInt32(),
                "manifest frozen folder scope");
        }
        var metadata = evidence.Items.Single(item => item.Nature == "ost-message-metadata");
        using (var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidence.PackageDirectory, metadata.RelativePath))))
        {
            Equal("ost", record.RootElement.GetProperty("source").GetProperty("format").GetString(), "metadata source format");
            Equal(target.Id, record.RootElement.GetProperty("message").GetProperty("id").GetString(), "metadata message identity");
        }

        var missing = session.Records.Single(record => record.SourceId == source.Id &&
            record.TransportHeaderState == HeaderEvidenceState.Missing);
        var metadataOnly = ExportForTest(session, new ExportRequest(output, ExportKind.OstEvidence,
            ExportScope.SelectedMessages, [missing.Id]));
        ValidatePackage(metadataOnly);
        Equal("ost-message-metadata", metadataOnly.Items.Single().Nature, "missing header exports metadata only");

        var report = ExportForTest(session, evidenceRequest with { Kind = ExportKind.Csv });
        ValidatePackage(report);
        using (var csv = OpenCsv(Path.Combine(report.PackageDirectory, report.Items.Single().RelativePath)))
        {
            var names = csv.ReadFields() ?? throw new InvalidDataException("OST CSV columns missing.");
            var fields = csv.ReadFields() ?? throw new InvalidDataException("OST CSV row missing.");
            var row = names.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second);
            Equal(target.Id, row["RecordId"], "CSV frozen search row");
            Equal("Ost", row["StoreFormat"], "CSV identifies OST source");
            Equal(target.PstMessage!.FolderNid, row["StoreFolderNid"], "CSV logical folder NID");
            Equal(target.PstMessage.MessageNid, row["StoreMessageNid"], "CSV logical message NID");
            Equal(target.TransportHeaderProperty!.TagHex, row["StoreTransportHeaderTag"], "CSV original header property tag");
            Require(row["Offset"].Length == 0 && row["Length"].Length == 0, "CSV physical range is absent");
            Require(csv.EndOfData, "CSV preserves exact search scope");
        }

        var attachment = target.Attachments.Single();
        var package = ExportForTest(session, new ExportRequest(output, ExportKind.Attachments, ExportScope.SelectedAttachments,
            [target.Id], [new AttachmentSelection(target.Id, attachment.Id)]));
        ValidatePackage(package);
        var saved = package.Items.Single();
        Equal("ost-attachment-property", saved.Nature, "attachment evidence type");
        Equal(attachment.PstProperty, saved.PstProperty, "exported attachment provenance");
        Equal(default(ByteRange?), saved.InputRange, "attachment has no physical evidence range");
        var path = Path.Combine(package.PackageDirectory, saved.RelativePath);
        Equal(expected.Attachments.Single().Property!.Sha256, FileHash(path), "saved attachment matches independent raw property");
        var tampered = File.ReadAllBytes(path);
        tampered[^1] ^= 1;
        File.WriteAllBytes(path, tampered);
        Require(!ExportService.Verify(package.PackageDirectory).Success, "same-length saved attachment tampering is detected");
        var resultAfter = session.Search(query);
        Equal(search.ExaminedCount, resultAfter.ExaminedCount, "export preserves search scope");
        Equal(target.Id, resultAfter.Hits.Single().Record.Id, "repeat search returns the same message after verification");
        Equal(oracle.Primary.Sha256, FileHash(primaryPath), "original OST stays unchanged after exports");
        Equal(oracle.Secondary.Sha256, FileHash(secondaryPath), "second original OST stays unchanged after exports");
    }

    private static void TestOstSourceHandling(string primary, OstSourceOracle oracle, string pst, string output)
    {
        var renamedOst = Path.Combine(output, "genuine-ost-named-pst.pst");
        var renamedPst = Path.Combine(output, "genuine-pst-named-ost.ost");
        File.Copy(primary, renamedOst);
        File.Copy(pst, renamedPst);
        var bytes = File.ReadAllBytes(primary);
        Equal(oracle.ClientMagic, System.Text.Encoding.ASCII.GetString(bytes, 8, 2), "native SO header");
        Equal(oracle.Version, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2)), "native OST version");
        var truncated = Path.Combine(output, "truncated-ost.ost");
        File.WriteAllBytes(truncated, bytes[..1024]);
        var unsupported = Path.Combine(output, "unsupported-ost-version.ost");
        bytes[10] = 37;
        File.WriteAllBytes(unsupported, bytes);
        bytes[10] = 36;
        var corruptIndex = Path.Combine(output, "damaged-ost-index.ost");
        var nbtRoot = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0xE0, 8));
        Require(nbtRoot > 0 && nbtRoot + 4096 <= bytes.Length && nbtRoot % 4096 == 0 && bytes[nbtRoot + 4072] == 0x81,
            "independent NBT root lies within a 4 KiB page in the real fixture");
        bytes[checked((int)nbtRoot + 32)] ^= 1;
        File.WriteAllBytes(corruptIndex, bytes);

        using var session = EvidenceSession.Open([truncated, unsupported, corruptIndex, renamedOst, renamedPst, primary]);
        var original = ValidateOstSource(session, primary, oracle);
        var renamed = ValidateOstSource(session, renamedOst, oracle);
        Require(original.Id != renamed.Id, "identical copies at different paths have separate source identities");
        Equal(MailFormat.Pst, session.Sources.Single(source => source.Path == renamedPst).Format, "SM header classifies renamed PST");
        Equal(3, session.Sources.Count, "damaged and unsupported inputs do not become evidence sources");
        Equal(2, session.Issues.Count(issue => issue.Code == "source-read"), "corrupt inputs are reported");
        var versionIssue = session.Issues.Single(issue => issue.Code == "ost-unsupported-version");
        Equal((ushort)37, versionIssue.StoreLayout!.Version, "unsupported OST version has a structured diagnosis");
        foreach (var path in new[] { truncated, unsupported, corruptIndex })
            Require(session.Issues.Any(issue => issue.Detail.Contains(path, StringComparison.Ordinal)), "each input is reported by its own path");
        Equal(oracle.Messages.Count, session.Records.Count(record => record.SourceId == original.Id), "original records remain intact");
        if (OperatingSystem.IsWindows())
            Throws<IOException>(() =>
            {
                using var _ = File.Open(primary, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }, "the session blocks Windows write handles to the original OST");
        Equal(oracle.Sha256, FileHash(primary), "rejections and renamed copies leave the original OST intact");
    }

    private static SourceInfo ValidateOstSource(EvidenceSession session, string path, OstSourceOracle oracle)
    {
        var source = session.Sources.Single(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        Equal(MailFormat.Ost, source.Format, "native source format from content");
        Equal("ost-unicode36-4k-property-v1", source.BoundaryRule, "per-format source boundary rule");
        Equal(oracle.Length, source.Length, "OST source length");
        Equal(oracle.Sha256, source.Sha256, "OST checksum from independent oracle");
        return source;
    }

    private sealed record OstOracle(OstSourceOracle Primary, OstSourceOracle Secondary);
    private sealed record OstSourceOracle(string File, string ClientMagic, ushort Version, long Length, string Sha256,
        IReadOnlyList<OstFolderOracle> Folders, IReadOnlyList<OstMessageOracle> Messages);
    private sealed record OstFolderOracle(uint Nid, uint? ParentNid, string? Name);
    private sealed record OstMessageOracle(uint MessageNid, uint FolderNid, string Subject,
        OstPropertyOracle? Header, IReadOnlyList<OstAttachmentOracle> Attachments);
    private sealed record OstAttachmentOracle(string Name, int Index, uint AttachmentNid, OstPropertyOracle? Property);
    private sealed record OstPropertyOracle(uint Tag, long Length, string Sha256,
        IReadOnlyList<OstHeaderFieldOracle>? Fields, IReadOnlyList<OstRangeOracle>? Ranges);
    private sealed record OstRangeOracle(long Offset, long Length, string Sha256);
    private sealed record OstHeaderFieldOracle(string Name, long Offset, long Length, string Sha256);
}
