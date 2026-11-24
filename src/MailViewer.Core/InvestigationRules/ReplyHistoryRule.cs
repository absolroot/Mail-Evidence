namespace MailViewer.Core;

internal static class ReplyHistoryRule
{
    internal static InvestigationClue Flag(ReplyHistory history) =>
        history.Status == ReplyHistoryStatus.Changed && history.Current is not null && history.Earlier is not null
            ? InvestigationClue.ReplyAddressChanged : InvestigationClue.None;

    internal static void AddEvidence(ReplyHistory history, InvestigationEvaluation result)
    {
        InvestigationClue clue = Flag(history);
        if (clue == InvestigationClue.None) return;

        var current = history.Current!;
        var earlier = history.Earlier!;
        int finding = result.BeginFinding(clue);
        result.Add(current.ReplyTo with { Field = "Current Reply-To", Clue = clue, Finding = finding });
        result.Add(current.DateHeader with { Field = "Current Date", Clue = clue, Finding = finding });
        result.Add(earlier.ReplyTo with { Field = "Earlier Reply-To", Clue = clue, Finding = finding });
        result.Add(earlier.DateHeader with { Field = "Earlier Date", Clue = clue, Finding = finding });
        foreach (var reference in history.Basis!)
            result.Add(new(reference.Header.Name, reference.MessageId, reference.Location,
                new(InvestigationKeyKind.MessageId, reference.MessageId), clue, finding));
    }
}
