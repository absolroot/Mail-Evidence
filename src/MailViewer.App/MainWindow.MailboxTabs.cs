using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    private TabItem? _activeMailboxTab;
    private bool _movingMailbox;
    private CancellationTokenSource? _anomalyCountWork;
    private bool _anomalyCountFailed;
    private readonly Dictionary<TabItem, MailboxView> _mailboxViews = [];

    private TabItem ActiveMailboxTab => _activeMailboxTab ?? InvestigationTab;

    private sealed record MailboxView(InvestigationPosition Position, InvestigationPosition[] RelatedHistory);

    private void RefreshMailboxTabs()
    {
        if (AnomalyMailboxTab is null) return;
        InvestigationTab.Header = L("전체", "All mail");
        AnomalyMailboxTab.IsEnabled = _session is not null;
        BookmarksMailboxTab.IsEnabled = _session is not null;
        if (!ReferenceEquals(_bookmarkCountSession, _session)) RefreshBookmarkScope();
        BookmarksMailboxTab.Header = L($"북마크 ({_loadedBookmarkCount:N0}건)", $"Bookmarks ({_loadedBookmarkCount:N0})");
        if (_session?.AnomalousMailCount is int count)
        {
            AnomalyMailboxTab.Header = L($"이상 징후 ({count:N0}건)", $"Anomalies ({count:N0})");
            AnomalyMailboxTab.ToolTip = L($"현재 열린 자료에서 이상 징후가 있는 메일 {count:N0}건",
                $"{count:N0} messages with anomalies across open sources");
        }
        else
        {
            AnomalyMailboxTab.Header = _anomalyCountFailed ? L("이상 징후", "Anomalies") : L("이상 징후 (집계 중)", "Anomalies (counting)");
            AnomalyMailboxTab.ToolTip = _anomalyCountFailed ? L("메일 수를 집계하지 못했습니다.", "The message count is unavailable.")
                : L("현재 열린 자료의 이상 징후를 집계하는 중입니다.", "Counting anomalies across open sources.");
        }
        AllMailScopeButton.Content = ActiveMailboxTab == InvestigationTab ? L("전체 메일", "All messages") : L("전체 자료", "All sources");
        RefreshMailboxCounts();
    }

    private async void CountMailboxAnomalies(EvidenceSession session)
    {
        _anomalyCountWork?.Cancel();
        var work = new CancellationTokenSource();
        _anomalyCountWork = work;
        _anomalyCountFailed = false;
        RefreshMailboxTabs();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Run(() => session.PrepareAnomalyCount(work.Token), work.Token);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (!ReferenceEquals(_session, session) || _work is not null) { }
        catch (Exception error) when (IsExpected(error))
        {
            if (ReferenceEquals(_session, session)) _anomalyCountFailed = true;
        }
        finally
        {
            if (ReferenceEquals(_anomalyCountWork, work))
            {
                _anomalyCountWork = null;
                if (ReferenceEquals(_session, session))
                {
                    foreach (ResultRow row in ResultRows())
                        if (session.HasKnownAnomaly(row.Record.Id)) row.SetAnomaly();
                    UpdateCommands();
                }
            }
            work.Dispose();
        }
    }

    private async void OnMailboxTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WorkspaceTabs) || _movingMailbox) return;
        if (WorkspaceTabs.SelectedItem == SourcesTab)
        {
            StopLiveSearch();
            return;
        }
        if (WorkspaceTabs.SelectedItem == ActiveMailboxTab)
        {
            QueueLiveSearch();
            return;
        }
        if (_session is null || WorkspaceTabs.SelectedItem is not TabItem selected ||
            (selected != InvestigationTab && selected != AnomalyMailboxTab && selected != BookmarksMailboxTab))
            return;

        var previous = ActiveMailboxTab;
        if (!CanChooseSources)
        {
            SelectMailboxTab(previous);
            return;
        }

        _mailboxViews[previous] = new MailboxView(CaptureInvestigationPosition(), _investigationBack.ToArray());
        var destination = _mailboxViews.GetValueOrDefault(selected);
        var requested = destination?.Position.Applied ??
            new SearchRequest
            {
                AnomaliesOnly = selected == AnomalyMailboxTab,
                BookmarkRecordIds = selected == BookmarksMailboxTab ? _bookmarkScope : null
            };

        AttachMailboxToTab(selected);
        if (!await SearchAsync(requested))
        {
            AttachMailboxToTab(previous);
            UpdateCommands();
            return;
        }

        _investigationBack.Clear();
        if (destination is not null)
        {
            foreach (var position in destination.RelatedHistory.Reverse())
                _investigationBack.Push(position);
            RestoreInvestigationPosition(destination.Position);
        }
        else
        {
            RestoreInvestigationInputs(requested);
            RevealMailboxListForSearch();
            if (selected == AnomalyMailboxTab)
                DetailTabs.SelectedItem = InvestigationCluesTab;
        }
        UpdateCommands();
    }

    private void SelectMailboxTab(TabItem tab)
    {
        _movingMailbox = true;
        try { WorkspaceTabs.SelectedItem = tab; }
        finally { _movingMailbox = false; }
    }

    private void AttachMailboxToTab(TabItem tab)
    {
        var previous = ActiveMailboxTab;
        if (previous != tab)
        {
            previous.Content = null;
            tab.Content = MailboxWorkspace;
            _activeMailboxTab = tab;
        }
        SelectMailboxTab(tab);
    }

    private InvestigationPosition CaptureInvestigationPosition()
    {
        var observation = (InvestigationGrid.SelectedItem as InvestigationRow)?.Observation;
        var scroll = FindVisualChild<ScrollViewer>(ResultsGrid);
        return new InvestigationPosition(_appliedRequest, CaptureSearch(), _activeRow?.Record.Id,
            DetailTabs.SelectedItem as TabItem, observation, scroll?.VerticalOffset ?? 0, scroll?.HorizontalOffset ?? 0,
            Keyboard.FocusedElement, _readerExpanded, _mailFieldsGrid?.SelectedItem as MailInvestigationField,
            ActiveMailboxTab, CaptureConversationPosition());
    }

    private void RestoreInvestigationPosition(InvestigationPosition previous)
    {
        RestoreInvestigationInputs(previous.Draft);
        ResultsGrid.SelectedItem = ResultRows().FirstOrDefault(row => row.Record.Id == previous.RecordId)
            ?? ResultsGrid.SelectedItem;
        if (previous.Tab is { Visibility: Visibility.Visible } tab) DetailTabs.SelectedItem = tab;
        if (previous.Conversation is { } conversation && _activeRow?.Record.Id == conversation.RecordId)
            OpenConversation(conversation);
        SelectInvestigationObservation(previous.Observation);
        if (previous.MailField is not null && _mailFieldsGrid is { } fields)
            fields.SelectedItem = fields.Items.Cast<MailInvestigationField>().FirstOrDefault(field => field == previous.MailField);

        if (previous.ReaderExpanded && _readerIsNarrow) ShowMailboxReader(markViewed: false);
        else RevealMailboxListForSearch();
        UpdateLayout();
        if (FindVisualChild<ScrollViewer>(ResultsGrid) is { } scroll)
        {
            scroll.ScrollToVerticalOffset(previous.Vertical);
            scroll.ScrollToHorizontalOffset(previous.Horizontal);
        }
        if (previous.Focus is FrameworkElement { IsVisible: true, IsEnabled: true } focus &&
            focus.IsDescendantOf(MailboxWorkspace))
            Keyboard.Focus(focus);
        else if (previous.Conversation is not null)
        {
            if (previous.Focus is Button button && System.Windows.Automation.AutomationProperties.GetAutomationId(button)
                == "OpenConversationMessage" && _conversationOpen?.Focus() == true) return;
            _conversationList?.Focus();
        }
    }
}
