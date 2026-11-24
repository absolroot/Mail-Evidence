namespace MailViewer.Core;

internal static class LinkRules
{
    internal static void Evaluate(MailRecord record, InvestigationEvaluation result, CancellationToken ct)
    {
        foreach (var part in record.BodyParts)
            foreach (var link in part.Links)
            {
                ct.ThrowIfCancellationRequested();
                if (link.DomainComparisonUnavailable) result.Unavailable |= InvestigationClue.LinkDomainMismatch;
                int finding = link.DomainMismatch ? result.BeginFinding(InvestigationClue.LinkDomainMismatch) : 0;
                if (result.CollectEvidence) AddLinkEvidence(link, finding, result);
            }
    }

    private static void AddLinkEvidence(ObservedLink link, int finding, InvestigationEvaluation result)
    {
        string location = $"{link.Part} · {link.Line}:{link.Column}";
        var target = new InvestigationKey(InvestigationKeyKind.Url, link.Key);
        InvestigationKey? displayed = InvestigationArtifacts.TryUrl(link.Display, out string url, out _)
            ? new(InvestigationKeyKind.Url, url) : null;
        result.Add(new("URL", link.Target, link.Location, target) { SourceNote = location });
        result.Add(new("URL host", link.Host, link.Location, new(InvestigationKeyKind.Host, link.Host)));
        if (displayed is not null && displayed != target)
            result.Add(new("Displayed URL", link.Display, link.Location, displayed) { SourceNote = location });
        if (finding == 0) return;
        result.Add(new("Displayed URL", link.Display, link.Location, displayed, InvestigationClue.LinkDomainMismatch, finding)
            { SourceNote = location });
        result.Add(new("Link target", link.Target, link.Location, target, InvestigationClue.LinkDomainMismatch, finding)
            { SourceNote = location });
    }
}
