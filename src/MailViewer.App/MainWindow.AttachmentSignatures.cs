using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private byte[]? _rawDecodedBytes;
    private MailAttachment? _rawDecodedAttachment;
    private AttachmentBoundary? _rawAttachmentBoundary;
    private ByteRange? _rawDecodedPeHeader;

    private void OpenFindingDetail(object sender, RoutedEventArgs e)
    {
        if (FindingDetailObservation() is { } observation)
            OpenInvestigationSource(observation, sender as FrameworkElement);
    }

    private async void OpenDecodedAttachment(string attachmentId, AttachmentBoundary? boundary, ByteRange? peHeader,
        FrameworkElement? returnFocus)
    {
        if (_session is not { } session || _activeRow?.Record is not { } record || !CanChooseSources ||
            record.Attachments.FirstOrDefault(attachment => attachment.Id == attachmentId) is not { } attachment)
            return;

        var work = StartWork(L("첨부파일 바이트를 확인하는 중입니다.", "Reading attachment bytes."));
        try
        {
            byte[] bytes = await Task.Run(() => session.ReadAttachment(record.Id, attachment.Id, work.Token), work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session, session) || _activeRow?.Record.Id != record.Id) return;
            _rawReturnTab = InvestigationCluesTab;
            _rawReturnFocus = returnFocus;
            _rawSourceRecord = null;
            SetRawRange(new ByteRange(0, bytes.LongLength), attachment.Name,
                decoded: bytes, decodedAttachment: attachment, boundary: boundary, decodedPeHeader: peHeader);
            _openingEvidenceBytes = true;
            try { DetailTabs.SelectedItem = RawTab; }
            finally { _openingEvidenceBytes = false; }
            RawBytesBox.FocusHexColumn();
            WorkStatus.Text = "";
        }
        catch (OperationCanceledException)
        {
            WorkStatus.Text = L("첨부파일 확인을 취소했습니다.", "Attachment inspection canceled.");
        }
        catch (Exception error) when (IsExpected(error))
        {
            WorkStatus.Text = L($"첨부파일 바이트를 읽지 못했습니다. {error.Message}",
                $"Could not read the attachment bytes. {error.Message}");
        }
        finally { EndWork(work); }
    }

    private void RefreshFindingPreview(InvestigationFinding? finding)
    {
        FindingBytePreview.Inlines.Clear();
        FindingBytePane.Visibility = Visibility.Collapsed;
        EvidenceField[] facts = [];
        string outcome = "";
        if (finding?.Evidence[0].Observation.Boundary is { } boundary)
        {
            (outcome, facts) = BoundaryEvidence(boundary);
            RenderBoundaryBytes(boundary);
        }
        else if (finding?.Evidence[0].Observation.Clue == InvestigationClue.DisguisedAttachmentName)
        {
            facts = FilenameEvidence(finding);
        }

        FindingOutcome.Text = outcome;
        FindingOutcome.Visibility = outcome.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FindingFacts.ItemsSource = facts;
        FindingFacts.Visibility = facts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static EvidenceField[] FilenameEvidence(InvestigationFinding finding)
    {
        var facts = new List<EvidenceField>();
        var direction = finding.Evidence.FirstOrDefault(row => row.Observation.Field == "Direction controls")?.Observation;
        facts.Add(direction is null
            ? new(L("기록된 파일명", "Recorded filename"), finding.Evidence[0].Observation.Value)
            : new(L("제어 문자를 표시한 이름", "Filename with controls shown"), direction.Value));
        if (finding.Evidence.FirstOrDefault(row => row.Observation.Field == "Double extension") is { } extension)
        {
            string filename = extension.Observation.Value;
            string[] parts = filename.TrimEnd(' ', '.').Split('.');
            facts.Add(new(L("앞 확장자", "First extension"), $".{parts[^2]}"));
            facts.Add(new(L("실행·스크립트 확장자", "Executable or script extension"), $".{parts[^1]}"));
        }
        if (direction?.RawValue is { } original)
        {
            var marks = new List<string>();
            int position = 0;
            foreach (var character in original.EnumerateRunes())
            {
                position++;
                if (character.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)
                    marks.Add(L($"{position}번째 문자 U+{character.Value:X4}", $"Character {position}: U+{character.Value:X4}"));
            }
            facts.Add(new(L("방향 제어 문자", "Direction control"), string.Join(" · ", marks)));
        }
        return facts.ToArray();
    }

    private static (string Outcome, EvidenceField[] Facts) BoundaryEvidence(AttachmentBoundary boundary)
    {
        if (boundary.LengthComparison is { } length)
        {
            string field = length.Chunk ?? L("PNG 청크", "PNG chunk");
            return (L($"{field}에 기록된 길이가 파일에 남은 공간을 넘습니다.",
                    $"The declared length of {field} exceeds the space left in the file."),
                [
                    new(L("기록된 데이터 길이", "Declared data length"), L($"{length.Declared:N0}바이트", $"{length.Declared:N0} bytes")),
                    new(L("남은 데이터 공간", "Available data space"), L($"최대 {length.Available:N0}바이트", $"At most {length.Available:N0} bytes"))
                ]);
        }

        return boundary.Kind switch
        {
            AttachmentBoundaryKind.HeaderMismatch =>
                (boundary.ObservedFormat is { } format
                    ? L($"확장자는 {boundary.Format}인데 파일 시작에서 {format} 시그니처가 확인됐습니다.",
                        $"The extension indicates {boundary.Format}, but the file starts with a {format} signature.")
                    : L($"파일 시작에 {boundary.Format} 시그니처가 없습니다.",
                        $"The file start does not contain the expected {boundary.Format} signature."),
                    [new(L("확장자 기준 시작 값", "Expected start signature"), boundary.Expected)]),
            AttachmentBoundaryKind.MissingEnd =>
                (L($"파일 끝까지 {boundary.Format} 종료 구조를 확인하지 못했습니다.",
                    $"The expected {boundary.Format} end structure was not found before the file ended."),
                    [new(L("확인할 종료 구조", "Expected end structure"), boundary.Expected)]),
            AttachmentBoundaryKind.TrailingBytes =>
                (L($"{boundary.Format} 종료 위치 뒤에 {boundary.Range.Length:N0}바이트가 남아 있습니다.",
                    $"{boundary.Range.Length:N0} bytes remain after the {boundary.Format} end."),
                    [new(L("확인한 종료 기준", "End structure found"), boundary.Expected)]),
            _ =>
                (L($"{boundary.Format} 내부 구조에서 아래 값이 기준과 다릅니다.",
                    $"The highlighted value does not meet the expected {boundary.Format} structure."),
                    [new(L("구조 기준", "Expected structure"), boundary.Expected)])
        };
    }

    private void RenderBoundaryBytes(AttachmentBoundary boundary)
    {
        var preview = boundary.Preview;
        if (preview is null) return;
        string address = $"0x{boundary.Range.Offset:X}";
        FindingBytePosition.Text = boundary.Kind switch
        {
            AttachmentBoundaryKind.MissingEnd => L($"디코딩한 첨부 바이트 · 파일 끝 {address}", $"Decoded attachment bytes · end of file {address}"),
            AttachmentBoundaryKind.TrailingBytes => L($"디코딩한 첨부 바이트 · {address}부터 추가", $"Decoded attachment bytes · extra data from {address}"),
            _ => L($"디코딩한 첨부 바이트 · {address}에서 비교", $"Decoded attachment bytes · checked at {address}")
        };
        FindingBytePane.Visibility = Visibility.Visible;
        FindingBytePreview.Visibility = preview.Bytes.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        var bytes = preview.Bytes.Span;
        for (int index = 0; index < bytes.Length; index++)
        {
            long at = preview.Offset + index;
            if (index % 8 == 0)
            {
                if (index != 0) FindingBytePreview.Inlines.Add(new LineBreak());
                var offset = new Run($"0x{at:X8}  ");
                offset.SetResourceReference(TextElement.ForegroundProperty, "Viewer.Muted");
                FindingBytePreview.Inlines.Add(offset);
            }
            var token = new Run($"{bytes[index]:X2}");
            if (at >= boundary.Range.Offset && at < boundary.Range.End)
            {
                token.FontWeight = FontWeights.Bold;
                token.SetResourceReference(TextElement.ForegroundProperty, "Viewer.WarningText");
                token.SetResourceReference(TextElement.BackgroundProperty, "Viewer.WarningFill");
            }
            FindingBytePreview.Inlines.Add(token);
            if (index % 8 != 7 && index + 1 < bytes.Length) FindingBytePreview.Inlines.Add(new Run(" "));
        }
    }

    private static string BoundaryTitle(AttachmentBoundaryKind kind) => kind switch
    {
        AttachmentBoundaryKind.HeaderMismatch => L("시작 시그니처 불일치", "Header signature mismatch"),
        AttachmentBoundaryKind.StructureMismatch => L("내부 구조 불일치", "Internal structure mismatch"),
        AttachmentBoundaryKind.MissingEnd => L("종료 구조 미확인", "End structure not found"),
        _ => L("종료 뒤 추가 바이트", "Extra bytes after the end")
    };

    private static string DescribeBoundary(AttachmentBoundary boundary)
    {
        string position = $"0x{boundary.Range.Offset:X}";
        string observed = boundary.Observed.Length == 0 ? L("바이트 없음", "No bytes") : boundary.Observed;
        return boundary.Kind switch
        {
            AttachmentBoundaryKind.HeaderMismatch => L(
                $"시작 {position}의 바이트가 {boundary.Format} 헤더와 다릅니다. 기대값 {boundary.Expected} · 관측값 {observed}",
                $"The filename suggests {boundary.Format}. At the start ({position}), expected {boundary.Expected}. Observed {observed}."),
            AttachmentBoundaryKind.StructureMismatch => L(
                $"{boundary.Format}의 {position}에서 구조가 어긋납니다. 기대값 {boundary.Expected} · 관측값 {observed}",
                $"At {position}, the {boundary.Format} file contains {observed}, which is inconsistent with {boundary.Expected}."),
            AttachmentBoundaryKind.MissingEnd => L(
                $"{boundary.Format}에서 파일 끝 {position}까지 {boundary.Expected} 종료 구조를 확인하지 못했습니다.",
                $"The expected {boundary.Format} end structure ({boundary.Expected}) was not found by the end of the file at {position}."),
            _ => L($"{boundary.Format}의 {boundary.Expected} 뒤에 {position}부터 {boundary.Range.Length:N0}바이트가 남아 있습니다.",
                $"After the {boundary.Format} end structure ({boundary.Expected}), {boundary.Range.Length:N0} extra bytes start at {position}.")
        };
    }
}

