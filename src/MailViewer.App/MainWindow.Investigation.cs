using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private readonly Dictionary<InvestigationClue, CheckBox> _investigationChoices = [];
    private readonly Stack<InvestigationPosition> _investigationBack = [];
    private InvestigationKey? _exactSearch;

    private sealed record InvestigationPosition(SearchRequest Applied, SearchRequest Draft,
        string? RecordId, TabItem? Tab, InvestigationObservation? Observation, double Vertical, double Horizontal,
        IInputElement? Focus, bool ReaderExpanded, MailInvestigationField? MailField, TabItem MailboxTab, ConversationPosition? Conversation);
    private sealed record InvestigationRow(string Field, InvestigationObservation Observation, string Tooltip);
    private sealed record InvestigationFinding(int Id, string Title, string Target, InvestigationRow[] Evidence);

    private void InitializeInvestigationFilters()
    {
        InvestigationGrid.ContextMenu = EvidenceMenu(() => (InvestigationGrid.SelectedItem as InvestigationRow)?.Observation);
        InvestigationGrid.SelectionChanged += (_, _) => RefreshFindingSourceButton();
        InvestigationGrid.PreviewMouseRightButtonDown += (_, args) =>
        {
            if (ItemsControl.ContainerFromElement(InvestigationGrid, args.OriginalSource as DependencyObject) is DataGridRow row)
                InvestigationGrid.SelectedItem = row.Item;
        };
        BindAddress(EarlierReplyValue, "Earlier Reply-To");
        BindAddress(CurrentReplyValue, "Current Reply-To");
        foreach (var clue in Enum.GetValues<InvestigationClue>().Where(clue => clue != InvestigationClue.None))
        {
            var choice = new CheckBox { Content = ClueName(clue), Margin = new Thickness(0, 2, 0, 2) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(choice, $"Clue{clue}");
            _investigationChoices.Add(clue, choice);
            InvestigationFilterChoices.Children.Add(choice);
            choice.Checked += FilterChanged;
            choice.Unchecked += FilterChanged;
        }

        void BindAddress(TextBox box, string field)
        {
            box.ContextMenu = EvidenceMenu(() => ReplyAddressRow(field)?.Observation);
            box.GotKeyboardFocus += (_, _) =>
            {
                if (ReplyAddressRow(field) is { } row) InvestigationGrid.SelectedItem = row;
            };
        }
    }

    private string ClueName(InvestigationClue clue) => clue switch
    {
        InvestigationClue.DuplicateHeader => L("단일 헤더 중복", "Repeated single-instance header"),
        InvestigationClue.ReplyDomainMismatch => L("발신·회신 도메인 차이", "From and Reply-To domain difference"),
        InvestigationClue.ReceivedTimeReversal => L("경유 기록의 시각 역전", "Reversed relay timestamps"),
        InvestigationClue.LinkDomainMismatch => L("HTML 링크의 도메인 차이", "HTML link domain difference"),
        InvestigationClue.DisguisedAttachmentName => L("첨부 이름의 위장 징후", "Attachment filename disguise"),
        InvestigationClue.AttachmentPeMismatch => L("첨부 이름과 PE 형식 불일치", "Filename and PE format mismatch"),
        InvestigationClue.AttachmentBoundaryMismatch => L("첨부파일 시작·끝 구조 이상", "Attachment signature or boundary difference"),
        InvestigationClue.ReplyAddressChanged => L("대화 내 회신 주소 변경", "Reply-To change in conversation"),
        _ => ""
    };

    private InvestigationClue CaptureInvestigationFilters()
    {
        var result = InvestigationClue.None;
        foreach (var (clue, control) in _investigationChoices)
            if (control.IsChecked == true) result |= clue;
        return result;
    }

    private void SetInvestigationFilters(InvestigationClue clues)
    {
        foreach (var (clue, control) in _investigationChoices)
            control.IsChecked = (clues & clue) != 0;
    }

    private void FilterInvestigationChoices(object sender, TextChangedEventArgs e) => RefreshInvestigationChoices();

    private void RefreshInvestigationChoices()
    {
        if (InvestigationFilterQuery is null || InvestigationFilterEmpty is null) return;
        string query = InvestigationFilterQuery.Text.Trim();
        bool any = false;
        foreach (var (clue, control) in _investigationChoices)
        {
            bool matches = ClueName(clue).Contains(query, StringComparison.CurrentCultureIgnoreCase);
            control.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            any |= matches;
        }
        InvestigationFilterEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshInvestigationCommands()
    {
        var count = _investigationChoices.Count(pair => pair.Value.IsChecked == true);
        InvestigationFilters.ToolTip = L("선택한 조건 중 하나 이상에 적중한 메일을 표시합니다.",
                "Show messages that match at least one selected clue.");
        InvestigationFilters.Header = count == 0 ? L("모든 유형", "Any anomaly type") :
            L($"{count}개 유형 선택", $"{count} selected");
        foreach (var (clue, control) in _investigationChoices) control.Content = ClueName(clue);
        if (InvestigationFilterQuery.Text.Length > 0) RefreshInvestigationChoices();
        InvestigationSearchBack.Content = L("이전 결과", "Previous results");
        InvestigationSearchBack.Visibility = _investigationBack.Count > 0 && _exactSearch is not null
            ? Visibility.Visible : Visibility.Collapsed;
        InvestigationSearchBack.IsEnabled = CanChooseSources;
        FindRelatedMailMenuItem.Header = L("메일의 주소·URL·해시·ID로 검색…", "Search by message address, URL, hash or ID…");
        FindRelatedMailMenuItem.IsEnabled = CanChooseSources && _activeRow is not null;
        if (_conversationRecordId is not null && _mailInfoTab?.Content is FrameworkElement conversation)
            conversation.IsEnabled = CanChooseSources;
    }

    private void RefreshInvestigation()
    {
        var previous = (InvestigationGrid.SelectedItem as InvestigationRow)?.Observation;
        InvestigationFindings.Columns[0].Header = L("이상 징후", "Anomaly");
        InvestigationFindings.Columns[1].Header = L("대상", "Affected item");
        InvestigationGrid.Columns[0].Header = L("필드", "Field");
        InvestigationGrid.Columns[1].Header = L("관측값", "Observed value");
        InvestigationFinding[] findings = [];
        ClueStatus.Text = "";
        if (_activeRow?.Record is { } record && _session is { } session)
        {
            var report = session.Inspect(record.Id);
            findings = report.Observations.Where(item => item.Finding > 0)
                .GroupBy(item => item.Finding)
                .Select(group => new InvestigationFinding(group.Key, FindingTitle(group), FindingTarget(group),
                    group.Select((item, index) => new InvestigationRow(FindingField(item, index), item,
                        EvidenceTooltip(session, item))).ToArray())).ToArray();
            var status = new List<string>();
            if (findings.Length == 0)
            {
                status.Add(L("확인한 범위에서 이상 징후를 찾지 못했습니다.", "No anomalies found in the examined data."));
                var reply = session.CompareReplyHistory(record.Id);
                if (reply.Status != ReplyHistoryStatus.NoReference) status.Add(ReplyComparisonText(reply));
            }
            if (report.Unavailable != InvestigationClue.None || session.Issues.Any(issue => issue.RecordId == record.Id))
                status.Add(L("일부 항목을 확인할 수 없습니다.", "Some checks could not be completed."));
            ClueStatus.Text = string.Join(" ", status);
        }
        InvestigationCluesTab.Header = L($"이상 징후 ({findings.Length:N0})", $"Anomalies ({findings.Length:N0})");
        System.Windows.Automation.AutomationProperties.SetName(InvestigationCluesTab,
            L($"선택한 메일의 이상 징후 {findings.Length:N0}건", $"Anomalies on selected message ({findings.Length:N0})"));
        bool single = findings.Length == 1;
        SingleFindingTitle.Text = single ? findings[0].Title : "";
        SingleFindingTitle.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        bool showSingleTarget = single && findings[0].Evidence[0].Observation.Boundary is not null;
        SingleFindingTarget.Text = showSingleTarget ? findings[0].Target : "";
        SingleFindingTarget.Visibility = showSingleTarget ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(SingleFindingTarget, L("첨부파일", "Attachment"));
        InvestigationFindings.Visibility = findings.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(InvestigationFindings, L("이상 징후 선택", "Select anomaly"));
        ClueStatus.Visibility = ClueStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        InvestigationFindings.ItemsSource = findings;
        SelectInvestigationObservation(previous);
        RefreshReplyComparison();
    }

    private static string EvidenceTooltip(EvidenceSession session, InvestigationObservation item)
    {
        if (item.EvidenceRecordId is { } id)
            return $"{item.Value}\n{session.GetRecord(id).SourcePath}";
        return item.SourceNote is { } note ? $"{item.Value}\n{DecodedBodyLocation(note)}" : item.Value;
    }

    private string FindingTitle(IEnumerable<InvestigationObservation> items)
    {
        var first = items.First();
        if (first.Boundary is { } boundary) return BoundaryTitle(boundary.Kind);
        return first.Clue switch
        {
            InvestigationClue.DuplicateHeader => L($"{first.Field} 헤더 {items.Count()}개", $"{items.Count()} {first.Field} headers"),
            InvestigationClue.DisguisedAttachmentName when items.Count() > 1 =>
                L("이중 확장자 및 방향 제어 문자", "Double extension and direction controls"),
            InvestigationClue.DisguisedAttachmentName when first.Field == "Double extension" =>
                L("이중 확장자", "Double extension"),
            InvestigationClue.DisguisedAttachmentName =>
                L("파일명에 방향 제어 문자 포함", "Direction controls in filename"),
            _ => ClueName(first.Clue)
        };
    }

    private static string FindingTarget(IGrouping<int, InvestigationObservation> items)
    {
        var first = items.First();
        return first.Clue switch
        {
            InvestigationClue.DuplicateHeader => first.Field,
            InvestigationClue.ReplyDomainMismatch => items.First(item => item.Field == "Reply-To").Value,
            InvestigationClue.ReplyAddressChanged => items.First(item => item.Field == "Current Reply-To").Value,
            InvestigationClue.LinkDomainMismatch => items.First(item => item.Field == "Link target").Value,
            InvestigationClue.ReceivedTimeReversal => items.Last().Value,
            _ => first.Value
        };
    }

    private string ClueField(InvestigationObservation item) =>
        item.Field switch
        {
            "Last reference" => L("마지막 참조", "Last reference"),
            "Displayed URL" => L("링크에 표시된 URL", "Displayed URL"),
            "URL host" => L("URL 호스트", "URL host"),
            "Link target" => L("실제 연결 주소", "Link target"),
            "From" => L("발신 주소", "From"),
            "Reply-To" => L("회신 주소", "Reply-To"),
            "Current Reply-To" => L("현재 메일의 회신 주소", "Current Reply-To"),
            "Current Date" => L("현재 메일의 발신 시각", "Current Date header"),
            "Earlier Reply-To" => L("이전 메일의 회신 주소", "Earlier Reply-To"),
            "Earlier Date" => L("이전 메일의 발신 시각", "Earlier Date header"),
            "Double extension" => L("이중 확장자", "Double extension"),
            "Direction controls" => L("문자 방향 제어", "Direction controls"),
            "Decoded PE header" => L("디코딩한 PE 헤더", "Decoded PE header"),
            "Attachment signature" => L("첨부파일", "Attachment"),
            _ => item.Field
        };

    private string FindingField(InvestigationObservation item, int index) => item.Clue switch
    {
        InvestigationClue.DuplicateHeader => L($"{item.Field} · {index + 1}번째 값", $"{item.Field} · value {index + 1}"),
        InvestigationClue.ReceivedTimeReversal => index == 0 ? L("앞 경유 기록", "Previous hop") : L("다음 경유 기록", "Next hop"),
        InvestigationClue.AttachmentPeMismatch when item.Field != "Decoded PE header" => L("기록된 파일명", "Recorded filename"),
        _ => ClueField(item)
    };

    private void OnFindingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (InvestigationGrid is null) return;
        FindingCriteria.IsExpanded = false;
        var finding = InvestigationFindings.SelectedItem as InvestigationFinding;
        InvestigationGrid.ItemsSource = finding?.Evidence;
        InvestigationGrid.SelectedIndex = finding is { Evidence.Length: > 0 } ? 0 : -1;
        RefreshReplyComparison();
    }

    private void SelectInvestigationObservation(InvestigationObservation? observation)
    {
        if (InvestigationFindings.ItemsSource is not InvestigationFinding[] findings) return;
        var finding = findings.FirstOrDefault(group => group.Id == observation?.Finding) ?? findings.FirstOrDefault();
        InvestigationFindings.SelectedItem = finding;
        if (finding is not null)
            InvestigationGrid.SelectedItem = finding.Evidence.FirstOrDefault(row => row.Observation == observation)
                ?? finding.Evidence.FirstOrDefault();
    }

    private void OnInvestigationEvidenceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (InvestigationGrid.SelectedItem as InvestigationRow)?.Observation is not { } row) return;
        OpenInvestigationSource(row);
        e.Handled = true;
    }

    private InvestigationRow? ReplyAddressRow(string field) =>
        (InvestigationFindings.SelectedItem as InvestigationFinding)?.Evidence
            .FirstOrDefault(row => row.Observation.Field == field);

    private ContextMenu EvidenceMenu(Func<InvestigationObservation?> read)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            if (read() is not { } row) return;
            var copy = new MenuItem { Header = L("값 복사", "Copy value") };
            copy.Click += (_, _) => CopyEvidenceText(menu.PlacementTarget is TextBox { SelectionLength: > 0 } box
                ? box.SelectedText : row.Value);
            menu.Items.Add(copy);

            if (row.Location is { Range.Length: > 0 } || row.DecodedAttachmentId is not null)
            {
                var bytes = new MenuItem { Header = row.DecodedAttachmentId is null
                    ? L("원본 바이트 탭에서 보기", "View in Source bytes") : L("첨부 HEX 보기", "View attachment hex"), IsEnabled = _work is null };
                System.Windows.Automation.AutomationProperties.SetAutomationId(bytes, "ViewEvidenceBytes");
                bytes.Click += (_, _) =>
                {
                    SelectInvestigationObservation(row);
                    OpenInvestigationSource(row, menu.PlacementTarget as FrameworkElement);
                };
                menu.Items.Add(bytes);
            }

            if (row.Key is { } key)
            {
                var search = new MenuItem
                {
                    Header = key.Kind switch
                    {
                        InvestigationKeyKind.Address => L("이 메일 주소로 검색", "Search by this email address"),
                        InvestigationKeyKind.Host => L("이 호스트로 검색", "Search by this host"),
                        InvestigationKeyKind.Url => L("이 URL로 검색", "Search by this URL"),
                        InvestigationKeyKind.AttachmentSha256 => L("이 첨부 SHA-256으로 검색", "Search by this attachment SHA-256"),
                        InvestigationKeyKind.MessageId => L("이 메시지 ID로 검색", "Search by this message ID"),
                        _ => L("이 메일 검색", "Find this message")
                    },
                    IsEnabled = CanChooseSources
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(search, "SearchEvidenceValue");
                search.Click += async (_, _) =>
                {
                    menu.PlacementTarget?.Focus();
                    await RunInvestigationSearch(key);
                };
                menu.Items.Add(search);
            }
        };
        return menu;
    }

    private void OpenInvestigationSource(InvestigationObservation row, FrameworkElement? returnFocus = null)
    {
        if (_work is not null) return;
        if (row.DecodedAttachmentId is { } attachmentId && (row.Boundary is not null || row.DecodedPeHeader is not null))
        {
            OpenDecodedAttachment(attachmentId, row.Boundary, row.DecodedPeHeader, returnFocus);
            return;
        }
        if (row.Location is not { Range.Length: > 0 } location) return;
        string label = row.SourceNote is { } note ? $"{ClueField(row)} · {DecodedBodyLocation(note)}" : ClueField(row);
        var sourceRecord = row.EvidenceRecordId is { } id ? _session?.GetRecord(id) : null;
        OpenEvidenceBytes(InvestigationCluesTab, location.Range, label, location.Stream, location.Property,
            sourceRecord: sourceRecord, returnFocus: returnFocus);
    }

    private void RefreshReplyComparison()
    {
        if (ReplyComparisonPane is null) return;
        var finding = InvestigationFindings.SelectedItem as InvestigationFinding;
        var observation = finding?.Evidence[0].Observation;
        FindingSummary.Visibility = finding is null ? Visibility.Collapsed : Visibility.Visible;
        FindingDescription.Text = finding is null ? "" : DescribeFinding(finding);
        FindingCriteria.Header = L("판독 기준", "How this was checked");
        FindingCriteria.Visibility = FindingDescription.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshFindingPreview(finding);
        RefreshFindingSourceButton();
        var reply = observation?.Clue == InvestigationClue.ReplyAddressChanged &&
            _activeRow?.Record is { } record ? _session?.CompareReplyHistory(record.Id) : null;
        bool show = reply is { Earlier: not null, Current: not null };
        ReplyComparisonPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            EarlierReplyLabel.Text = L("이전 회신 주소", "Earlier Reply-To");
            CurrentReplyLabel.Text = L("현재 회신 주소", "Current Reply-To");
            EarlierReplyValue.Text = reply!.Earlier!.ReplyTo.Value;
            CurrentReplyValue.Text = reply.Current!.ReplyTo.Value;
            System.Windows.Automation.AutomationProperties.SetName(EarlierReplyValue, EarlierReplyLabel.Text);
            System.Windows.Automation.AutomationProperties.SetName(CurrentReplyValue, CurrentReplyLabel.Text);
            var earlierRecord = _session!.GetRecord(reply.Earlier.RecordId);
            string title = ConversationSubject(earlierRecord);
            ReplyComparisonScope.Text = L($"비교 메일 · {title} · {reply.Earlier.DateHeader.Value}",
                $"Compared message · {title} · {reply.Earlier.DateHeader.Value}");
            if (reply.UnresolvedReferenceCount > 0)
                ReplyComparisonScope.Text += L($" · 비교 불가 참조 {reply.UnresolvedReferenceCount:N0}개",
                    $" · {reply.UnresolvedReferenceCount:N0} references could not be compared");
            ReplyEvidenceToggle.Header = L("비교 근거", "Comparison evidence");
        }
        if (show && ReplyEvidenceToggle.Content is null)
        {
            InvestigationEvidenceSlot.Children.Remove(InvestigationEvidenceBody);
            ReplyEvidenceToggle.Content = InvestigationEvidenceBody;
        }
        else if (!show && ReplyEvidenceToggle.Content is not null)
        {
            ReplyEvidenceToggle.Content = null;
            InvestigationEvidenceSlot.Children.Add(InvestigationEvidenceBody);
        }
        ReplyEvidenceToggle.Visibility = ReplyComparisonPane.Visibility;
        bool inlineFilename = observation?.Clue == InvestigationClue.DisguisedAttachmentName;
        InvestigationEvidenceSlot.Visibility = finding is not null && observation?.Boundary is null && !inlineFilename
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private InvestigationObservation? FindingDetailObservation()
    {
        if (InvestigationFindings.SelectedItem is not InvestigationFinding finding) return null;
        return finding.Evidence.FirstOrDefault(item => item.Observation.DecodedAttachmentId is not null)?.Observation
            ?? (InvestigationGrid.SelectedItem as InvestigationRow)?.Observation;
    }

    private void RefreshFindingSourceButton()
    {
        if (OpenFindingSourceButton is null) return;
        var observation = FindingDetailObservation();
        bool decoded = observation?.DecodedAttachmentId is not null;
        OpenFindingSourceButton.Visibility = decoded || observation?.Location is { Range.Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        OpenFindingSourceButton.Content = decoded ? L("첨부 HEX 자세히 보기", "Open full attachment hex")
            : observation?.Clue == InvestigationClue.DisguisedAttachmentName ? L("파일명 원본 보기", "View filename source")
            : L("선택한 값의 원본 보기", "View source of selected value");
    }

    private void ResizeReplyComparison(object sender, SizeChangedEventArgs e) =>
        ReplyComparisonColumns.Columns = e.NewSize.Width < 560 ? 1 : 2;

    private static string DescribeFinding(InvestigationFinding finding) => finding.Evidence[0].Observation.Clue switch
    {
        InvestigationClue.ReplyAddressChanged => L(
            "이 메일이 참조한 직전 30일의 메일 중에서 발신 주소(From)가 같고 비교할 수 있는 가장 최근 메일을 골랐습니다. 두 메일의 회신 주소가 다릅니다.",
            "The Reply-To differs from the most recent comparable referenced message sent from the same From address in the previous 30 days."),
        InvestigationClue.DuplicateHeader => L(
            $"한 번만 기록되는 {finding.Evidence[0].Observation.Field} 헤더가 이 메일에 {finding.Evidence.Length:N0}번 나타납니다.",
            $"The single-instance {finding.Evidence[0].Observation.Field} header appears {finding.Evidence.Length:N0} times in this message."),
        InvestigationClue.ReplyDomainMismatch => L(
            "발신 주소와 회신 주소의 @ 뒤 도메인이 다릅니다. 고객지원이나 메일 발송 서비스에서도 이런 차이가 생길 수 있습니다.",
            "The domains after @ in the From and Reply-To addresses differ. Support and mailing services can also use separate domains."),
        InvestigationClue.ReceivedTimeReversal => L(
            "나중 경유 기록의 시각이 바로 앞 경유 기록보다 30초 이상 빠릅니다. 메일 서버의 시계 차이로도 발생할 수 있습니다.",
            "A later mail hop is timestamped at least 30 seconds before the preceding hop. Server clocks can also differ."),
        InvestigationClue.LinkDomainMismatch => L(
            "메일에 표시된 주소와 실제 연결 주소의 등록 도메인이나 IP 주소가 다릅니다. 추적용 링크에서도 이런 차이가 생길 수 있습니다.",
            "The displayed address and link target have different registrable domains or IP addresses. Tracking links can also cause this difference."),
        InvestigationClue.DisguisedAttachmentName => string.Join(" ", finding.Evidence.Select(item => item.Observation.Field switch
        {
            "Double extension" => L("문서나 이미지 확장자 뒤에 실행 파일 또는 스크립트 확장자가 붙어 있습니다.",
                "An executable or script extension follows a document or image extension."),
            _ => L("첨부파일 이름에 문자 표시 순서를 바꾸는 제어 문자가 들어 있습니다.",
                "The attachment filename contains characters that can change its display order.")
        })),
        InvestigationClue.AttachmentBoundaryMismatch => L(
            "파일 이름의 확장자에 따라 시작 시그니처와 확인 가능한 내부·종료 구조를 검사합니다. 위치는 디코딩한 첨부파일의 시작을 기준으로 표시합니다.",
            "The filename extension determines which signature and supported file structures are checked. Offsets are relative to the decoded attachment."),
        InvestigationClue.AttachmentPeMismatch => L(
            "파일 이름에는 문서나 이미지 확장자가 붙어 있지만, 디코딩한 첨부 바이트에서 Windows 실행 형식(PE)의 헤더가 확인됐습니다.",
            "The filename has a document or image extension, but the decoded attachment contains a Windows executable (PE) header."),
        _ => ""
    };

    private static string DecodedBodyLocation(string note) =>
        L($"디코딩한 본문 위치 {note}", $"Decoded body location {note}");

    private sealed record RelatedValue(InvestigationKey Key, string Label);

    private async void FindRelatedMail(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || _activeRow?.Record is not { } record || !CanChooseSources) return;
        var values = session.Inspect(record.Id).Observations.Where(row => row.Key is not null &&
                (row.EvidenceRecordId is null || row.EvidenceRecordId == record.Id))
            .DistinctBy(row => (row.Key, row.Field))
            .Select(row => new RelatedValue(row.Key!, $"{ClueField(row)}  ·  {row.Key!.Value}")).ToArray();
        var dialog = new Window
        {
            Owner = this, Title = L("메일의 주소·URL·해시·ID로 검색", "Search by message address, URL, hash or ID"), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 560, Height = 410, MinWidth = 370, MinHeight = 260, ShowInTaskbar = false, Language = Language
        };
        Appearance.ApplyToWindow(dialog);
        dialog.SetResourceReference(BackgroundProperty, "Viewer.Canvas");
        dialog.SetResourceReference(ForegroundProperty, "Viewer.Text");
        var panel = new DockPanel { Margin = new Thickness(14) };
        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(filter, L("값 좁히기", "Narrow values"));
        filter.ToolTip = L("주소, URL, 해시 또는 메시지 ID로 목록 좁히기", "Narrow addresses, URLs, hashes or message IDs");
        DockPanel.SetDock(filter, Dock.Top);
        panel.Children.Add(filter);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = L("닫기", "Close"), IsCancel = true, Style = (Style)FindResource("Action") };
        var find = new Button { Content = L("찾기", "Find"), IsDefault = true, Style = (Style)FindResource("PrimaryAction"),
            Margin = new Thickness(6, 0, 0, 0) };
        controls.Children.Add(cancel);
        controls.Children.Add(find);
        DockPanel.SetDock(controls, Dock.Bottom);
        panel.Children.Add(controls);
        var list = new ListBox
        {
            DisplayMemberPath = nameof(RelatedValue.Label), Style = (Style)FindResource("EvidenceList")
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(list, "RelatedMailValues");
        list.SetResourceReference(BackgroundProperty, "Viewer.Canvas");
        list.SetResourceReference(ForegroundProperty, "Viewer.Text");
        list.SetResourceReference(BorderBrushProperty, "Viewer.Rule");
        var empty = new TextBlock { Text = L("찾을 값이 없습니다.", "No matching values."), Margin = new Thickness(6) };
        var content = new Grid();
        content.Children.Add(list);
        content.Children.Add(empty);
        panel.Children.Add(content);
        dialog.Content = panel;
        list.SelectionChanged += (_, _) => find.IsEnabled = list.SelectedItem is not null;
        filter.TextChanged += (_, _) => RefreshValues();
        find.Click += (_, _) => Accept();
        list.MouseDoubleClick += (_, _) => Accept();
        dialog.Loaded += (_, _) => filter.Focus();
        RefreshValues();
        if (dialog.ShowDialog() == true && list.SelectedItem is RelatedValue value)
            await RunInvestigationSearch(value.Key);
        return;

        void RefreshValues()
        {
            var matching = values.Where(value => value.Label.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = matching;
            list.SelectedIndex = matching.Length == 0 ? -1 : 0;
            empty.Visibility = matching.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            find.IsEnabled = matching.Length > 0;
        }

        void Accept()
        {
            if (list.SelectedItem is RelatedValue) dialog.DialogResult = true;
        }
    }

    private async Task RunInvestigationSearch(InvestigationKey key, bool openReader = false)
    {
        if (_session is null || _result is null) return;
        var previous = CaptureInvestigationPosition();
        var request = new SearchRequest { Exact = key };
        if (!await SearchAsync(request)) return;

        if (ActiveMailboxTab != InvestigationTab)
        {
            _mailboxViews[ActiveMailboxTab] = new MailboxView(previous, _investigationBack.ToArray());
            AttachMailboxToTab(InvestigationTab);
        }
        _investigationBack.Push(previous);
        RestoreInvestigationInputs(request);
        RevealMailboxListForSearch();
        UpdateCommands();
        if (openReader && _activeRow is not null)
        {
            DetailTabs.SelectedItem = BodyTab;
            ShowMailboxReader();
        }
        else InvestigationSearchBack.Focus();
    }

    private async void ReturnFromInvestigationSearch(object sender, RoutedEventArgs e)
    {
        if (!_investigationBack.TryPeek(out var previous)) return;
        if (!await SearchAsync(previous.Applied)) return;

        _investigationBack.Pop();
        if (ActiveMailboxTab != previous.MailboxTab)
            AttachMailboxToTab(previous.MailboxTab);
        RestoreInvestigationPosition(previous);
        UpdateCommands();
    }

    private void RestoreInvestigationInputs(SearchRequest request)
    {
        _exactSearch = request.Exact;
        SearchSourceBox.SelectedItem = SearchSourceBox.Items.Cast<Choice<string?>>()
            .FirstOrDefault(option => option.Value == request.SourceId);
        RestorePstTreeSelection(request);
        SearchFieldBox.SelectedIndex = (int)request.Field;
        RestoreSearchQuery(request.Query);
        FromFilterBox.Text = request.From ?? "";
        ToFilterBox.Text = request.To ?? "";
        SubjectFilterBox.Text = request.Subject ?? "";
        SearchDateFrom.SelectedDate = request.FromDateInclusive?.Date;
        SearchDateTo.SelectedDate = request.ToDateExclusive?.Date.AddDays(-1);
        SearchAttachmentBox.SelectedItem = SearchAttachmentBox.Items.Cast<Choice<bool?>>()
            .FirstOrDefault(option => option.Value == request.HasAttachment);
        SetInvestigationFilters(request.Clues);
    }
}
