using System.Text;
using System.Text.Json;
using MailViewer.Core;

namespace MailViewer.Verification;

internal static partial class Program
{
    private static void TestConversation(string root, string work)
    {
        var samples = Path.Combine(root, "Mail-Viewer", "tests", "fixtures", "conversation-history");
        string[] paths = Directory.GetFiles(samples, "*.eml").Order(StringComparer.Ordinal).ToArray();
        string folder = Directory.CreateDirectory(Path.Combine(work, "conversation")).FullName;
        using var session = EvidenceSession.Open(paths);
        var original = session.Records[0];
        var changed = session.Records[1];
        var followUp = session.Records[2];
        var history = session.GetConversation(changed.Id);
        Equal(3, history.Previous.Count, "references preserved in header and item order");
        Equal("<missing@example.com>", history.Previous[0].Reference.MessageId, "unavailable reference preserved");
        Equal<MailRecord?>(null, history.Previous[0].ReferencedMail, "missing reference has no record");
        Equal(ConversationReferenceKind.LastReference, history.Previous[1].Reference.Kind, "last References marker");
        Equal(ConversationReferenceKind.InReplyTo, history.Previous[2].Reference.Kind, "direct reference preserved");
        Equal(2, history.Later.Count, "reverse references from the follow-up");
        var comparison = history.ReplyComparison;
        Equal(ReplyHistoryStatus.Changed, comparison.Status, "same domain, different full Reply-To");
        Equal(1, comparison.ComparableMailCount, "duplicate references count as one comparison");
        Equal(1, comparison.UnresolvedReferenceCount, "missing reference coverage");
        Equal(original.Id, comparison.Earlier!.RecordId, "matched previous source record");
        Equal(ReplyHistoryStatus.Unchanged, session.CompareReplyHistory(followUp.Id).Status, "domain normalized");
        Equal(ReplyHistoryStatus.NoReference, session.CompareReplyHistory(original.Id).Status, "no earlier reference");

        var request = new SearchRequest { Clues = InvestigationClue.ReplyAddressChanged, AnomaliesOnly = true, SourceId = changed.SourceId };
        Equal(changed.Id, session.Search(request).Hits.Single().Record.Id, "clue filters by current source");
        session.PrepareAnomalyCount();
        Equal<int?>(1, session.AnomalousMailCount, "history change included in tab count");
        var rows = session.Inspect(changed.Id).Observations.Where(row => row.Clue == InvestigationClue.ReplyAddressChanged).ToArray();
        Equal(1, rows.Select(row => row.Finding).Distinct().Count(), "both sources are one finding");
        foreach (var hit in rows.Where(row => row.Key?.Kind == InvestigationKeyKind.Address))
        {
            string raw = Encoding.UTF8.GetString(session.ReadRange(session.GetRecord(hit.EvidenceRecordId!).SourceId, hit.Location!.Range));
            Equal(true, raw.Contains($"Reply-To: {hit.Value}", StringComparison.Ordinal), "comparison header belongs to its source");
        }
        var valueSearch = new SearchRequest { Exact = comparison.Earlier.ReplyTo.Key };
        Equal(original.Id, session.Search(valueSearch).Hits.Single().Record.Id, "cross-record evidence does not alter exact address results");

        var export = ExportService.Export(session, new ExportRequest(folder, ExportKind.Csv,
            ExportScope.SearchResults, [changed.Id], Search: request));
        CreatedPackages.Add(export);
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(export.PackageDirectory, "reply-address-changes.json")));
        var exported = evidence.RootElement.GetProperty("comparisons")[0];
        Equal(30, evidence.RootElement.GetProperty("windowDays").GetInt32(), "comparison window recorded");
        Equal(original.Id, exported.GetProperty("earlier").GetProperty("recordId").GetString(), "earlier record identifier exported");
        Equal(2, exported.GetProperty("basis").GetArrayLength(), "both raw reference headers exported");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(export.PackageDirectory, "manifest.json")));
        Equal(1, manifest.RootElement.GetProperty("records").GetArrayLength(), "comparison source is not an extra exported row");
        Equal(true, manifest.RootElement.GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("id").GetString() == original.SourceId), "earlier source fingerprint in manifest");
        Equal(true, ExportService.Verify(export.PackageDirectory).Success, "comparison evidence package verifies");

        VerifyConversationBoundaries(paths[0], folder);

        string duplicate = Path.Combine(folder, "duplicate.eml");
        File.Copy(paths[0], duplicate);
        using var additional = EvidenceSession.Open([duplicate]);
        var merged = session.Append(additional, request, countAnomalies: true);
        using var combined = merged.Session;
        Equal(0, merged.Result.Hits.Count, "appending a competing original invalidates the prior comparison");
        Equal<int?>(0, combined.AnomalousMailCount, "append recomputes conversation anomalies");
        var newHistory = combined.GetConversation(changed.Id);
        Equal(2, newHistory.Previous.Count(link => link.Reference.Kind == ConversationReferenceKind.LastReference &&
            link.CandidateCount == 2 && link.ReferencedMail is not null), "both IDs available to open");
        var duplicateRecord = newHistory.Previous.First(link => link.ReferencedMail?.SourcePath == duplicate).ReferencedMail!;
        var recordQuery = new SearchRequest { Exact = new(InvestigationKeyKind.RecordId, duplicateRecord.Id) };
        Equal(duplicate, combined.Search(recordQuery).Hits.Single().Record.SourcePath, "open one exact copy by record ID");
    }

    private static void VerifyConversationBoundaries(string originalPath, string folder)
    {
        string path = Path.Combine(folder, "comparison.mbox");
        string original = File.ReadAllText(originalPath);
        const string reference = "References: <payment-original@example.com>";
        string Message(string name, string date, string references = reference, string? reply = "changed@example.com") =>
            $"""
            From: accounts@example.com
            {reply switch { null => "", _ => $"Reply-To: {reply}\n" }}Date: {date}
            Message-ID: <{name}@example.com>
            {references}
            Subject: {name}

            Synthetic comparison case.
            """;
        string[] messages = [
            original,
            Message("at-boundary", "11 Feb 2026 00:00:00 +0000"),
            Message("outside-window", "11 Feb 2026 00:00:01 +0000"),
            Message("unknown-offset", "13 Jan 2026 09:00:00"),
            Message("missing-reply", "13 Jan 2026 09:00:00 +0900", reply: null),
            Message("self-reference", "13 Jan 2026 09:00:00 +0900", "In-Reply-To: <self-reference@example.com>"),
            Message("no-reference", "13 Jan 2026 09:00:00 +0900", "X-Test: no relationship")
        ];
        File.WriteAllText(path, string.Concat(messages.Select(message =>
            $"From fixture@example.com Mon Jan 12 00:00:00 2026\r\n{message.TrimEnd().ReplaceLineEndings("\r\n")}\r\n\r\n")));
        using var session = EvidenceSession.Open([path]);
        Equal(messages.Length, session.Records.Count, "MBOX history fixtures loaded");
        var matches = session.Search(new SearchRequest { Clues = InvestigationClue.ReplyAddressChanged }).Hits;
        Equal("at-boundary", matches.Single().Record.Subject, "inclusive 30-day boundary");
        var atBoundary = session.CompareReplyHistory(matches[0].Record.Id);
        Equal(true, atBoundary.Current!.ReplyTo.Location!.Range.Offset > atBoundary.Earlier!.ReplyTo.Location!.Range.Offset,
            "MBOX comparison locations use container offsets");
        foreach (string subject in new[] { "unknown-offset", "missing-reply" })
            Equal(ReplyHistoryStatus.CurrentUnavailable, Compare(subject).Status, subject);
        Equal(ReplyHistoryIssue.Date, Compare("unknown-offset").CurrentIssue, "identify unusable Date");
        Equal(ReplyHistoryIssue.MissingReplyTo, Compare("missing-reply").CurrentIssue, "identify absent explicit Reply-To");
        Equal(ReplyHistoryStatus.NoComparableMail, Compare("self-reference").Status, "self-reference never becomes a baseline");
        Equal(ReplyHistoryStatus.NoComparableMail, Compare("outside-window").Status, "older than the 30-day window");
        Equal(ReplyHistoryStatus.NoReference, Compare("no-reference").Status, "same From alone cannot create a relation");

        ReplyHistory Compare(string subject) =>
            session.CompareReplyHistory(session.Records.Single(record => record.Subject == subject).Id);
    }
}

