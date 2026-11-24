using System.Text.RegularExpressions;
using MimeKit.Utils;

namespace MailViewer.Core;

public enum ConversationReferenceKind { Reference, LastReference, InReplyTo }
public enum ReplyHistoryStatus { NoReference, CurrentUnavailable, NoComparableMail, Ambiguous, Unchanged, Changed }
public enum ReplyHistoryIssue { None, TransportHeaders, References, Date, From, MissingReplyTo, ReplyTo }

public sealed record ConversationReference(ConversationReferenceKind Kind, string MessageId, MailHeader Header,
    EvidenceLocation Location);
public sealed record ConversationLink(ConversationReference Reference, MailRecord ReferencingMail, MailRecord? ReferencedMail,
    int CandidateCount);
public sealed record ReplyHistoryMail(string RecordId, string SourceId, DateTimeOffset Date,
    InvestigationObservation DateHeader, InvestigationObservation From, InvestigationObservation ReplyTo);
public sealed record ReplyHistory(ReplyHistoryStatus Status, int ComparableMailCount, int UnresolvedReferenceCount,
    ReplyHistoryMail? Current = null, ReplyHistoryMail? Earlier = null, IReadOnlyList<ConversationReference>? Basis = null)
{
    public ReplyHistoryIssue CurrentIssue { get; init; }
}
public sealed record ConversationHistory(IReadOnlyList<ConversationLink> Previous, IReadOnlyList<ConversationLink> Later,
    ReplyHistory ReplyComparison);

internal sealed partial class ConversationIndex
{
    internal const int WindowDays = 30;
    private readonly Dictionary<string, ConversationReference[]> references = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MailRecord>> messagesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(MailRecord Mail, ConversationReference Reference)>> laterById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> idsByRecord = new(StringComparer.Ordinal);

