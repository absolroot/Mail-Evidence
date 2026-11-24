using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using JournalDesk;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private ListBox? _conversationList;
    private Button? _conversationOpen;
    private ScrollViewer? _conversationScroll;
    private Expander? _conversationEvidence;
    private Expander? _conversationMissing;
    private string? _conversationRecordId;
    private string? _conversationLanguage;
    private EvidenceSession? _conversationSession;

    private sealed record ConversationPosition(string RecordId, string? Selected, bool EvidenceExpanded, bool MissingExpanded,
        double Scroll = 0, double ListScroll = 0);
    private sealed record ConversationRow(string Key, string Title, string Info, MailRecord[] Mails, ConversationLink[] Links);

    private void EnsureConversationOpen()
    {
        if (ConversationTab.Visibility == Visibility.Visible && _activeRow?.Record is { } record &&
            (_conversationRecordId != record.Id || !ReferenceEquals(_conversationSession, _session)))
            OpenConversation(new(record.Id, null, false, false));
    }

    private ConversationPosition? CaptureConversationPosition() => _conversationRecordId is { } id
        ? new(id, (_conversationList?.SelectedItem as ConversationRow)?.Key,
            _conversationEvidence?.IsExpanded == true, _conversationMissing?.IsExpanded == true,
            _conversationScroll?.VerticalOffset ?? 0,
            _conversationList is { } list ? FindVisualChild<ScrollViewer>(list)?.VerticalOffset ?? 0 : 0) : null;

    private void RefreshConversation()
    {
        int count = _session is { } session && _activeRow?.Record is { } record
            ? session.GetRelatedMessageCount(record.Id) : 0;
        if (count == 0)
        {
            if (DetailTabs.SelectedItem == ConversationTab) DetailTabs.SelectedItem = BodyTab;
            ConversationTab.Visibility = Visibility.Collapsed;
            ClearConversation();
            return;
        }
        ConversationTab.Header = L($"관련 메일 ({count:N0}건)", $"Related mail ({count:N0})");
        ConversationTab.Visibility = Visibility.Visible;
        if (_conversationRecordId != _activeRow?.Record.Id || !ReferenceEquals(_conversationSession, _session))
            ClearConversation();
        else if (Language.IetfLanguageTag != _conversationLanguage && CaptureConversationPosition() is { } position)
            OpenConversation(position);
        if (DetailTabs.SelectedItem == ConversationTab) EnsureConversationOpen();
    }

    private void ClearConversation()
    {
        ConversationTab.Content = null;
        _conversationRecordId = null;
        _conversationSession = null;
        _conversationList = null;
        _conversationOpen = null;
        _conversationScroll = null;
        _conversationEvidence = null;
        _conversationMissing = null;
    }

    private void OpenConversation(ConversationPosition position)
    {
        if (_session is not { } session || _activeRow?.Record is not { } record || record.Id != position.RecordId ||
            ConversationTab.Visibility != Visibility.Visible) return;
        var history = session.GetConversation(record.Id);
        var rows = BuildConversationRows(history, record.Id);
        var missing = history.Previous.Where(link => link.ReferencedMail is null).ToArray();
        var self = history.Previous.Where(link => link.ReferencedMail?.Id == record.Id).ToArray();
        _conversationRecordId = record.Id;
        _conversationSession = session;
        _conversationLanguage = Language.IetfLanguageTag;

        var panel = new DockPanel { Margin = (Thickness)FindResource("EvidencePaneInset") };
        var status = new TextBlock
        {
            Text = ReplyComparisonText(history.ReplyComparison), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 7)
        };
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetAutomationId(status, "ConversationComparisonStatus");
        DockPanel.SetDock(status, Dock.Top);
        panel.Children.Add(status);

        var list = new ListBox
        {
            ItemsSource = rows, ItemTemplate = (DataTemplate)FindResource("ConversationRowTemplate"),
            Style = (Style)FindResource("EvidenceList"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxHeight = 218,
            Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible
        };
        list.SetResourceReference(BackgroundProperty, "Viewer.Canvas");
        list.SetResourceReference(ForegroundProperty, "Viewer.Text");
        list.SetResourceReference(BorderBrushProperty, "Viewer.Rule");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(list, "ConversationMessages");
        AutomationProperties.SetName(list, L("관련 메일 목록", "Related messages"));

        var section = new StackPanel();
        section.Children.Add(list);
        var open = ConversationButton(L("메일 열기", "Open message"), "OpenConversationMessage");
        open.Style = (Style)FindResource("Action");
        open.Margin = new Thickness(0, 5, 0, 0);
        section.Children.Add(open);

        var evidence = new Expander { Margin = new Thickness(0, 4, 0, 4), IsExpanded = position.EvidenceExpanded };
        var evidenceContent = new StackPanel();
        evidence.Content = evidenceContent;
        AutomationProperties.SetAutomationId(evidence, "ConversationReferenceHeader");
        section.Children.Add(evidence);

        if (missing.Length > 0)
        {
            var ids = missing.Select(link => link.Reference.MessageId).Distinct(StringComparer.Ordinal).ToArray();
            var missingContent = new StackPanel();
            missingContent.Children.Add(ConversationText(string.Join(Environment.NewLine, ids)));
            AddHeaders(missingContent, missing);
            var missingPane = new Expander
            {
                Header = L($"찾지 못한 참조 {ids.Length:N0}개", $"References missing from open sources ({ids.Length:N0})"),
                Margin = new Thickness(0, 4, 0, 4), IsExpanded = position.MissingExpanded,
                Content = missingContent
            };
            AutomationProperties.SetAutomationId(missingPane, "ConversationMissingReferences");
            section.Children.Add(missingPane);
            _conversationMissing = missingPane;
        }
        else _conversationMissing = null;

        if (self.Length > 0)
        {
            var selfPane = new Expander { Header = L("현재 메일을 가리키는 참조", "References to this same message") };
            var selfEvidence = new StackPanel();
            AddHeaders(selfEvidence, self);
            selfPane.Content = selfEvidence;
            section.Children.Add(selfPane);
        }

        var scroll = new ScrollViewer { Content = section, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        panel.Children.Add(scroll);
        _conversationScroll = scroll;
        _conversationList = list;
        _conversationOpen = open;
        _conversationEvidence = evidence;
        list.SelectionChanged += (_, _) => UpdateSelection();
        list.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter || list.SelectedItem is not ConversationRow) return;
            args.Handled = true;
            await ActivateSelected();
        };
        list.MouseDoubleClick += async (_, args) =>
        {
            if (ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) is ListBoxItem)
                await ActivateSelected();
        };
        open.Click += async (_, _) => await ActivateSelected();
        list.SelectedItem = rows.FirstOrDefault(row => row.Key == position.Selected)
            ?? rows.FirstOrDefault(row => row.Mails.Any(mail => mail.Id == history.ReplyComparison.Earlier?.RecordId))
            ?? rows.FirstOrDefault();
        UpdateSelection();
        ConversationTab.Content = panel;
        if (position.Selected is not null)
        {
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (!ReferenceEquals(_conversationList, list)) return;
                scroll.ScrollToVerticalOffset(position.Scroll);
                FindVisualChild<ScrollViewer>(list)?.ScrollToVerticalOffset(position.ListScroll);
            }));
        }
        async Task ActivateSelected()
        {
            if (list.SelectedItem is not ConversationRow selected) return;
            if (selected.Mails.Length == 1) await OpenMail(selected.Mails[0], open);
            else { evidence.IsExpanded = true; evidenceContent.Children.OfType<Button>().FirstOrDefault()?.Focus(); }
        }

        async Task OpenMail(MailRecord target, Button button)
        {
            if (!CanChooseSources || target.Id == record.Id) return;
            button.Focus();
            await RunInvestigationSearch(new(InvestigationKeyKind.RecordId, target.Id), openReader: true);
        }

        void UpdateSelection()
        {
            if (list.SelectedItem is not ConversationRow selected)
            {
                open.Visibility = evidence.Visibility = Visibility.Collapsed;
                return;
            }
            open.Visibility = evidence.Visibility = Visibility.Visible;
            open.Content = selected.Mails.Length == 1
                ? L("메일 열기", "Open message") : L("후보 보기", "Show candidates");
            evidence.Header = selected.Mails.Length == 1 ? L("참조 근거", "Reference evidence")
                : L($"후보 {selected.Mails.Length:N0}건과 참조 근거", $"{selected.Mails.Length:N0} candidates and reference evidence");
            evidenceContent.Children.Clear();
            if (selected.Mails.Length > 1)
            {
                foreach (var candidate in selected.Mails)
                {
                    string file = System.IO.Path.GetFileName(candidate.SourcePath);
                    var button = ConversationButton(L($"{file} 열기", $"Open {file}"), "OpenConversationCandidate");
                    button.ToolTip = candidate.SourcePath;
                    button.Click += async (_, _) => await OpenMail(candidate, button);
                    evidenceContent.Children.Add(button);
                    evidenceContent.Children.Add(ConversationText(candidate.SourcePath));
                }
            }
            AddHeaders(evidenceContent, selected.Links);
        }

        void AddHeaders(Panel parent, IEnumerable<ConversationLink> links)
        {
            foreach (var group in links.GroupBy(link => link.ReferencingMail.Id))
            {
                var source = group.First().ReferencingMail;
                parent.Children.Add(ConversationText(source.SourcePath));
                foreach (var link in group.DistinctBy(link => link.Reference.Header.Range))
                {
                    var reference = link.Reference;
                    var value = ConversationText($"{reference.Header.Name}: {reference.Header.Value}");
                    parent.Children.Add(value);
                    if (reference.Location.Range.Length <= 0) continue;
                    value.ContextMenu = new ContextMenu();
                    var copy = new MenuItem { Header = L("복사", "Copy") };
                    var original = new MenuItem { Header = "HEX" };
                    AutomationProperties.SetAutomationId(original, "OpenConversationReference");
                    copy.Click += (_, _) => CopyEvidenceText(value.SelectionLength > 0 ? value.SelectedText : value.Text);
                    original.Click += (_, _) =>
                    {
                        if (_work is null)
                            OpenEvidenceBytes(ConversationTab, reference.Location.Range, reference.Header.Name, reference.Location.Stream,
                                reference.Location.Property, sourceRecord: source, returnFocus: value, container: reference.Location.Container);
                    };
                    value.ContextMenu.Items.Add(copy);
                    value.ContextMenu.Items.Add(original);
                }
            }
        }
    }

    private ConversationRow[] BuildConversationRows(ConversationHistory history, string current)
    {
        var items = history.Previous.Where(link => link.ReferencedMail is { } mail && mail.Id != current)
            .Select(link => (Mail: link.ReferencedMail!, Link: link, Inbound: false))
            .Concat(history.Later.Where(link => link.ReferencingMail.Id != current)
                .Select(link => (Mail: link.ReferencingMail, Link: link, Inbound: true))).ToArray();
        var competingIds = items.Where(item => !item.Inbound && item.Link.CandidateCount > 1)
            .GroupBy(item => item.Mail.Id).ToDictionary(group => group.Key, group => group.First().Link.Reference.MessageId);
        return items.GroupBy(item => competingIds.TryGetValue(item.Mail.Id, out string? id) ? id : item.Mail.Id,
                StringComparer.Ordinal)
            .Select(group =>
            {
                var mails = group.Select(item => item.Mail).DistinctBy(mail => mail.Id).ToArray();
                var sample = mails[0];
                bool inbound = group.Any(item => item.Inbound);
                bool outbound = group.Any(item => !item.Inbound);
                string direction = outbound && inbound ? L("서로 참조", "Mutual references") : inbound
                    ? L("현재 메일을 참조함", "Refers to current message") : L("현재 메일이 참조함", "Referenced by current message");
                string detail = mails.Length > 1 ? L($"후보 {mails.Length:N0}건", $"{mails.Length:N0} candidates")
                    : string.Join(" · ", new[] { direction, sample.DateRaw, System.IO.Path.GetFileName(sample.SourcePath) }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                return new ConversationRow(group.Key, mails.Length > 1 ? group.Key : ConversationSubject(sample), detail,
                    mails, group.Select(item => item.Link).ToArray());
            }).ToArray();
    }

    private static string ConversationSubject(MailRecord record) =>
        string.IsNullOrWhiteSpace(record.Subject) ? L("(제목 없음)", "(No subject)") : record.Subject;

    private Button ConversationButton(string text, string automationId)
    {
        var button = new Button
        {
            Content = text, Style = (Style)FindResource("QuietAction"), HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private CopyText ConversationText(string value) => new()
    {
        Text = value, Style = (Style)FindResource("CopyField"), TextWrapping = TextWrapping.Wrap, Padding = new Thickness(3, 2, 3, 2)
    };

    private static string ReplyComparisonText(ReplyHistory comparison) => comparison.Status switch
    {
        ReplyHistoryStatus.Changed => L("이전 메일과 회신 주소가 다릅니다.", "The Reply-To address changed from the earlier message."),
        ReplyHistoryStatus.Ambiguous => L("가장 최근 후보의 회신 주소가 서로 다릅니다.", "The most recent candidates have different Reply-To addresses."),
        _ => ""
    };
}

