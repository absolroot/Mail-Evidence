using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MailViewer.Core;

namespace MailViewer.Verification;

internal static partial class Program
{
    private static void CheckStoredAttachmentName(EvidenceSession session, MailRecord record, MailAttachment attachment)
    {
        var observed = attachment.Analysis?.NameLocation ?? throw new InvalidDataException("No stored attachment name location.");
        (uint tag, byte[] bytes) = observed switch
        {
            { Stream: { } stream } => (stream.PropertyTag, session.ReadCompoundRange(record.SourceId, stream, observed.Range)),
            { Property: { } property } => (property.PropertyTag, session.ReadPstPropertyRange(record.SourceId, property, observed.Range)),
            _ => throw new InvalidDataException("Expected a stream or property for the stored name.")
        };
        var encoding = (tag & 0xffff) == 0x1f ? Encoding.Unicode : Encoding.Latin1;
        Equal(attachment.Name, encoding.GetString(bytes).TrimEnd('\0'), "stored attachment filename evidence");
        var key = new InvestigationKey(InvestigationKeyKind.AttachmentSha256, attachment.Sha256!);
        Require(session.Search(new SearchRequest { Exact = key }).Hits.Any(hit => hit.Record.Id == record.Id),
            "digest query finds the stored attachment");
    }

