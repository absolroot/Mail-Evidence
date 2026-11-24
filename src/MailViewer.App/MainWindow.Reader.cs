using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow
{
    public static readonly DependencyProperty UseToggleHighlightProperty = DependencyProperty.RegisterAttached(
        "UseToggleHighlight", typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    public static bool GetUseToggleHighlight(DependencyObject element) => (bool)element.GetValue(UseToggleHighlightProperty);
    public static void SetUseToggleHighlight(DependencyObject element, bool value) => element.SetValue(UseToggleHighlightProperty, value);

    public static readonly DependencyProperty FillCommandIconProperty = DependencyProperty.RegisterAttached(
        "FillCommandIcon", typeof(bool), typeof(MainWindow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetFillCommandIcon(DependencyObject element) => (bool)element.GetValue(FillCommandIconProperty);
    public static void SetFillCommandIcon(DependencyObject element, bool value) => element.SetValue(FillCommandIconProperty, value);

    private enum MailboxSort { Newest, Oldest, Sender, Subject }

    private const double ReaderMinimumWidth = 430;
    private const double MailListMinimumWidth = 290;
    private const double NarrowWindowWidth = 825;
    private const double DefaultMailboxFolderWidth = 192;
    private const double MinimumMailboxFolderWidth = 136;
    private const double MaximumMailboxFolderWidth = 420;
    private const double CollapsedMailboxFolderWidth = 38;
    private const double FolderDividerWidth = 7;
    private const double ReaderDividerWidth = 5;
    private double _mailboxFolderWidth = DefaultMailboxFolderWidth;
    private GridLength _readerListWidth = new(375);
    private bool _readerIsNarrow;
    private bool _readerExpanded;

    private void OnMailboxWorkspaceLoaded(object sender, RoutedEventArgs e)
    {
        if (MailboxSortBox.ItemsSource is null)
        {
            RefreshReaderLanguage();
            var rowStyle = new Style(typeof(DataGridRow), ResultsGrid.RowStyle);
            rowStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(ResultRow.AccessibleName))));
            rowStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new Binding("Record.SourcePath")));
            rowStyle.Setters.Add(new Setter(ToolTipService.ShowsToolTipOnKeyboardFocusProperty, true));
            ResultsGrid.RowStyle = rowStyle;
        }
        EnsureMailInfo();
        SynchronizeNarrowReader();
    }

    private void OnMailboxViewportSizeChanged(object sender, SizeChangedEventArgs e) => SynchronizeNarrowReader();

    private void SynchronizeNarrowReader()
    {
        if (ActualWidth <= 0 || MailboxReaderPane is null)
            return;

        bool hasFolders = PstFolderPane.Visibility == Visibility.Visible;
        bool foldersExpanded = hasFolders && PstFoldersToggle.IsChecked == true;
        double folderReserve = foldersExpanded ? DefaultMailboxFolderWidth + FolderDividerWidth
            : hasFolders ? CollapsedMailboxFolderWidth : 0;
        Thickness border = SystemParameters.WindowResizeBorderThickness;
        double width = ActualWidth - border.Left - border.Right;
        double splitWidth = folderReserve + MailListMinimumWidth + ReaderMinimumWidth + ReaderDividerWidth;
        bool narrow = width < Math.Max(NarrowWindowWidth, splitWidth);
        if (narrow && !_readerIsNarrow)
        {
            _readerExpanded = _activeRow is not null && MailboxReaderPane.IsKeyboardFocusWithin;
        }
        _readerIsNarrow = narrow;
        bool readerOnly = IsAttachmentPreviewExpanded || (narrow && _readerExpanded);
        double folderDivider = foldersExpanded && !readerOnly ? FolderDividerWidth : 0;
        double folderBudget = width - MailListMinimumWidth - folderDivider - (narrow ? 0 : ReaderMinimumWidth + ReaderDividerWidth);
        double folderLimit = Math.Clamp(folderBudget, MinimumMailboxFolderWidth, MaximumMailboxFolderWidth);
        double folders = readerOnly || !hasFolders ? 0 : foldersExpanded
            ? Math.Clamp(_mailboxFolderWidth, MinimumMailboxFolderWidth, folderLimit) : CollapsedMailboxFolderWidth;
        double listWidth = Math.Max(MailListMinimumWidth,
            Math.Min(_readerListWidth.Value, width - folders - folderDivider - ReaderMinimumWidth - ReaderDividerWidth));
        MailboxResultsColumn.MinWidth = readerOnly || narrow ? 0 : MailListMinimumWidth;
        MailboxResultsColumn.Width = readerOnly ? new GridLength(0)
            : narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(listWidth);
        MailboxReaderColumn.MinWidth = narrow ? 0 : ReaderMinimumWidth;
        MailboxReaderColumn.Width = narrow && !readerOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        MailboxSplitterColumn.Width = new GridLength(narrow || readerOnly ? 0 : ReaderDividerWidth);
        MailboxFolderColumn.MinWidth = folderDivider > 0 ? MinimumMailboxFolderWidth : 0;
        MailboxFolderColumn.MaxWidth = folderDivider > 0 ? folderLimit : folders;
        MailboxFolderColumn.Width = new GridLength(folders);
        MailboxFolderDividerColumn.Width = new GridLength(folderDivider);
        MailboxFolderSplitter.Visibility = folderDivider > 0 ? Visibility.Visible : Visibility.Collapsed;
        MailboxFolderSurface.Visibility = readerOnly ? Visibility.Collapsed : Visibility.Visible;
        MailboxResultsPane.Visibility = readerOnly ? Visibility.Collapsed : Visibility.Visible;
        MailboxReaderPane.Visibility = narrow && !readerOnly ? Visibility.Collapsed : Visibility.Visible;
        MailboxSplitter.Visibility = narrow || readerOnly ? Visibility.Collapsed : Visibility.Visible;
        ReaderBackButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;

        if (!narrow)
            _readerExpanded = false;
    }

    private void OnMailboxFolderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!e.Canceled)
            KeepMailboxFolderWidth();
        else
            SynchronizeNarrowReader();
    }

    private void OnMailboxFolderSplitterKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
            KeepMailboxFolderWidth();
    }

    private void KeepMailboxFolderWidth()
    {
        double displayedPreference = Math.Clamp(_mailboxFolderWidth, MailboxFolderColumn.MinWidth, MailboxFolderColumn.MaxWidth);
        double newWidth = MailboxFolderColumn.Width.Value;
        if (Math.Abs(newWidth - displayedPreference) >= 1)
            _mailboxFolderWidth = newWidth;
        SynchronizeNarrowReader();
    }

    private void OnMailboxSplitterChanged(object sender, DragCompletedEventArgs e)
    {
        if (!_readerIsNarrow)
            _readerListWidth = MailboxResultsColumn.Width;
    }

    private void OpenReaderForSingleResult()
    {
        if (ResultsGrid.Items.Count != 1 || _activeRow is null)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (WorkspaceTabs.SelectedItem != ActiveMailboxTab || ResultsGrid.Items.Count != 1 || _activeRow is null)
                return;
            SynchronizeNarrowReader();
            if (_readerIsNarrow)
                ShowMailboxReader(markViewed: false);
        });
    }

    private void ToggleMailboxSelection(object sender, RoutedEventArgs e)
    {
        if (SelectionModeButton.IsChecked != true)
        {
            foreach (ResultRow row in ResultRows().Where(row => row.Checked))
            {
                row.PropertyChanged -= OnResultChecked;
                row.Checked = false;
                row.PropertyChanged += OnResultChecked;
            }
            _checkedRecords.Clear();
        }
        UpdateCommands();
    }

    private void OnMailboxResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _activeRow is not null && CanChooseSources)
        {
            ShowMailboxReader();
            e.Handled = true;
            return;
        }
        if (!_readerIsNarrow && e.Key is (Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) &&
            e.OriginalSource is not ButtonBase && CanChooseSources)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(MarkVisibleMessageViewed));
        OnResultsKeyDown(sender, e);
    }

    private void OnMailboxResultClick(object sender, MouseButtonEventArgs e)
    {
        if (!CanChooseSources || e.OriginalSource is not DependencyObject current)
            return;

        for (DependencyObject? element = current; element is Visual; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
                return;
            if (element is DataGridRow { Item: ResultRow row })
            {
                if (ReferenceEquals(row, ResultsGrid.SelectedItem))
                {
                    if (_readerIsNarrow) ShowMailboxReader();
                    else MarkVisibleMessageViewed();
                }
                return;
            }
        }
    }

    private void ShowMailboxReader(bool markViewed = true)
    {
        if (_readerIsNarrow)
        {
            _readerExpanded = true;
            SynchronizeNarrowReader();
        }
        Keyboard.Focus(ReaderHeader.IsVisible ? ReaderSubject : DetailTabs.SelectedItem as IInputElement);
        if (markViewed) MarkVisibleMessageViewed();
    }

    private void OnReviewClicked(object sender, MouseButtonEventArgs e) => MarkVisibleMessageViewed();

    private void MarkVisibleMessageViewed()
    {
        bool reviewing = DetailTabs.SelectedItem == BodyTab || DetailTabs.SelectedItem == InvestigationCluesTab ||
            ActiveMailboxTab == AnomalyMailboxTab;
        if (!reviewing || _activeRow is not { Viewed: false } row || !MailboxReaderPane.IsVisible ||
            WorkspaceTabs.SelectedItem != ActiveMailboxTab) return;
        row.SetViewed();
        try { _mailMarks.MarkViewed(row.Record.Id); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            WorkStatus.Text = L("이 메일을 읽은 상태를 저장하지 못했습니다.", "Could not save the read state for this message.");
        }
    }

    private void BackToMailboxList(object sender, RoutedEventArgs e)
    {
        if (!_readerIsNarrow || !_readerExpanded)
            return;

        RevealMailboxListForSearch();
        if (ResultsGrid.SelectedItem is { } row)
        {
            ResultsGrid.ScrollIntoView(row);
            ResultsGrid.UpdateLayout();
            if (ResultsGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container &&
                Keyboard.Focus(container) == container)
                return;
        }
        Keyboard.Focus(ResultsGrid);
    }

    private void RevealMailboxListForSearch()
    {
        if (IsAttachmentPreviewExpanded) SetAttachmentPreviewExpansion(false);
        if (!_readerIsNarrow || !_readerExpanded)
            return;

        _readerExpanded = false;
        SynchronizeNarrowReader();
    }

    private void OnMailboxWorkspaceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        if (DetailTabs.SelectedItem == AttachmentsTab)
        {
            if (AttachmentPicker.IsDropDownOpen) return;
            if (TryCloseExpandedAttachmentPreview())
            {
                e.Handled = true;
                return;
            }
        }
        if (MailboxReaderPane.IsVisible && DetailTabs.SelectedItem != BodyTab)
        {
            DetailTabs.SelectedItem = BodyTab;
            Keyboard.Focus(BodyTab);
            e.Handled = true;
        }
        else if (_readerIsNarrow && _readerExpanded)
        {
            BackToMailboxList(sender, e);
            e.Handled = true;
        }
    }

    private void UpdateReaderHeader()
    {
        bool body = DetailTabs.SelectedItem == BodyTab;
        ReaderHeader.Visibility = _activeRow is not null && DetailTabs.SelectedItem != HeadersTab && DetailTabs.SelectedItem != RawTab
            ? Visibility.Visible : Visibility.Collapsed;
        ReaderAddressSummary.Visibility = body ? Visibility.Visible : Visibility.Collapsed;
        ReaderSubject.FontSize = body ? 20 : 16;
        ReaderHeader.Padding = body ? new Thickness(16, 9, 16, 4) : new Thickness(8, 6, 8, 6);
    }

    private void OnReaderDetailTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, DetailTabs) || _activeRow?.Record is not { } record)
            return;
        if (_expandAttachmentPreview && (DetailTabs.SelectedItem == AttachmentsTab || e.RemovedItems.Contains(AttachmentsTab)))
            SynchronizeNarrowReader();
        UpdateReaderHeader();
        if (DetailTabs.SelectedItem is TabItem { IsKeyboardFocusWithin: true }) MarkVisibleMessageViewed();
        if (DetailTabs.SelectedItem == ConversationTab)
            EnsureConversationOpen();
        else if (DetailTabs.SelectedItem == HeadersTab && _headerSourceBox is not null && _headerSourceRecordId != record.Id)
            LoadHeaderSource(record);
        else if (DetailTabs.SelectedItem == RawTab && !_openingEvidenceBytes)
        {
            if (e.RemovedItems.Contains(InvestigationCluesTab) &&
                InvestigationGrid.SelectedItem is InvestigationRow { Observation: var observation } &&
                (observation.Location is { Range.Length: > 0 } || observation.DecodedAttachmentId is not null))
            {
                var returnFocus = ReplyComparisonPane.IsVisible ? observation.Field switch
                {
                    "Earlier Reply-To" => EarlierReplyValue,
                    "Current Reply-To" => CurrentReplyValue,
                    _ => null
                } : null;
                OpenInvestigationSource(observation, returnFocus);
            }
            else if (_rawRange.Length == 0 && record.CanReadMessage &&
                (record.ContainerMessage is not null || record.Format is MailFormat.Eml or MailFormat.Mbox))
                OpenMessageBytes(sender, e);
        }
    }

    private void RefreshReaderAttachments(IReadOnlyList<MailAttachment> attachments)
    {
        int count = attachments.Count;
        BodyAttachmentsButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string countLabel = count == 1 ? L("첨부파일 1개", "1 attachment") : L($"첨부파일 {count:N0}개", $"{count:N0} attachments");
        string? name = count == 1 ? attachments[0].Name : null;
        BodyAttachmentCount.Text = countLabel;
        BodyAttachmentName.Text = name ?? "";
        BodyAttachmentName.Visibility = name is null ? Visibility.Collapsed : Visibility.Visible;
        BodyAttachmentIcon.Visibility = BodyAttachmentName.Visibility;
        BodyAttachmentsButton.ToolTip = name ?? L("첨부파일 목록 열기", "Open attachment list");
        AutomationProperties.SetName(BodyAttachmentsButton, name is null ? countLabel : $"{countLabel}, {name}");
        RefreshBodyAttachmentStrip(attachments);
    }

    private void OpenReaderAttachments(object sender, RoutedEventArgs e)
    {
        if (_activeRow?.Record.Attachments.Count is not > 0)
            return;

        if (SelectedBodyAttachment is { } attachment)
            AttachmentsGrid.SelectedItem = attachment;
        DetailTabs.SelectedItem = AttachmentsTab;
        FocusAttachmentSelection();
    }

    private void RefreshReaderLanguage()
    {
        ReaderSearchHint.Text = L("메일 검색", "Search mail");
        ReaderBackButton.Content = L("목록으로 돌아가기", "Back to messages");
        SetStaticLabels(ReaderHeader);
        ReaderToLabel.Text = L("받는 사람", "To");
        ReaderEmptyHint.Text = L("목록에서 읽을 메일을 선택하세요.", "Select a message to read it.");
        RefreshMailboxFilterCaption();
        AutomationProperties.SetName(MailboxSortBox, L("메일 정렬", "Sort messages"));
        AutomationProperties.SetName(MailboxFolderSplitter, L("메일함 너비 조절", "Resize mailboxes"));
        AutomationProperties.SetName(MailboxSplitter, L("메일 목록과 읽기 창 너비 조절", "Resize message list and reading pane"));

        int selected = MailboxSortBox.SelectedIndex;
        MailboxSortBox.ItemsSource = new Choice<MailboxSort>[]
        {
            new(MailboxSort.Newest, L("최신순", "Newest")),
            new(MailboxSort.Oldest, L("오래된순", "Oldest")),
            new(MailboxSort.Sender, L("보낸 사람순", "By sender")),
            new(MailboxSort.Subject, L("제목순", "By subject"))
        };
        MailboxSortBox.SelectedIndex = Math.Max(0, selected);
    }

    private void RefreshMailboxFilterCaption()
    {
        SearchRequest request = _appliedRequest;
        int active = new[]
        {
            request.HasAttachment.HasValue, request.FromDateInclusive.HasValue || request.ToDateExclusive.HasValue,
            request.From is not null, request.To is not null, request.Subject is not null,
            request.Field != SearchField.Any, request.IncludePstSubfolders, request.Clues != InvestigationClue.None
        }.Count(value => value);
        AddressFiltersToggle.Content = active > 0 ? L($"필터 {active}", $"Filters {active}") : L("필터", "Filters");
        AddressFiltersToggle.ContentStringFormat = active > 0 ? null : "GlyphOnly";
        AddressFiltersToggle.Width = active > 0 ? double.NaN : 30;
        AddressFiltersToggle.Padding = active > 0 ? new Thickness(6, 0, 6, 0) : new Thickness(0);
    }

    private void OnMailboxSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filterDialog is not null) return;
        ApplyMailboxSort();
        if (ResultsGrid?.SelectedItem is { } selected && ResultsGrid.IsVisible)
            ResultsGrid.ScrollIntoView(selected);
    }

    private void ApplyMailboxSort()
    {
        if (ResultsGrid?.ItemsSource is { } source && CollectionViewSource.GetDefaultView(source) is ListCollectionView view)
        {
            var sort = MailboxSortBox.SelectedItem as Choice<MailboxSort>;
            MailboxSort order = sort?.Value ?? MailboxSort.Newest;
            using (view.DeferRefresh())
            {
                view.GroupDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(order is MailboxSort.Newest or MailboxSort.Oldest
                    ? nameof(ResultRow.DateSection) : nameof(ResultRow.PinSection)));
                view.CustomSort = new MailboxRowComparer(order);
            }
        }
    }

    private sealed class MailboxRowComparer(MailboxSort order) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ResultRow left || y is not ResultRow right)
                return 0;

            int pinOrder = right.Pinned.CompareTo(left.Pinned);
            if (pinOrder != 0) return pinOrder;
            MailRecord a = left.Record;
            MailRecord b = right.Record;
            int comparison = order switch
            {
                MailboxSort.Sender => StringComparer.CurrentCultureIgnoreCase.Compare(a.From, b.From),
                MailboxSort.Subject => StringComparer.CurrentCultureIgnoreCase.Compare(a.Subject, b.Subject),
                _ => CompareDate(a.Date, b.Date)
            };
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }

        private int CompareDate(DateTimeOffset? a, DateTimeOffset? b)
        {
            if (a.HasValue && b.HasValue)
                return order == MailboxSort.Oldest ? a.Value.CompareTo(b.Value) : b.Value.CompareTo(a.Value);

            return b.HasValue.CompareTo(a.HasValue);
        }
    }
}

public sealed class MailReaderTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => parameter switch
    {
        "Date" => value is DateTimeOffset date ? date.UtcDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "—",
        "AttachmentVisibility" => value is int and > 0 ? Visibility.Visible : Visibility.Collapsed,
        "Preview" => value is IReadOnlyList<MailBodyPart> parts ? Preview(parts) : "",
        "Visibility" => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible,
        _ => throw new ArgumentOutOfRangeException(nameof(parameter))
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Preview(IReadOnlyList<MailBodyPart> parts)
    {
        var body = parts.FirstOrDefault(part => part.MediaType == "text/plain") ?? parts.FirstOrDefault();
        if (body is null)
            return "";

        ReadOnlySpan<char> excerpt = body.Text.AsSpan(0, Math.Min(body.Text.Length, 200));
        return string.Join(' ', excerpt.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

