namespace MailViewer.Core;


internal readonly record struct InvestigationSummary(InvestigationClue Findings, InvestigationClue Unavailable);

internal static class InvestigationRules
{
    internal static InvestigationSummary Summarize(MailRecord record, CancellationToken ct) =>
        Evaluate(record, false, ct).Summary;

    internal static InvestigationReport Inspect(MailRecord record, ReplyHistory? history, CancellationToken ct)
    {
        var result = Evaluate(record, true, ct);
        if (history is not null) ReplyHistoryRule.AddEvidence(history, result);
        return result.ToReport();
    }

    private static InvestigationEvaluation Evaluate(MailRecord record, bool collectEvidence, CancellationToken ct)
    {
        var result = new InvestigationEvaluation(collectEvidence);
        HeaderRules.Evaluate(record, result, ct);
        if (collectEvidence) InvestigationObservations.AddMetadata(record, result, ct);
        LinkRules.Evaluate(record, result, ct);
        AttachmentRules.Evaluate(record, result, ct);
        return result;
    }
}

internal sealed class InvestigationEvaluation(bool collectEvidence)
{
    private readonly List<InvestigationObservation>? observations = collectEvidence ? [] : null;
    private InvestigationClue findings;
    private int findingNumber;
    internal bool CollectEvidence => observations is not null;
    internal InvestigationClue Unavailable { get; set; }
    internal InvestigationSummary Summary => new(findings, Unavailable);

    internal int BeginFinding(InvestigationClue clue)
    {
        findings |= clue;
        return ++findingNumber;
    }

    internal void Add(InvestigationObservation observation) => observations!.Add(observation);

    internal void AddHeader(MailRecord record, MailHeader header, string field, string value,
        InvestigationKey? key = null, InvestigationClue clue = InvestigationClue.None, int finding = 0) =>
        Add(new(field, value, new(header.Range, record.TransportHeaderStream, record.TransportHeaderProperty),
            key, clue, finding));

    internal InvestigationReport ToReport() => new(observations!.AsReadOnly(), Unavailable);
}