    private static void TestInvestigation(string directory)
    {
        string eml = Path.Combine(directory, "investigation.eml");
        string mbox = Path.Combine(directory, "investigation.mbox");
        string previous = Path.Combine(directory, "investigation-parent.eml");
        var pe = new byte[64 + 24 + 96];
        "MZ"u8.CopyTo(pe);
        BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(60), 64);
        "PE\0\0"u8.CopyTo(pe.AsSpan(64));
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(84), 96);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(88), 0x10b);
        string current = """
            From: Accounts <Billing+Finance@Example.COM>
            Reply-To: billing@support.example.net
            Date: Wed, 18 Nov 2026 12:00:00 +0000
            dAtE: Wed, 18 Nov 2026 12:00:00 +0000
            Received: from a.example.net (comment (with ; punctuation)) by b.example.net;
              Wed, 18 Nov 2026 11:58:00 +0000
            Received: from x.example.net by a.example.net; Wed, 18 Nov 2026 12:00:00 +0000
            Authentication-Results: gateway.example.net; dkim=pass header.d=example.com;
              dkim=fail header.d=example.net
            Authentication-Results: second.example.net; spf=fail smtp.mailfrom=example.com
            Message-ID: <current@example.com>
            References: <missing@example.com> <"previous"@example.com>
            In-Reply-To: <previous@example.com>
            Subject: Invoice review
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary=review

            --review
            Content-Type: text/html; charset=utf-8

            <a href="https://bob.github.io/pay?q=X&amp;a=2">https://alice.github.io/pay</a>
            <a href="https://www.example.co.uk/">https://portal.example.co.uk/</a>
            <a href="https://xn--bcher-kva.de/">https://bücher.de/</a>
            <a href="/relative">https://alice.github.io/relative</a>
            <a href="https://bob.github.io/button">Open invoice</a>
            --review
            Content-Type: text/plain

            https://bob.github.io/pay?q=X&a=2
            --review
            Content-Type: application/octet-stream
            Content-Disposition: attachment; filename="invoice.pdf"
            Content-Transfer-Encoding: base64

            PE_DATA
            --review
            Content-Type: application/octet-stream
            Content-Disposition: attachment; filename="invoice.pdf.exe"
            Content-Transfer-Encoding: base64

            PE_DATA
            --review
            Content-Type: application/octet-stream
            Content-Disposition: attachment; filename="invoice.jpg"
            Content-Transfer-Encoding: base64

            TVo=
            --review--
            """.Replace("PE_DATA", Convert.ToBase64String(pe));
        string parent = """
            From: Someone <Billing+Finance@example.com>
            Reply-To: prior@example.com
            Message-ID: <previous@example.com>
            Subject: Invoice review
            Content-Type: text/plain

            Earlier invoice.
            """;
        File.WriteAllText(eml, Crlf(current), new UTF8Encoding(false));
        File.WriteAllText(previous, Crlf(parent), new UTF8Encoding(false));
        File.WriteAllText(mbox, "From billing@example.com Wed Nov 18 12:00:00 2026\r\n" + Crlf(current)
            + "\r\nFrom billing@example.com Wed Nov 18 12:01:00 2026\r\n" + Crlf(parent), new UTF8Encoding(false));
        using var session = EvidenceSession.Open([eml, previous, mbox]);
        Equal(4, session.Records.Count, "separate MBOX and EML observations");
        session.PrepareAnomalyCount();
        Equal(2, session.AnomalousMailCount, "anomaly count is per mail, independent of its findings");
        var reports = session.Records.ToDictionary(mail => mail.Id, mail => session.Inspect(mail.Id));
        foreach (var clue in Enum.GetValues<InvestigationClue>().Where(item => item != InvestigationClue.None))
        {
            string[] expected = session.Records.Where(mail => reports[mail.Id].Observations.Any(row => row.Clue == clue))
                .Select(mail => mail.Id).ToArray();
            var filtered = session.Search(new SearchRequest { Clues = clue });
            Require(expected.SequenceEqual(filtered.Hits.Select(hit => hit.Record.Id)), $"summary and evidence agree for {clue}");
            Equal(reports.Values.Count(report => (report.Unavailable & clue) != 0),
                filtered.InvestigationUnavailableCount, $"unavailable count for {clue}");
        }
        foreach (var key in reports.Values.SelectMany(report => report.Observations)
                     .Select(row => row.Key).OfType<InvestigationKey>().Distinct())
        {
            string[] expected = session.Records.Where(mail => reports[mail.Id].Observations.Any(row =>
                row.Key == key && (row.EvidenceRecordId is null || row.EvidenceRecordId == mail.Id) &&
                (key.Kind != InvestigationKeyKind.MessageId || row.Field == "Message-ID"))).Select(mail => mail.Id).ToArray();
            Require(expected.SequenceEqual(session.Search(new SearchRequest { Exact = key }).Hits.Select(hit => hit.Record.Id)),
                $"exact lookup and evidence agree for {key}");
        }
        var zeroScope = session.Sources.Single(source => source.Path == previous);
        Equal(0, session.Search(new SearchRequest(SourceId: zeroScope.Id) { AnomaliesOnly = true }).Hits.Count,
            "anomaly-only scope excludes mail without findings");
        var record = session.Records.Single(item => item.SourcePath == eml);
        var observations = session.Inspect(record.Id).Observations;
        Equal(2, observations.Count(row => row.Clue == InvestigationClue.DuplicateHeader), "duplicate Date occurrences");
        Equal(2, observations.Count(row => row.Clue == InvestigationClue.ReceivedTimeReversal), "adjacent UTC relay timestamps");
        Equal(2, observations.Count(row => row.Clue == InvestigationClue.ReplyDomainMismatch), "two differing addresses");
        var auth = observations.Where(row => row.Field == "Authentication-Results").ToArray();
        Equal(3, auth.Length, "separate recorded authentication assertions");
        Require(auth[1].Value.Contains("header.d=example.net", StringComparison.Ordinal) &&
            auth[0].Location == auth[1].Location && auth[0].Location != auth[2].Location, "authentication locations");
        var link = observations.Single(row => row.Clue == InvestigationClue.LinkDomainMismatch && row.Field == "Link target");
        Require(observations.Any(row => row.Finding == link.Finding && row.Field == "Displayed URL" &&
            row.Value.Contains("alice.github.io", StringComparison.Ordinal)), "private suffix mismatch");
        Equal(7, observations.Where(row => row.Finding > 0).Select(row => row.Finding).Distinct().Count(), "one finding per event");
        Require(observations.Any(row => row.Value == "invoice.jpg" &&
            row.Boundary is { Kind: AttachmentBoundaryKind.HeaderMismatch, Range.Offset: 0 }), "short wrong signature evidence");
        var relatedLink = session.Search(new SearchRequest { Exact = link.Key });
        Equal(2, relatedLink.Hits.Count, "one result row per message with matching URL");
        Equal(2, observations.Count(row => row.Field == "URL" && row.Key == link.Key), "HTML and plain occurrence locations");
        Require(Encoding.UTF8.GetString(session.ReadRange(record.SourceId, link.Location!.Range))
            .Contains("href=", StringComparison.Ordinal), "link connects to original MIME body");
        var attachment = record.Attachments.Single(item => item.Name == "invoice.pdf");
        Require(attachment.Analysis is { PeHeader.Offset: 64, NameLocation: not null }, "PE and exact name evidence");
        var name = attachment.Analysis!.NameLocation!;
        Require(Encoding.UTF8.GetString(session.ReadRange(record.SourceId, name.Range)).Contains("filename=\"invoice.pdf\"", StringComparison.Ordinal),
            "MIME name evidence points to the disposition header");
        Equal(1, observations.Count(row => row.Clue == InvestigationClue.DisguisedAttachmentName), "double extension");
        Require(record.Attachments.Single(item => item.Name == "invoice.jpg").Analysis is { PeUnreadable: true },
            "truncated PE explicitly unreadable");
        Equal(2, session.Search(new SearchRequest { Exact = new(InvestigationKeyKind.AttachmentSha256, attachment.Sha256!) }).Hits.Count,
            "same bytes under different names, separate messages");
        Equal(0, session.Search(new SearchRequest { Exact = new(InvestigationKeyKind.Address, "billing+finance@example.com") }).Hits.Count,
            "no local-part case folding");
        Equal(4, session.Search(new SearchRequest { Exact = new(InvestigationKeyKind.Address, "Billing+Finance@example.com") }).Hits.Count,
            "address domain normalization");
        var reference = observations.Single(row => row.Field == "Last reference");
        Equal(2, session.Search(new SearchRequest { Exact = reference.Key }).Hits.Count, "quoted ID and duplicate sources");
        var missing = observations.Single(row => row.Field == "References");
        Equal(0, session.Search(new SearchRequest { Exact = missing.Key }).Hits.Count, "missing reference");
        var request = new SearchRequest(Query: "Invoice", Field: SearchField.Subject)
        {
            Clues = InvestigationClue.DuplicateHeader | InvestigationClue.AttachmentPeMismatch
        };
        var result = session.Search(request);
        Equal(2, result.Hits.Count, "clue OR plus existing subject search AND");
        var saved = ExportForTest(session, new ExportRequest(directory, ExportKind.Csv, ExportScope.SearchResults,
            result.Hits.Select(hit => hit.Record.Id).ToArray(), Search: request));
        ValidatePackage(saved);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved.PackageDirectory, "manifest.json")));
        Require(manifest.RootElement.GetProperty("search").TryGetProperty("clues", out _), "serialized clue filter");

        string newSource = Path.Combine(directory, "investigation-added.eml");
        File.WriteAllText(newSource, Crlf(current.Replace("<current@example.com>", "<new@example.com>")), new UTF8Encoding(false));
        using var addition = EvidenceSession.Open([newSource]);
        var added = session.Append(addition, new SearchRequest { AnomaliesOnly = true }, countAnomalies: true);
        using var combined = added.Session;
        Equal(3, combined.AnomalousMailCount, "anomalous mail count after adding a source");
        Equal(3, added.Result.Hits.Count, "updated list agrees with combined session count");
        return;

        static string Crlf(string text) => text.ReplaceLineEndings("\r\n") + "\r\n";
    }
}
