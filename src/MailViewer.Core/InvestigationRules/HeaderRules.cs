using System.Globalization;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;
using static MailViewer.Core.InvestigationHeaderValues;

namespace MailViewer.Core;

internal static partial class HeaderRules
{
    private static readonly TimeSpan MinimumRelayReversal = TimeSpan.FromSeconds(30);
    private static readonly string[] SingleHeaders = ["From", "Sender", "Reply-To", "Date", "Message-ID"];
    private static readonly InvestigationClue HeaderClues = InvestigationClue.DuplicateHeader |
        InvestigationClue.ReplyDomainMismatch | InvestigationClue.ReceivedTimeReversal;

    [GeneratedRegex(@"(?<!\S)(?:\+\d{4}|-(?!0000\b)\d{4})(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();

    internal static void Evaluate(MailRecord record, InvestigationEvaluation result, CancellationToken ct)
    {
        if (record.Headers.Count == 0 || record.TransportHeaderState is HeaderEvidenceState.Unreadable
            or HeaderEvidenceState.Empty or HeaderEvidenceState.Missing)
            result.Unavailable |= HeaderClues;

        var byName = record.Headers.ToLookup(header => header.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string name in SingleHeaders)
        {
            if (!byName[name].Skip(1).Any()) continue;
            int finding = result.BeginFinding(InvestigationClue.DuplicateHeader);
            if (result.CollectEvidence)
                foreach (var header in byName[name])
                    result.AddHeader(record, header, name, header.Value.Trim(),
                        clue: InvestigationClue.DuplicateHeader, finding: finding);
        }

        CheckReplyDomain(record, byName, result);

        (MailHeader Header, string Raw, DateTimeOffset Date)? previous = null;
        foreach (var header in byName["Received"].Reverse())
        {
            ct.ThrowIfCancellationRequested();
            int separator = -1;
            foreach (int point in TopLevelSemicolons(header.Value)) separator = point;
            string raw = separator >= 0 ? Unfold(header.Value[(separator + 1)..]).Trim() : "";
            if (!OffsetPattern().IsMatch(raw) || !DateUtils.TryParse(raw, out var date))
            {
                result.Unavailable |= InvestigationClue.ReceivedTimeReversal;
                previous = null;
                if (result.CollectEvidence) result.AddHeader(record, header, "Received", header.Value.Trim());
                continue;
            }
            if (result.CollectEvidence) result.AddHeader(record, header, "Received", raw);
            if (previous is { } earlier && earlier.Date - date >= MinimumRelayReversal)
            {
                int finding = result.BeginFinding(InvestigationClue.ReceivedTimeReversal);
                if (result.CollectEvidence)
                {
                    string difference = (date - earlier.Date).ToString("c", CultureInfo.InvariantCulture);
                    result.AddHeader(record, earlier.Header, "Received", earlier.Raw,
                        clue: InvestigationClue.ReceivedTimeReversal, finding: finding);
                    result.AddHeader(record, header, "Received", $"{raw} ({difference})",
                        clue: InvestigationClue.ReceivedTimeReversal, finding: finding);
                }
            }
            previous = (header, raw, date);
        }
    }

    private static void CheckReplyDomain(MailRecord record, ILookup<string, MailHeader> headers, InvestigationEvaluation result)
    {
        var replies = headers["Reply-To"];
        int replyCount = replies.Count();
        if (replyCount == 0) return;

        if (replyCount != 1 || headers["From"].Count() != 1 ||
            SingleAddress(headers["From"].First().Value) is not { } sender ||
            SingleAddress(replies.First().Value) is not { } response ||
            AddressHost(sender) is not { } senderHost || AddressHost(response) is not { } replyHost)
        {
            result.Unavailable |= InvestigationClue.ReplyDomainMismatch;
            return;
        }

        if (senderHost == replyHost || Uri.CheckHostName(senderHost) != UriHostNameType.Dns ||
            Uri.CheckHostName(replyHost) != UriHostNameType.Dns || HasListReply(headers, response, replyHost)) return;

        int finding = result.BeginFinding(InvestigationClue.ReplyDomainMismatch);
        if (!result.CollectEvidence) return;
        result.AddHeader(record, headers["From"].First(), "From", sender.Address, AddressKey(sender, senderHost),
            InvestigationClue.ReplyDomainMismatch, finding);
        result.AddHeader(record, replies.First(), "Reply-To", response.Address, AddressKey(response, replyHost),
            InvestigationClue.ReplyDomainMismatch, finding);
    }

    private static bool HasListReply(ILookup<string, MailHeader> headers, MailboxAddress reply, string replyHost)
    {
        var identifiers = headers["List-Id"].Take(2).ToArray();
        var posts = headers["List-Post"].Take(2).ToArray();
        if (identifiers.Length != 1 || posts.Length != 1) return false;

        string identifier = Unfold(identifiers[0].Value).Trim();
        int bracket = identifier.LastIndexOf('<');
        if (bracket < 0 || !identifier.EndsWith('>') ||
            InvestigationArtifacts.Host(identifier[(bracket + 1)..^1]) is not { } listHost ||
            (listHost != replyHost && !listHost.EndsWith("." + replyHost, StringComparison.Ordinal)))
            return false;

        string post = Unfold(posts[0].Value).Trim();
        const string mailto = "<mailto:";
        return post.StartsWith(mailto, StringComparison.OrdinalIgnoreCase) && post.EndsWith('>') &&
            TryMailbox(post[mailto.Length..^1], out var listAddress) &&
            AddressKey(listAddress) == AddressKey(reply);
    }
}
