using System.Buffers.Binary;
using System.Text.Json;
using MailViewer.Core;

namespace MailViewer.Verification;

internal static partial class Program
{
    private const string ExpandedTargetAttachment = "image001.jpg";
    private const uint ExpandedTargetMessage = 0x00214D84;

    private static void AddExpandedOstChecks(List<(string Name, Action Test)> cases, string root, string output,
        bool requireFixtures)
    {
        var fixtures = Path.Combine(root, "tests", "fixtures");
        var oracle = JsonSerializer.Deserialize<ExpandedOstOracle>(
            File.ReadAllText(Path.Combine(fixtures, "ost-expanded-oracle.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Expanded OST independent oracle is missing.");
        var fixtureDirectory = Path.Combine(fixtures, "ost");
        var samples = oracle.Samples.Concat(oracle.Controls).ToDictionary(
            sample => Path.Combine(fixtureDirectory, sample.File));
        if (samples.Keys.Any(path => !File.Exists(path)) ||
            !File.Exists(Path.Combine(fixtureDirectory, "pstextractortestpdf.ost")))
        {
            const string acquire = "Run make-ost-oracle.py --fetch-only and make-ost-oracle.py --expanded --fetch-only.";
            if (requireFixtures)
                cases.Add(("expanded OST fixtures available", () => throw new FileNotFoundException(acquire)));
            else
                Console.WriteLine($"SKIP local expanded OST fixtures missing. {acquire}");
            return;
        }

        cases.Add(("genuine legacy permute OST and generated controls preserve folders, header fields and attachments",
            () => TestExpandedOstData(samples, oracle)));
        cases.Add(("legacy permute OST and cyclic control exports preserve property hashes and source scope",
            () => TestExpandedOstExports(samples, oracle, output)));
        cases.Add(("legacy OST mixed with existing PST/OST isolates 512-byte corruption and unsupported encodings",
            () => TestExpandedOstIsolation(samples, fixtures, output)));
    }

    private static void TestExpandedOstData(Dictionary<string, ExpandedOstSource> samples, ExpandedOstOracle oracle)
    {
        using var session = EvidenceSession.Open(samples.Keys.ToArray());
        Equal(samples.Count, session.Sources.Count, "all genuine and generated expanded inputs loaded");
        foreach (var (path, expected) in samples)
        {
            var source = ValidateExpandedOstSource(session, path, expected);
            var content = expected.ExpectationsFrom is { } role
                ? oracle.Samples.Single(item => item.Role == role) : expected;
            var folders = session.PstFolders.Where(folder => folder.SourceId == source.Id).ToDictionary(folder => folder.NodeId);
            Equal(content.Folders!.Count, folders.Count, "normal folder count agrees with the independent parser");
            foreach (var folder in content.Folders)
            {
                Equal(folder.ParentNid, folders[folder.Nid].ParentNodeId, "independent parent NID");
                if (folder.Name is not null)
                    Equal(folder.Name, folders[folder.Nid].Name, "independent folder name");
            }

            var messages = session.Records.Where(record => record.SourceId == source.Id)
                .ToDictionary(record => record.PstMessage!.MessageNodeId);
            Equal(content.Messages!.Count, messages.Count, "supported active mail count");
            foreach (var message in content.Messages)
            {
                var record = messages[message.MessageNid];
                Equal(MailFormat.Ost, record.Format, "source and message types remain OST");
                Equal(message.FolderNid, record.PstMessage!.FolderNodeId, "independent message folder NID");
                Equal(folders[message.FolderNid].Path, record.PstMessage.FolderPath, "message retains exact folder path");
                Equal(message.Subject, record.Subject, "independent message subject");
                CheckExpandedHeader(session, source, record, message.Header);
                CheckExpandedAttachments(session, source, record, message.Attachments);
            }
            Equal(expected.Sha256, FileHash(path), "source remains unchanged after reading properties");
        }
    }

    private static void CheckExpandedHeader(EvidenceSession session, SourceInfo source, MailRecord record,
        OstPropertyOracle? expected)
    {
        if (expected is null)
        {
            Equal(HeaderEvidenceState.Missing, record.TransportHeaderState, "missing headers have an explicit state");
            Require(record.TransportHeaderProperty is null && record.Headers.Count == 0,
                "a missing transport header has no artificial property bytes or field rows");
            return;
        }

        var locator = record.TransportHeaderProperty ?? throw new InvalidDataException("Missing stored header address.");
        Equal(HeaderEvidenceState.Present, record.TransportHeaderState, "stored transport headers are present");
        Equal(record.PstMessage, locator.Message, "header address belongs to the selected message");
        Equal(expected.Tag, locator.PropertyTag, "stored transport header tag");
        Equal(expected.Length, locator.Length, "complete original transport header length");
        byte[] property = session.ReadPstPropertyRange(source.Id, locator, new ByteRange(0, locator.Length));
        Equal(expected.Sha256, Hash(property), "complete transport header property independently verified");
        Equal(expected.Fields!.Count, record.Headers.Count, "transport header field count");
        foreach (var (field, row) in expected.Fields.Zip(record.Headers))
        {
            Equal(field.Name, row.Name, "header fields in stored order");
            Equal(new ByteRange(field.Offset, field.Length), row.Range, "MAPI property-relative header field range");
            Equal(field.Sha256, Hash(property.AsSpan(checked((int)field.Offset), checked((int)field.Length))),
                "exact independently measured field bytes");
        }
    }

    private static void CheckExpandedAttachments(EvidenceSession session, SourceInfo source, MailRecord record,
        IReadOnlyList<OstAttachmentOracle> attachments)
    {
        Equal(attachments.Count, record.Attachments.Count, "attachment table count including unsupported values");
        foreach (var expected in attachments)
        {
            var attachment = record.Attachments[expected.Index];
            if (expected.Property is not { } data)
            {
                Require(attachment.Sha256 is null && attachment.Issue is not null, "unreadable attachment is declared");
                continue;
            }
            var locator = attachment.PstProperty ?? throw new InvalidDataException("Missing stored attachment address.");
            Equal(expected.Name, attachment.Name, "original attachment name");
            Equal(record.PstMessage, locator.Message, "attachment address belongs to the selected message");
            Equal(expected.AttachmentNid, locator.AttachmentNodeId, "original attachment NID");
            Equal(data.Tag, locator.PropertyTag, "original attachment data tag");
            Equal(data.Length, locator.Length, "original attachment data length");
            Equal(data.Sha256, attachment.Sha256, "indexed attachment hash");
            Equal(data.Sha256, Hash(session.ReadAttachment(record.Id, attachment.Id)), "read-back attachment hash");
            foreach (var range in data.Ranges!)
            {
                var actual = session.ReadPstPropertyRange(source.Id, locator, new ByteRange(range.Offset, range.Length));
                Equal(range.Sha256, Hash(actual), "stored attachment subrange");
            }
        }
    }

    private static void TestExpandedOstExports(Dictionary<string, ExpandedOstSource> samples,
        ExpandedOstOracle oracle, string output)
    {
        var baseline = oracle.Samples.Single(sample => sample.Role == "legacy-permute");
        var target = baseline.Messages!.Single(message => message.MessageNid == ExpandedTargetMessage);
        var expectedAttachment = target.Attachments.Single(attachment => attachment.Name == ExpandedTargetAttachment).Property!;
        uint parentFolder = baseline.Folders!.Single(folder => folder.Nid == target.FolderNid).ParentNid!.Value;
        using var session = EvidenceSession.Open(samples.Keys.ToArray());
        var paired = session.Sources.Where(source => source.StoreLayout?.Version == 23).ToArray();
        Equal(2, paired.Length, "authentic permute source and its cyclic control");
        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in paired)
        {
            var query = new SearchRequest(ExpandedTargetAttachment, SearchField.AttachmentName, source.Id,
                PstFolderNodeId: target.FolderNid);
            var search = session.Search(query);
            var record = search.Hits.Single().Record;
            Require(recordIds.Add(record.Id), "same NID between two layouts is scoped to its source");
            Equal(target.MessageNid, record.PstMessage!.MessageNodeId, "independent message identity");
            Equal(0, session.Search(query with { PstFolderNodeId = parentFolder }).Hits.Count,
                "parent scope alone excludes Inbox");
            var subtree = session.Search(query with { PstFolderNodeId = parentFolder, IncludePstSubfolders = true });
            Equal(2, subtree.Hits.Count, "descendants include both Inbox and Sent Items copies");
            Require(subtree.Hits.Any(hit => hit.Record.Id == record.Id), "descendants include the same Inbox record");

            var request = new ExportRequest(output, ExportKind.OstEvidence, ExportScope.SearchResults, [record.Id],
                Search: query, ExaminedCount: search.ExaminedCount);
            var package = ExportForTest(session, request);
            ValidatePackage(package);
            var header = package.Items.Single(item => item.Nature == "ost-transport-header-property");
            Equal(target.Header!.Sha256, FileHash(Path.Combine(package.PackageDirectory, header.RelativePath)),
                "exported header matches independent original property bytes");
            using (var manifest = OpenManifest(package))
            {
                var root = manifest.RootElement;
                var storedSource = root.GetProperty("sources").EnumerateArray().Single();
                var layout = storedSource.GetProperty("storeLayout");
                Equal(source.StoreLayout!.BlockEncoding, layout.GetProperty("blockEncoding").GetByte(), "manifest encoding");
                Equal(source.StoreLayout.PageSize, layout.GetProperty("pageSize").GetInt32(), "manifest index page size");
                Equal(source.StoreLayout.Version, layout.GetProperty("version").GetUInt16(), "manifest NDB version");
                Equal(source.Sha256, storedSource.GetProperty("sha256").GetString(), "manifest original SHA-256");
                Equal(target.FolderNid, root.GetProperty("search").GetProperty("pstFolderNodeId").GetUInt32(),
                    "manifest frozen folder scope");
                Require(root.GetProperty("records")[0].GetProperty("messageRange").ValueKind == JsonValueKind.Null,
                    "manifest leaves physical range blank");
            }

            var attachment = record.Attachments.Single(item => item.Name == ExpandedTargetAttachment);
            var attachmentPackage = ExportForTest(session, new ExportRequest(output, ExportKind.Attachments,
                ExportScope.SelectedAttachments, [record.Id], [new AttachmentSelection(record.Id, attachment.Id)]));
            ValidatePackage(attachmentPackage);
            var exported = attachmentPackage.Items.Single();
            Equal("ost-attachment-property", exported.Nature, "exported data originated as an OST property");
            Equal(attachment.PstProperty, exported.PstProperty, "attachment provenance is preserved");
            var savedAttachment = Path.Combine(attachmentPackage.PackageDirectory, exported.RelativePath);
            Equal(expectedAttachment.Sha256, FileHash(savedAttachment), "exported attachment matches independent property");
            byte[] modified = File.ReadAllBytes(savedAttachment);
            modified[^1] ^= 1;
            File.WriteAllBytes(savedAttachment, modified);
            Require(!ExportService.Verify(attachmentPackage.PackageDirectory).Success, "same-size attachment change is detected");
            Equal(record.Id, session.Search(query).Hits.Single().Record.Id, "repeat search after results verification");
        }
        foreach (var (path, expected) in samples)
            Equal(expected.Sha256, FileHash(path), "exports and verification did not modify the sources");
    }

    private static void TestExpandedOstIsolation(Dictionary<string, ExpandedOstSource> samples, string fixtures, string output)
    {
        var (legacy, oracle) = samples.Single(pair => pair.Value.Role == "legacy-permute");
        var original = File.ReadAllBytes(legacy);
        var corrupt = Path.Combine(output, "damaged-512-byte-index.ost");
        byte[] corruptBytes = original.ToArray();
        long root = BinaryPrimitives.ReadInt64LittleEndian(corruptBytes.AsSpan(224, 8));
        Require(root > 0 && root + 512 <= corruptBytes.LongLength && root % 512 == 0 && corruptBytes[root + 496] == 0x81,
            "legacy NBT root is independently located at a 512-byte index page");
        corruptBytes[root + 32] ^= 1;
        File.WriteAllBytes(corrupt, corruptBytes);

        var protection = Path.Combine(output, "unsupported-wip-legacy.ost");
        byte[] protectedBytes = original.ToArray();
        protectedBytes[513] = 0x10;
        BinaryPrimitives.WriteUInt32LittleEndian(protectedBytes.AsSpan(524, 4), FixtureWeakCrc(protectedBytes.AsSpan(8, 516)));
        File.WriteAllBytes(protection, protectedBytes);
        var modern = Path.Combine(fixtures, "ost", "pstextractortestpdf.ost");
        var pst = Path.Combine(fixtures, "pst", "libyal-outlook.pst");
        using var session = EvidenceSession.Open([corrupt, protection, legacy, modern, pst]);
        var source = ValidateExpandedOstSource(session, legacy, oracle);
        Equal(3, session.Sources.Count, "damaged and unsupported files are excluded while three normal files remain");
        Equal(oracle.Messages!.Count, session.Records.Count(record => record.SourceId == source.Id),
            "the affected source does not change unrelated legacy records");
        Equal(new StoreLayoutInfo(36, 4096, 0), session.Sources.Single(item => item.Path == modern).StoreLayout,
            "modern OST retains its 4 KiB layout");
        Equal(MailFormat.Pst, session.Sources.Single(item => item.Path == pst).Format, "PST content is still recognized");
        var corruptIssue = session.Issues.Single(issue => issue.SourcePath == corrupt);
        Equal("source-read", corruptIssue.Code, "damaged 512-byte root structural diagnosis");
        var protectedIssue = session.Issues.Single(issue => issue.SourcePath == protection);
        Equal("ost-unsupported-encoding", protectedIssue.Code, "unsupported protection diagnosis");
        Equal(new StoreLayoutInfo(23, 512, 0x10), protectedIssue.StoreLayout, "unsupported encoding layout is retained");
        Equal(oracle.Sha256, FileHash(legacy), "corruption tests left the published OST unchanged");
    }

    private static uint FixtureWeakCrc(ReadOnlySpan<byte> buffer)
    {
        uint crc = 0;
        foreach (byte value in buffer)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        return crc;
    }

    private static SourceInfo ValidateExpandedOstSource(EvidenceSession session, string path, ExpandedOstSource expected)
    {
        var source = session.Sources.Single(item => item.Path == path);
        Equal(MailFormat.Ost, source.Format, "OST format from the SO client signature");
        Equal(new StoreLayoutInfo(expected.Version, expected.PageSize, expected.Encoding), source.StoreLayout,
            "independent header layout and encoding");
        Equal(expected.Sha256, source.Sha256, "source checksum");
        Equal(expected.Length, source.Length, "source file length");
        return source;
    }

    private sealed record ExpandedOstOracle(IReadOnlyList<ExpandedOstSource> Samples, IReadOnlyList<ExpandedOstSource> Controls);
    private sealed record ExpandedOstSource(string File, string? Role, string? ExpectationsFrom, ushort Version, int PageSize,
        byte Encoding, long Length, string Sha256, IReadOnlyList<OstFolderOracle>? Folders, IReadOnlyList<OstMessageOracle>? Messages);
}