    public ConversationIndex(IReadOnlyList<MailRecord> records, CancellationToken ct)
    {
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            if (!record.Headers.Any(IsReferenceHeader)) continue;
            Index(records, ct);
            return;
        }
    }

    private void Index(IReadOnlyList<MailRecord> records, CancellationToken ct)
    {
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            var ids = new List<string>();
            var refs = new List<ConversationReference>();
            foreach (var header in record.Headers)
            {
                ct.ThrowIfCancellationRequested();
                bool isReference = header.Name.Equals("References", StringComparison.OrdinalIgnoreCase);
                bool isReply = header.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase);
                if (isReference || isReply)
                {
                    var values = InvestigationHeaderValues.ParseIds(header.Value).ToArray();
                    for (int i = 0; i < values.Length; i++)
                        refs.Add(new(isReply ? ConversationReferenceKind.InReplyTo :
                            i == values.Length - 1 ? ConversationReferenceKind.LastReference : ConversationReferenceKind.Reference,
                            values[i], header, Location(record, header)));
                }
                else if (header.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase))
                    ids.AddRange(InvestigationHeaderValues.ParseIds(header.Value));
            }

            if (ids.Count > 0)
            {
                var distinct = ids.Distinct(StringComparer.Ordinal).ToArray();
                idsByRecord.Add(record.Id, distinct);
                foreach (string id in distinct)
                    Add(messagesById, id, record);
            }
            if (refs.Count > 0)
            {
                references.Add(record.Id, refs.ToArray());
                foreach (var reference in refs)
                    Add(laterById, reference.MessageId, (record, reference));
            }
        }
    }

    public int CountRelated(MailRecord record)
    {
        var related = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in References(record).DistinctBy(item => item.MessageId, StringComparer.Ordinal))
            if (messagesById.TryGetValue(reference.MessageId, out var candidates))
                foreach (var candidate in candidates)
                    related.Add(candidate.Id);

        foreach (string id in idsByRecord.GetValueOrDefault(record.Id) ?? [])
            if (laterById.TryGetValue(id, out var links))
                foreach (var link in links)
                    related.Add(link.Mail.Id);

        related.Remove(record.Id);
        return related.Count;
    }

    public ConversationHistory Open(MailRecord record, ReplyHistory comparison, CancellationToken ct)
    {
        var previous = new List<ConversationLink>();
        foreach (var reference in References(record))
        {
            ct.ThrowIfCancellationRequested();
            if (messagesById.TryGetValue(reference.MessageId, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    previous.Add(new(reference, record, candidate, candidates.Count));
                }
            }
            else previous.Add(new(reference, record, null, 0));
        }

        var later = new List<ConversationLink>();
        foreach (string id in idsByRecord.GetValueOrDefault(record.Id) ?? [])
        {
            if (!laterById.TryGetValue(id, out var links)) continue;
            foreach (var link in links)
            {
                ct.ThrowIfCancellationRequested();
                later.Add(new(link.Reference, link.Mail, record, messagesById[id].Count));
            }
        }
        return new(previous.AsReadOnly(), later.AsReadOnly(), comparison);
    }

    public ReplyHistory Compare(MailRecord record, CancellationToken ct)
    {
        var refs = References(record);
        if (refs.Length == 0)
        {
            bool hasReferenceHeader = record.Headers.Any(IsReferenceHeader);
            if (!HasReadableHeaders(record)) return Unavailable(ReplyHistoryIssue.TransportHeaders);
            return hasReferenceHeader ? Unavailable(ReplyHistoryIssue.References) : new(ReplyHistoryStatus.NoReference, 0, 0);
        }

        if (ReadComparisonMail(record, out var issue) is not { } current)
            return Unavailable(issue);

        var eligible = new List<(ReplyHistoryMail Mail, ConversationReference[] Basis)>();
        int unresolved = 0;
        foreach (var group in refs.GroupBy(reference => reference.MessageId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!messagesById.TryGetValue(group.Key, out var candidates) || candidates.Count != 1 ||
                candidates[0].Id == record.Id || !HasSingleId(candidates[0]))
            {
                unresolved++;
                continue;
            }
            if (ReadComparisonMail(candidates[0], out _) is not { } earlier)
            {
                unresolved++;
                continue;
            }
            if (earlier.From.Key != current.From.Key) continue;
            var age = current.Date - earlier.Date;
            if (age <= TimeSpan.Zero || age > TimeSpan.FromDays(WindowDays)) continue;
            eligible.Add((earlier, group.ToArray()));
        }

        if (eligible.Count == 0)
            return new(ReplyHistoryStatus.NoComparableMail, 0, unresolved, current);

        var nearest = eligible.MaxBy(candidate => candidate.Mail.Date);
        if (eligible.Any(candidate => candidate.Mail.Date == nearest.Mail.Date
            && candidate.Mail.ReplyTo.Key != nearest.Mail.ReplyTo.Key))
            return new(ReplyHistoryStatus.Ambiguous, eligible.Count, unresolved, current);

        var status = nearest.Mail.ReplyTo.Key == current.ReplyTo.Key ? ReplyHistoryStatus.Unchanged : ReplyHistoryStatus.Changed;
        return new(status, eligible.Count, unresolved, current, nearest.Mail, Array.AsReadOnly(nearest.Basis));
    }

    private static bool IsReferenceHeader(MailHeader header) =>
        header.Name.Equals("References", StringComparison.OrdinalIgnoreCase) ||
        header.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase);

    private static bool HasSingleId(MailRecord record)
    {
        var headers = record.Headers.Where(header => header.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return headers.Length == 1 && InvestigationHeaderValues.ParseIds(headers[0].Value).Take(2).Count() == 1;
    }

    internal bool HasReferences(MailRecord record) => references.ContainsKey(record.Id);

    private ConversationReference[] References(MailRecord record) => references.GetValueOrDefault(record.Id) ?? [];

    [GeneratedRegex(@"^(?:(?<weekday>Mon|Tue|Wed|Thu|Fri|Sat|Sun),\s*)?[0-9]{1,2}\s+[A-Z]{3}\s+[0-9]{4}\s+[0-9]{2}:[0-9]{2}(?::[0-9]{2})?\s+[+-][0-9]{2}[0-5][0-9]\s*(?:\(|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericTimeZone();

    private static ReplyHistory Unavailable(ReplyHistoryIssue issue) =>
        new(ReplyHistoryStatus.CurrentUnavailable, 0, 0) { CurrentIssue = issue };

    private static bool HasReadableHeaders(MailRecord record) =>
        record.Headers.Count > 0 && record.TransportHeaderState is not
            (HeaderEvidenceState.Unreadable or HeaderEvidenceState.Empty or HeaderEvidenceState.Missing);

    private static ReplyHistoryMail? ReadComparisonMail(MailRecord record, out ReplyHistoryIssue issue)
    {
        issue = ReplyHistoryIssue.TransportHeaders;
        if (!HasReadableHeaders(record)) return null;
        issue = ReplyHistoryIssue.Date;
        var headers = record.Headers.ToLookup(header => header.Name, StringComparer.OrdinalIgnoreCase);
        var dates = headers["Date"].Take(2).ToArray();
        if (dates.Length != 1) return null;
        string rawDate = InvestigationHeaderValues.Unfold(dates[0].Value).Trim();
        var match = NumericTimeZone().Match(rawDate);
        if (!match.Success || !DateUtils.TryParse(rawDate, out var date) || date.Year < 1900)
            return null;
        var weekday = match.Groups["weekday"];
        if (weekday.Success && !date.DayOfWeek.ToString().StartsWith(weekday.Value, StringComparison.OrdinalIgnoreCase))
            return null;
        issue = ReplyHistoryIssue.From;
        if (ReadAddress("From") is not { } from) return null;
        issue = headers["Reply-To"].Any() ? ReplyHistoryIssue.ReplyTo : ReplyHistoryIssue.MissingReplyTo;
        if (ReadAddress("Reply-To") is not { } reply) return null;
        issue = ReplyHistoryIssue.None;
        return new(record.Id, record.SourceId, date, Observe(dates[0], rawDate), from, reply);

        InvestigationObservation? ReadAddress(string field)
        {
            var matches = headers[field].Take(2).ToArray();
            return InvestigationHeaderValues.SingleAddress(matches) is { } address && InvestigationHeaderValues.AddressKey(address) is { } key
                ? Observe(matches[0], address.Address) with { Key = key } : null;
        }

        InvestigationObservation Observe(MailHeader header, string value) =>
            new(header.Name, value, Location(record, header)) { EvidenceRecordId = record.Id, RawValue = header.Value };
    }

    private static EvidenceLocation Location(MailRecord record, MailHeader header) =>
        new(header.Range, record.TransportHeaderStream, record.TransportHeaderProperty);

    private static void Add<T>(Dictionary<string, List<T>> index, string key, T value)
    {
        if (!index.TryGetValue(key, out var entries)) index.Add(key, entries = []);
        entries.Add(value);
    }
}

