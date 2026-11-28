using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MailViewer.Core;

namespace MailViewer.App;

public partial class MainWindow : Window
{
    private readonly HashSet<string> _checkedRecords = new(StringComparer.Ordinal);
    private EvidenceSession? _session;
    private (IReadOnlyList<SourceInfo> Sources, IReadOnlyList<ReadIssue> Issues)? _unappliedSourceReport;
    private CancellationTokenSource? _work;
    private SearchRequest _appliedRequest = new();
    private SearchResult? _result;
    private ResultRow? _activeRow;
    private readonly bool _persistLanguage;

    public MainWindow(IReadOnlyList<string> paths, string? marksFolder = null, bool persistLanguage = false)
    {
        _persistLanguage = persistLanguage;
        _mailMarks = new MailboxMarks(marksFolder);
        RefreshBookmarkScope();
        if (!Application.Current.Resources.Contains("Viewer.Canvas"))
        {
            Appearance.Apply(Application.Current, Appearance.MatchWindows());
        }
        MailText.Apply(MailText.Current, save: false);
        InitializeComponent();
        InitializeLanguageChoices();
        SetStaticLabels(this);
        Language = JournalDesk.L.XmlLanguage;
        Appearance.ApplyToWindow(this);
        AssignIcon();
        PopulateFilters();
        InitializeInvestigationFilters();
        InitializeIntake(paths);
        SearchFieldBox.SelectionChanged += FilterChanged;
        SearchSourceBox.SelectionChanged += OnSearchSourceChanged;
        PstFoldersToggle.Checked += FilterChanged;
        PstFoldersToggle.Unchecked += FilterChanged;
        SearchAttachmentBox.SelectionChanged += FilterChanged;
        SearchDateFrom.SelectedDateChanged += FilterChanged;
        SearchDateTo.SelectedDateChanged += FilterChanged;
        InitializeLiveSearch();
        RefreshDisplaySettings();
        FromFilterBox.TextChanged += FilterChanged;
        ToFilterBox.TextChanged += FilterChanged;
        SubjectFilterBox.TextChanged += FilterChanged;
        BodyRepresentationBox.SelectionChanged += OnBodyRepresentationChanged;
    }

    private void PopulateFilters()
    {
        SearchFieldBox.ItemsSource = Enum.GetValues<SearchField>()
            .Select(field => new Choice<SearchField>(field, FieldName(field))).ToArray();
        SearchFieldBox.SelectedIndex = 0;
        SearchAttachmentBox.ItemsSource = new Choice<bool?>[]
        {
            new(null, L("첨부 여부 무관", "Any attachment state")), new(true, L("첨부 있음", "With attachments")),
            new(false, L("첨부 없음", "Without attachments"))
        };
        SearchAttachmentBox.SelectedIndex = 0;
        PopulateSourceOptions();
    }

    private void AssignIcon()
    {
        try
        {
            if (Application.GetResourceStream(new Uri("Assets/MailViewer.ico", UriKind.Relative)) is { } asset)
            {
                using var stream = asset.Stream;
                Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
        }
        catch (IOException)
        {
            // The development build can be used before the branding resource is added.
        }
    }

    private void PopulateSourceOptions(bool preserveSelection = true)
    {
        string? selection = preserveSelection ? SelectedSourceId : null;
        List<Choice<string?>> values = [new(null, L("전체 자료", "All sources"))];
        if (_session is { } session)
        {
            values.AddRange(session.Sources.Select(source => new Choice<string?>(source.Id,
                source.Format.IsFolderStore() ? ScopeSourceName(source) : source.Path,
                source.StoreLayout is { } layout ? $"{source.Path}\n{StoreFormatName(source.Format)} · {DescribeStoreLayout(layout)}" : source.Path)));
        }
        _syncingPstTree = true;
        try
        {
            SearchSourceBox.ItemsSource = values;
            SearchSourceBox.SelectedItem = values.FirstOrDefault(value => value.Value == selection) ?? values[0];
        }
        finally
        {
            _syncingPstTree = false;
        }
    }

    private async void RunSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchFromKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None &&
            !_composingSearch && SearchQueryBox.Text.Length > 0)
        {
            e.Handled = true;
            ClearSearchQuery(sender, e);
            return;
        }

        if (e.Key == Key.Enter && !_composingSearch)
        {
            e.Handled = true;
            if (_filterDialog is null) await SearchAsync(); else await ApplyOpenFilters();
        }
    }

    private void ClearSearchQuery(object sender, RoutedEventArgs e)
    {
        SearchQueryBox.Clear();
        SearchQueryBox.Focus();
    }

    private Window? _filterDialog;

    private void OpenSearchFilters(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources || _session is null || _filterDialog is not null)
        {
            AddressFiltersToggle.IsChecked = false;
            return;
        }

        StopLiveSearch();
        SearchRequest original = CaptureSearch();
        int sourceIndex = SearchSourceBox.SelectedIndex;
        int sortIndex = MailboxSortBox.SelectedIndex;
        bool hadTreeSelection = _selectedPstFolder is not null || _selectedPstFile is not null || _selectedSourceGroup is not null;
        var dialog = new Window
        {
            Owner = this,
            Title = L("검색 조건", "Search filters"),
            Width = 520, Height = 490, MinWidth = 420, MinHeight = 410,
            FontFamily = FontFamily, Language = Language, Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        Appearance.ApplyToWindow(dialog);
        dialog.SetResourceReference(BackgroundProperty, "Viewer.Canvas");
        dialog.SetResourceReference(ForegroundProperty, "Viewer.Text");
        NameScope.SetNameScope(dialog, NameScope.GetNameScope(this));
        InvestigationFilterQuery.Clear();
        InvestigationFilters.IsExpanded = original.Clues != InvestigationClue.None;
        dialog.Loaded += (_, _) =>
        {
            FilterScroll.ScrollToTop();
            SearchFieldBox.Focus();
        };
        dialog.PreviewKeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape || _work is not null) return;
            if (SearchFieldBox.IsDropDownOpen || SearchSourceBox.IsDropDownOpen ||
                SearchAttachmentBox.IsDropDownOpen || MailboxSortBox.IsDropDownOpen) return;
            key.Handled = true;
            dialog.DialogResult = false;
        };
        dialog.Closing += (_, args) => args.Cancel = _work is not null;

        FilterFeedback.Text = "";
        _filterDialog = dialog;
        AddressFiltersToggle.IsChecked = true;
        MailboxResultsPane.Children.Remove(FilterOptions);
        FilterOptions.Visibility = Visibility.Visible;
        var filterSurface = new Border { Padding = new Thickness(16, 14, 16, 14), Child = FilterOptions };
        filterSurface.SetResourceReference(Border.BackgroundProperty, "Viewer.Canvas");
        dialog.Content = filterSurface;
        try
        {
            if (dialog.ShowDialog() != true)
            {
                SearchFieldBox.SelectedIndex = (int)original.Field;
                SearchSourceBox.SelectedIndex = sourceIndex;
                if (hadTreeSelection) RestorePstTreeSelection(original);
                FromFilterBox.Text = original.From ?? "";
                ToFilterBox.Text = original.To ?? "";
                SubjectFilterBox.Text = original.Subject ?? "";
                SearchDateFrom.SelectedDate = original.FromDateInclusive?.Date;
                SearchDateTo.SelectedDate = original.ToDateExclusive?.Date.AddDays(-1);
                SearchAttachmentBox.SelectedItem = SearchAttachmentBox.Items.Cast<Choice<bool?>>()
                    .FirstOrDefault(item => item.Value == original.HasAttachment);
                SetInvestigationFilters(original.Clues);
                MailboxSortBox.SelectedIndex = sortIndex;
            }
        }
        finally
        {
            filterSurface.Child = null;
            dialog.Content = null;
            FilterOptions.Visibility = Visibility.Collapsed;
            MailboxResultsPane.Children.Add(FilterOptions);
            _filterDialog = null;
            AddressFiltersToggle.IsChecked = false;
            QueueLiveSearch();
            UpdateCommands();
        }
    }

    private void OnAnomalyFilterExpanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, InvestigationFilters) || _filterDialog is not { IsLoaded: true } dialog) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(_filterDialog, dialog) || !InvestigationFilters.IsExpanded) return;
            double height = Math.Min(InvestigationFilters.ActualHeight, FilterScroll.ViewportHeight);
            if (height > 0 && InvestigationFilters.ActualWidth > 0)
                InvestigationFilters.BringIntoView(new Rect(0, 0, InvestigationFilters.ActualWidth, height));
        }));
    }

    private void CancelFilterDraft(object sender, RoutedEventArgs e)
    {
        if (_work is null && _filterDialog is { } dialog)
            dialog.DialogResult = false;
    }

    private void ResetFilterDraft(object sender, RoutedEventArgs e) => ResetSearchInputs(clearQuery: false);

    private async void ApplyFilterDraft(object sender, RoutedEventArgs e) => await ApplyOpenFilters();

    private async Task ApplyOpenFilters()
    {
        if (await SearchAsync())
        {
            if (_filterDialog is { } dialog) dialog.DialogResult = true;
        }
        else
        {
            FilterFeedback.Text = WorkStatus.Text;
        }
    }

    private void ResetSearchInputs(bool clearQuery = true)
    {
        SearchFieldBox.SelectedIndex = 0;
        if (_selectedPstFolder is null && _selectedPstFile is null && _selectedSourceGroup is null && !IsFolderStoreSource(SelectedSourceId))
            SearchSourceBox.SelectedIndex = 0;
        SearchAttachmentBox.SelectedIndex = 0;
        SearchDateFrom.SelectedDate = null;
        SearchDateTo.SelectedDate = null;
        if (clearQuery) RestoreSearchQuery("");
        FromFilterBox.Clear();
        ToFilterBox.Clear();
        SubjectFilterBox.Clear();
        InvestigationFilterQuery.Clear();
        SetInvestigationFilters(InvestigationClue.None);
    }

    private async Task<bool> SearchAsync(SearchRequest? requested = null)
    {
        if (_session is not { } session || !CanChooseSources) return false;
        DateTime? from = SearchDateFrom.SelectedDate;
        DateTime? to = SearchDateTo.SelectedDate;
        if (requested is null && from.HasValue && to.HasValue && from.Value > to.Value)
        {
            WorkStatus.Text = L("종료일은 시작일보다 앞설 수 없습니다.", "The end date cannot be before the start date.");
            return false;
        }
        SearchRequest request = WithCurrentBookmarks(requested ?? CaptureSearch());
        var work = StartWork(L("메일을 검색하는 중입니다.", "Searching mail."));
        try
        {
            SearchResult result = await Task.Run(() => session.Search(request, work.Token), work.Token);
            work.Token.ThrowIfCancellationRequested();
            SetResults(request, result, selectFirst: false);
            WorkStatus.Text = "";
            return true;
        }
        catch (OperationCanceledException)
        {
            WorkStatus.Text = L("검색을 취소했습니다. 이전 검색 결과를 표시합니다.", "Search canceled. Showing the previous results.");
            return false;
        }
        catch (Exception error) when (IsExpected(error))
        {
            WorkStatus.Text = L($"검색하지 못했습니다. {error.Message}", $"Search failed. {error.Message}");
            return false;
        }
        finally
        {
            EndWork(work);
        }
    }

    private SearchRequest CaptureSearch()
    {
        DateTime? from = SearchDateFrom.SelectedDate;
        DateTime? to = SearchDateTo.SelectedDate;
        return new SearchRequest(SearchQueryBox.Text.Trim(),
            (SearchFieldBox.SelectedItem as Choice<SearchField>)?.Value ?? SearchField.Any,
            _selectedPstFolder?.SourceId ?? _selectedPstFile ?? SelectedSourceId,
            from.HasValue ? UtcDate(from.Value.Date) : null,
            to.HasValue && to.Value.Date < DateTime.MaxValue.Date
                ? UtcDate(to.Value.Date.AddDays(1)) : null,
            (SearchAttachmentBox.SelectedItem as Choice<bool?>)?.Value,
            Optional(FromFilterBox.Text), Optional(ToFilterBox.Text), Optional(SubjectFilterBox.Text),
            _selectedPstFolder?.NodeId, _selectedPstFolder is not null && PstSubfoldersBox.IsChecked == true)
            {
                Clues = CaptureInvestigationFilters(), Exact = _exactSearch, AnomaliesOnly = ActiveMailboxTab == AnomalyMailboxTab,
                SourceGroup = _selectedSourceGroup,
                BookmarkRecordIds = ActiveMailboxTab == BookmarksMailboxTab ? _bookmarkScope : null
            };
    }

    private static DateTimeOffset UtcDate(DateTime date) => new(DateTime.SpecifyKind(date, DateTimeKind.Utc));

    private void SetResults(SearchRequest request, SearchResult result, bool selectFirst)
    {
        string? previous = selectFirst ? null : _activeRow?.Record.Id;
        Dictionary<(string SourceId, uint NodeId), string> folders = [];
        if (_session is { } session)
        {
            foreach (var group in session.PstFolders.GroupBy(folder => (folder.SourceId, folder.Name.ToUpperInvariant())))
            {
                bool duplicate = group.Skip(1).Any();
                foreach (PstFolder folder in group)
                {
                    string caption = duplicate ? $"0x{folder.NodeId:X} · {folder.Name}" : folder.Name;
                    folders.Add((folder.SourceId, folder.NodeId), caption);
                }
            }
        }
        ResultRow[] rows = result.Hits.Select(hit => new ResultRow(hit, _checkedRecords.Contains(hit.Record.Id),
            hit.Record.FolderNodeId is { } folderId
                ? folders.GetValueOrDefault((hit.Record.SourceId, folderId)) ?? hit.Record.FolderPath : null)).ToArray();
        foreach (ResultRow row in rows)
        {
            row.SetMarks(_mailMarks.IsBookmarked(row.Record.Id), _mailMarks.IsPinned(row.Record.Id));
            if (_mailMarks.IsViewed(row.Record.Id)) row.SetViewed();
            if (_session?.HasKnownAnomaly(row.Record.Id) == true) row.SetAnomaly();
            row.PropertyChanged += OnResultChecked;
        }
        _appliedRequest = request;
        _result = result;
        ResultsGrid.ItemsSource = rows;
        ApplyMailboxSort();
        if (rows.Length > 0)
        {
            ResultRow? selected = rows.FirstOrDefault(row => row.Record.Id == previous);
            if (selected is null && (!selectFirst || rows.Length == 1))
                selected = rows[0];
            ResultsGrid.SelectedItem = selected;
            if (selected is not null)
                ResultsGrid.ScrollIntoView(selected);
            else
                ShowRecord(null);
        }
        else
        {
            ShowRecord(null);
        }
        UpdateCommands();
    }

    private void RefreshSearchSummary(bool pending)
    {
        if (_result is not { } result) return;
        EmptyResultsHint.Visibility = result.Hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyResultsHint.Text = _appliedRequest.BookmarkRecordIds is not null && _loadedBookmarkCount == 0
            ? L("현재 열린 자료에서 북마크한 메일이 없습니다.", "There are no bookmarked messages in the open sources.")
            : _appliedRequest.AnomaliesOnly && _session?.AnomalousMailCount == 0
            ? L("확인한 자료에서 이상 징후가 있는 메일을 찾지 못했습니다.", "No messages with anomalies were found in the examined sources.")
            : L("검색 결과가 없습니다.", "No matching messages.");
        SearchRequest request = _appliedRequest;
        string scope = request.SourceId is { } sourceId
            ? _session?.Sources.FirstOrDefault(source => source.Id == sourceId)?.Path ?? sourceId
            : L("전체 자료", "All sources");
        string partial = result.Issues.Count == 0 ? "" : $" · {L("일부 항목 확인 불가", "Partial coverage")}";
        bool updatingQuery = _searchTyping.IsEnabled || _liveSearch is not null || _composingSearch;
        string unapplied = updatingQuery ? $" · {L("검색 중…", "Searching…")}" :
            pending ? $" · {L("미적용 조건 있음", "Unapplied filters")}" : "";
        SearchSummary.Text = L($"{result.Hits.Count:N0}건", $"Results: {result.Hits.Count:N0}") + partial + unapplied;
        ExactSearchContext.Visibility = request.Exact is null ? Visibility.Collapsed : Visibility.Visible;
        if (request.Exact is { } key)
        {
            ExactSearchLabel.Text = key.Kind switch
            {
                InvestigationKeyKind.Address => L("같은 메일 주소", "Same email address"),
                InvestigationKeyKind.Host => L("같은 호스트", "Same host"),
                InvestigationKeyKind.Url => L("같은 URL", "Same URL"),
                InvestigationKeyKind.AttachmentSha256 => L("동일 첨부 SHA-256", "Same attachment SHA-256"),
                InvestigationKeyKind.MessageId => L("같은 Message-ID", "Same Message-ID"),
                InvestigationKeyKind.RecordId => L("참조 메일", "Referenced message"),
                _ => L("같은 값", "Exact match")
            };
            ExactSearchValue.Text = key.Kind == InvestigationKeyKind.RecordId && result.Hits.FirstOrDefault() is { } hit
                ? hit.Record.SourcePath : key.Value;
            AutomationProperties.SetName(ExactSearchValue, ExactSearchLabel.Text);
        }
        else ExactSearchValue.Clear();
        if (result.InvestigationUnavailableCount > 0)
            SearchSummary.Text += L($" · 일부 확인 불가 {result.InvestigationUnavailableCount:N0}건",
                $" · {result.InvestigationUnavailableCount:N0} partially assessable");
        SearchSummary.ToolTip = $"{scope}\n{DescribeSearch(request)}\n{DescribeResultCoverage(result)}"
            + (result.Issues.Count == 0 ? "" : "\n\n" + FormatIssues(result.Issues.Take(25), _session?.Sources ?? []));
    }

    private static string DescribeResultCoverage(SearchResult result)
    {
        string dates = result.DateUnknownCount == 0 ? "" : $" · {L("시각 미확정", "Unknown dates")} {result.DateUnknownCount:N0}";
        return $"{L("판독 범위", "Examined")} {result.ExaminedCount:N0}{L("건", "")}{dates}";
    }


    private void FilterChanged(object? sender, RoutedEventArgs e) => UpdateCommands();

    private void OnSearchSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingPstTree)
        {
            RestorePstTreeSelection(new SearchRequest(SourceId: SelectedSourceId));
            UpdateCommands();
        }
    }

    private void ShowSearchCoverage(object sender, RoutedEventArgs e)
    {
        if (_result is not { Issues.Count: > 0 } result) return;
        string status = FormatIssues(result.Issues, _session?.Sources ?? []);
        var report = new TextBox
        {
            Text = status,
            Style = (Style)FindResource("Copyable"),
            Padding = new Thickness(10)
        };
        var window = new Window
        {
            Owner = this,
            Title = L("판독 문제", "Read issues"),
            Width = 640,
            Height = 370,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = report
        };
        Appearance.ApplyToWindow(window);
        window.SetResourceReference(BackgroundProperty, "Viewer.Canvas");
        window.SetResourceReference(ForegroundProperty, "Viewer.Text");
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) window.Close();
        };
        window.ShowDialog();
    }

    private void OnResultChecked(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ResultRow row && e.PropertyName == nameof(ResultRow.Checked))
        {
            if (row.Checked) _checkedRecords.Add(row.Record.Id);
            else _checkedRecords.Remove(row.Record.Id);
            UpdateCommands();
        }
    }

    private bool CanChooseSources => _work is null && ExportPanel.Visibility != Visibility.Visible;

    private CancellationTokenSource StartWork(string status)
    {
        StopLiveSearch();
        var work = new CancellationTokenSource();
        _work = work;
        WorkStatus.Text = status;
        ProgressIndicator.Visibility = Visibility.Visible;
        ProgressIndicator.IsIndeterminate = true;
        CancelWorkButton.IsEnabled = true;
        UpdateCommands();
        return work;
    }

    private void EndWork(CancellationTokenSource work)
    {
        _work = null;
        work.Dispose();
        ProgressIndicator.Visibility = Visibility.Collapsed;
        ProgressIndicator.IsIndeterminate = false;
        CancelWorkButton.IsEnabled = false;
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        RefreshIntakeCommands();
        FileMenu.IsEnabled = _work is null && ExportPanel.Visibility != Visibility.Visible;
        SearchButton.IsEnabled = CanChooseSources && _session is not null;
        ShowSettingsMenuItem.IsEnabled = CanChooseSources;
        SearchPane.IsEnabled = _work is null;
        FilterOptions.IsEnabled = _work is null;
        var draft = CaptureSearch();
        bool pending = _result is not null && draft != _appliedRequest;
        bool folderScopePending = _result is not null &&
            (!Equals(draft.SourceGroup, _appliedRequest.SourceGroup) ||
             (IsFolderStoreSource(draft.SourceId) || IsFolderStoreSource(_appliedRequest.SourceId)) &&
             (draft.PstFolderNodeId != _appliedRequest.PstFolderNodeId ||
              draft.IncludePstSubfolders != _appliedRequest.IncludePstSubfolders || draft.SourceId != _appliedRequest.SourceId));
        RefreshPstControls();
        RefreshSearchSummary(pending);
        RefreshMailboxFilterCaption();
        RefreshMailboxTabs();
        RefreshInvestigationCommands();
        bool partialCoverage = _result is { Issues.Count: > 0 };
        CoverageButton.Visibility = partialCoverage ? Visibility.Visible : Visibility.Collapsed;
        CoverageButton.IsEnabled = CanChooseSources && partialCoverage;
        CoverageButton.Content = L("읽지 못한 항목", "Read issues");
        int checkedCount = _checkedRecords.Count == 0 ? 0 : ResultRows().Count(row => row.Checked);
        bool selecting = SelectionModeButton.IsChecked == true;
        SelectionModeButton.IsEnabled = CanChooseSources && (selecting || _result is { Hits.Count: > 0 });
        ReaderBookmarkButton.IsEnabled = ReaderPinButton.IsEnabled = CanChooseSources && _activeRow is not null;
        SelectionModeButton.Content = selecting ? L("여러 메일 선택 종료", "End multi-select") : L("여러 메일 선택", "Select messages");
        MailboxCheckColumn.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        if (selecting)
            SearchSummary.Text += L($" · {checkedCount:N0}건 선택", $" · {checkedCount:N0} selected");

        ExportMenu.Visibility = _session is null ? Visibility.Collapsed : Visibility.Visible;
        bool canExportSearch = CanChooseSources && !pending && _result is { Hits.Count: > 0 };
        ExportSearchButton.IsEnabled = canExportSearch;
        ExportSearchButton.Header = _result is { Issues.Count: > 0 }
            ? L("판독한 검색 결과", "Readable search results") : L("검색 결과", "Search results");
        bool canExportSelected = CanChooseSources && !folderScopePending && _activeRow is not null;
        ExportSelectedButton.IsEnabled = canExportSelected;
        ExportSelectedButton.Header = L("현재 메일", "Current message");
        bool canExportChecked = CanChooseSources && !folderScopePending && checkedCount > 0;
        ExportCheckedButton.Visibility = selecting && checkedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportCheckedButton.IsEnabled = canExportChecked;
        ExportCheckedButton.Content = L("내보내기", "Export");
        string checkedExportLabel = L($"선택한 메일 {checkedCount:N0}건 내보내기", $"Export {checkedCount:N0} selected messages");
        ExportCheckedButton.ToolTip = checkedExportLabel;
        AutomationProperties.SetName(ExportCheckedButton, checkedExportLabel);
        BookmarkCheckedButton.Visibility = ExportCheckedButton.Visibility;
        BookmarkCheckedButton.IsEnabled = canExportChecked;
        bool removeBookmarks = checkedCount > 0 && !ResultRows().Any(row => row.Checked && !row.Bookmarked);
        BookmarkCheckedButton.Content = removeBookmarks ? L("북마크 해제", "Remove bookmarks") : L("북마크", "Bookmark");
        string bookmarkLabel = removeBookmarks
            ? L($"선택한 메일 {checkedCount:N0}통의 북마크 해제", $"Remove bookmarks from {checkedCount:N0} selected messages")
            : L($"선택한 메일 {checkedCount:N0}통 북마크", $"Bookmark {checkedCount:N0} selected messages");
        BookmarkCheckedButton.ToolTip = bookmarkLabel;
        AutomationProperties.SetName(BookmarkCheckedButton, bookmarkLabel);
        ExportAttachmentButton.Header = L("첨부파일 근거 묶음", "Attachment evidence package");

        bool headerAvailable = _activeRow?.Record is { } record
            && (record.ContainerHeaders is { Length: > 0, CanRead: true } || (record.Format switch
            {
                MailFormat.Msg => record.TransportHeaderStream is { Length: > 0 },
                MailFormat.Pst or MailFormat.Ost => record.TransportHeaderProperty is { Length: > 0 },
                _ => record.ContainerMessage is null && record.HeaderRange.Length > 0
            }));
        bool attachmentBytesAvailable = AttachmentsGrid.SelectedItem is MailAttachment attachment
            && (attachment.CompoundStream is { Length: > 0 } || attachment.EncodedRange is { Length: > 0 }
                || attachment.PstProperty is { Length: > 0 } || attachment.ContainerLocation is { Length: > 0, CanRead: true });
        bool attachmentExportAvailable = AttachmentsGrid.SelectedItem is MailAttachment
            { Sha256: not null, DecodedLength: not null, Issue: null } exportAttachment
            && (_activeRow?.Record.Format.IsFolderStore() != true || exportAttachment.PstProperty is not null ||
                exportAttachment.ContainerLocation is { CanRead: true });
        if (_headerSourceBytes is { } headerButton)
            SetEvidenceAction(headerButton, headerAvailable);
        SetEvidenceAction(OpenAttachmentBytesButton, attachmentBytesAvailable);
        bool canExportAttachment = CanChooseSources && !folderScopePending && attachmentExportAvailable;
        ExportAttachmentButton.IsEnabled = canExportAttachment;
        SaveAttachmentFileButton.IsEnabled = canExportAttachment;
        SaveAttachmentFileButton.ToolTip = !attachmentExportAvailable && AttachmentsGrid.SelectedItem is MailAttachment
            ? L("선택한 첨부파일은 개별 파일로 추출할 수 없습니다.", "The selected attachment cannot be extracted as a file.")
            : null;
        ExportMenu.IsEnabled = canExportSearch || canExportSelected || canExportAttachment;
        bool rawWholeAvailable = _rawDecodedBytes is null && (_rawSourceRecord ?? _activeRow?.Record) is { } activeRecord &&
            (activeRecord.ContainerMessage is { } message
                ? activeRecord.CanReadMessage && (_rawContainer != (message.FileRange is null ? message : null) ||
                    _rawRange != (message.FileRange ?? new ByteRange(0, message.Length)))
                : activeRecord.Format.IsFolderStore()
                    ? _rawPstProperty is { } property && _rawRange != new ByteRange(0, property.Length)
                    : _rawStream is not null || _rawRange != activeRecord.MessageRange);
        if (_rawSourceRecord is { } rawSource)
            RawMessageButton.Content = rawSource.Format.IsFolderStore() ? L("전체 속성", "Whole property")
                : rawSource.Format == MailFormat.Msg ? L("전체 MSG", "Whole MSG") : L("메시지 범위", "Message range");
        RawMessageButton.IsEnabled = CanChooseSources && rawWholeAvailable;
        RawMessageButton.Visibility = rawWholeAvailable ? Visibility.Visible : Visibility.Collapsed;
        RawCopyLocationButton.IsEnabled = _rawLocationFields.Length > 0 && CanChooseSources;
        RawCopyLocationButton.Content = L("위치 복사", "Copy location");
        RawCopyLocationButton.ToolTip = RawCopyLocationButton.Content;
        RawPager.Visibility = _rawRange.Length > RawPageBytes ? Visibility.Visible : Visibility.Collapsed;
        if (_rawRange.Length > RawPageBytes)
            RawPageIndicator.Text = $"{_rawPage / RawPageBytes + 1:N0} / {(_rawRange.Length - 1) / RawPageBytes + 1:N0}";
        RawPreviousButton.IsEnabled = CanChooseSources && _rawPage > 0;
        RawNextButton.IsEnabled = CanChooseSources && _rawRange.Length - _rawPage > RawPageBytes;
        RawBackButton.Visibility = _rawReturnTab is null ? Visibility.Collapsed : Visibility.Visible;
        RawBackButton.IsEnabled = CanChooseSources && _rawReturnTab is not null;
        if (_rawReturnTab is { } origin)
        {
            var (label, description) = origin == InvestigationCluesTab
                ? (L("이상 징후", "Anomalies"), L("이상 징후로 돌아가기", "Back to anomalies"))
                : origin == HeadersTab
                ? (L("헤더 원문", "Raw headers"), L("헤더 원문으로 돌아가기", "Back to raw headers"))
                : origin == AttachmentsTab
                    ? (L("첨부파일", "Attachments"), L("첨부파일로 돌아가기", "Back to attachments"))
                    : (L("메일 정보", "Message information"), L("메일 정보로 돌아가기", "Back to message information"));
            RawBackButton.Content = label;
            RawBackButton.ToolTip = description;
            AutomationProperties.SetName(RawBackButton, description);
        }
        SaveExportButton.IsEnabled = _work is null && _pendingExport is not null && _exportResult is null;
        VerifyExportButton.IsEnabled = _work is null && _exportResult is not null;
        ReturnToSearchButton.IsEnabled = _work is null;
    }

    private void SetEvidenceAction(Button button, bool available)
    {
        button.IsEnabled = available && CanChooseSources;
        button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<ResultRow> ResultRows() => ResultsGrid.Items.Cast<ResultRow>();

    private void CancelWork(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        CancelWorkButton.IsEnabled = false;
        WorkStatus.Text = L("작업을 취소하는 중입니다.", "Canceling operation…");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_work is { } work)
        {
            work.Cancel();
            e.Cancel = true;
            return;
        }
        StopLiveSearch();
        _anomalyCountWork?.Cancel();
        _session?.Dispose();
        _session = null;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Escape && ExportPanel.Visibility == Visibility.Visible && _work is null)
        {
            e.Handled = true;
            if (_pendingExport is not null && _exportResult is null)
                CancelExportConfirmation();
            else
                ReturnToSearch(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape && WorkspaceTabs.SelectedItem == SourcesTab && CancelIntakeButton.IsVisible && _work is null)
        {
            e.Handled = true;
            CancelIntake(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape && RawBytesBox.IsKeyboardFocusWithin && RawBytesBox.SelectionLength > 1)
        {
            RawBytesBox.SelectByte(RawBytesBox.SelectedOffset);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DetailTabs.SelectedItem == RawTab && RawBackButton.IsEnabled)
        {
            e.Handled = true;
            ReturnToEvidence(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.O && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            e.Handled = true;
            if (modifiers == ModifierKeys.Control) ChooseFiles(this, new RoutedEventArgs());
            else ChooseFolder(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.OemComma && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenIntake(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.F && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            FocusSearch(this, new RoutedEventArgs());
        }
    }

    private void FocusSearch(object sender, RoutedEventArgs e)
    {
        if (_session is not null && CanChooseSources)
        {
            WorkspaceTabs.SelectedItem = ActiveMailboxTab;
            RevealMailboxListForSearch();
            SearchQueryBox.Focus();
            SearchQueryBox.SelectAll();
        }
    }

    private void Exit(object sender, RoutedEventArgs e) => Close();

    private void SwitchTheme(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string mode })
        {
            SetDisplayTheme(mode == "dark");
        }
    }

    private static bool IsExpected(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or NotSupportedException;

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FieldName(SearchField field) => field switch
    {
        SearchField.Any => L("제목 · 주소 · 본문", "Subject, addresses, body"),
        SearchField.Subject => L("제목", "Subject"),
        SearchField.From => L("보낸 사람", "From"),
        SearchField.To => L("받는 사람", "To"),
        SearchField.Cc => L("참조", "Cc"),
        SearchField.Bcc => L("숨은 참조", "Bcc"),
        SearchField.Body => L("본문", "Body"),
        SearchField.AttachmentName => L("첨부파일 이름", "Attachment name"),
        SearchField.MessageId => "Message-ID",
        SearchField.SourcePath => L("원본 경로", "Source path"),
        _ => field.ToString()
    };

    private string DescribeSearch(SearchRequest request)
    {
        var values = new List<string> { $"{L("검색 필드", "Search field")}  {FieldName(request.Field)}", $"{L("검색어", "Search query")}  {request.Query}" };
        if (request.SourceGroup is not null)
            values.Add($"{L("자료 범위", "Source scope")}  {PstScopeCaption(request, fullPath: true)}");
        if (PstSearchPath(request) is { } folder && request.PstFolderNodeId is { } nid)
        {
            string? format = SourceForScope(request.SourceId)?.Format.ToString().ToUpperInvariant();
            string identity = _session?.PstFolders.Any(item => item.SourceId == request.SourceId && item.NodeId == nid && item.IsLogicalPath) == true
                ? "" : $" · NID 0x{nid:X8}";
            values.Add($"{format} {L("폴더", "folder")}  {folder}{identity}");
        }
        if (request.From is { } from) values.Add($"{L("보낸 사람", "From")}  {from}");
        if (request.To is { } to) values.Add($"{L("받는 사람", "To")}  {to}");
        if (request.Subject is { } subject) values.Add($"{L("제목", "Subject")}  {subject}");
        if (request.FromDateInclusive is { } start) values.Add($"{L("메시지 날짜 시작", "Start date")}  {start:yyyy-MM-dd} (UTC)");
        if (request.ToDateExclusive is { } end) values.Add($"{L("메시지 날짜 종료", "End date")}  {end.AddDays(-1):yyyy-MM-dd} (UTC)");
        if (request.HasAttachment.HasValue) values.Add(request.HasAttachment.Value ? L("첨부 있음", "With attachments") : L("첨부 없음", "Without attachments"));
        foreach (var (clue, _) in _investigationChoices)
            if ((request.Clues & clue) != 0) values.Add(ClueName(clue));
        if (request.AnomaliesOnly) values.Add(L("이상 징후가 있는 메일만", "Messages with anomalies only"));
        if (request.Exact is { } key) values.Add($"{L("같은 값", "Exact match")}  {key.Value}");
        return string.Join(Environment.NewLine, values);
    }

    private void RefreshSourceReport()
    {
        var text = new StringBuilder();
        if (_unappliedSourceReport is { } attempt)
        {
            text.AppendLine(L("현재 조사에 적용되지 않은 자료", "Sources outside the current investigation"))
                .Append(FormatSourceReport(attempt.Sources, attempt.Issues));
            if (_session is not null) text.AppendLine().AppendLine();
        }
        if (_session is { } session)
        {
            if (_unappliedSourceReport is not null)
                text.AppendLine(L("현재 조사 자료", "Current investigation sources"));
            text.Append(FormatSourceReport(session.Sources, session.Issues));
        }
        SourcesReportBox.Text = text.ToString();
        SourcesReportPanel.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_unappliedSourceReport is not null)
            SourcesReportPanel.IsExpanded = true;
    }

    private static string FormatSourceReport(IReadOnlyList<SourceInfo> sources, IReadOnlyList<ReadIssue> issues)
    {
        var text = new StringBuilder();
        foreach (SourceInfo source in sources)
        {
            text.AppendLine(source.Path).Append("  ").Append(source.Format == MailFormat.MailboxIndex
                ? source.DetectedFormat ?? L("메일함 색인", "Mailbox index") : StoreFormatName(source.Format));
            if (source.StoreLayout is { } layout) text.Append(" · ").Append(DescribeStoreLayout(layout));
            text.AppendLine(L($" · {source.Length:N0}바이트", $" · {source.Length:N0} bytes"))
                .Append("  SHA-256  ").AppendLine(source.Sha256).Append("  ").AppendLine(source.BoundaryRule);
            if (source.IndexSummary is { } index)
            {
                text.Append("  ").AppendLine(L(
                    $"폴더 {index.FolderCount:N0} · 색인 항목 {index.EntryCount:N0} · MH 시퀀스 {index.SequenceCount:N0}",
                    $"Folders {index.FolderCount:N0} · index entries {index.EntryCount:N0} · MH sequences {index.SequenceCount:N0}"));
                foreach (string name in index.Names) text.Append("    ").AppendLine(name);
            }
            text.AppendLine();
        }
        var ids = sources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        var unread = issues.Where(issue => !ids.Contains(issue.SourceId)).ToArray();
        var partial = issues.Where(issue => ids.Contains(issue.SourceId)).ToArray();
        if (unread.Length > 0)
        {
            text.AppendLine(L("판독하지 못한 자료", "Unread sources")).AppendLine(FormatIssues(unread, sources));
        }
        if (partial.Length > 0)
        {
            if (unread.Length > 0) text.AppendLine();
            text.AppendLine(L("판독 범위 제한", "Read coverage limits")).Append(FormatIssues(partial, sources));
        }
        return text.ToString();
    }

    private static string DescribeStoreLayout(StoreLayoutInfo layout)
    {
        string version = layout.Version switch
        {
            14 or 15 => $"ANSI v{layout.Version}",
            21 or 23 or 36 => $"Unicode v{layout.Version}",
            _ => L($"버전 {layout.Version}", $"Version {layout.Version}")
        };
        string? page = layout.PageSize switch
        {
            512 => L("512 B 페이지", "512 B pages"),
            4096 => L("4 KiB 페이지", "4 KiB pages"),
            _ => null
        };
        string? encoding = layout.BlockEncoding switch
        {
            0 => L("블록 인코딩 없음", "No block encoding"),
            1 => L("Permute 인코딩", "Permute encoding"),
            2 => L("Cyclic 인코딩", "Cyclic encoding"),
            0x10 => L("Windows 정보 보호", "Windows Information Protection"),
            null => null,
            _ => L("알 수 없는 블록 인코딩", "Unknown block encoding")
        };
        return version + (page is null ? "" : $" · {page}")
            + (encoding is null ? "" : $" · {encoding} (0x{layout.BlockEncoding:X2})");
    }

    private static string FormatIssues(IEnumerable<ReadIssue> issues, IReadOnlyList<SourceInfo> sources)
    {
        var paths = sources.ToDictionary(source => source.Id, source => source.Path, StringComparer.Ordinal);
        return string.Join(Environment.NewLine, issues.Select(issue =>
        {
            string? path = issue.SourcePath ?? paths.GetValueOrDefault(issue.SourceId);
            string location = issue.Range is { } range ? $" · {range}" : "";
            if (issue.PstFolderNodeId is { } nid)
                location += $" · {L("폴더 NID", "Folder NID")} 0x{nid:X8}";
            string field = issue.Field is { } value ? $" · {FieldName(value)}" : "";
            string detail = issue.Code switch
            {
                "ost-unsupported-version" => L("이 OST 버전은 지원 범위 밖입니다.", "This OST version is outside the supported range."),
                "ost-unsupported-encoding" when issue.StoreLayout?.BlockEncoding == 0x10 =>
                    L("Windows 정보 보호가 적용된 OST는 열 수 없습니다.", "Cannot read an OST protected by Windows Information Protection."),
                "ost-unsupported-encoding" => L("이 OST 블록 인코딩은 지원 범위 밖입니다.", "This OST block encoding is outside the supported range."),
                _ => issue.Detail
            };
            if (path is not null && detail.StartsWith($"{path}: ", StringComparison.OrdinalIgnoreCase))
                detail = detail[(path.Length + 2)..];
            string? layout = issue.StoreLayout is { } detected ? DescribeStoreLayout(detected) : null;
            string heading = path is null ? "" : $"{path}{location}{field}\n  ";
            return heading + (layout is null ? "" : $"{layout} · ") + $"{detail} ({issue.Code})";
        }));
    }

    private sealed record Choice<T>(T Value, string Label, string? Description = null)
    {
        public string AccessibleLabel => Description ?? Label;
        public override string ToString() => Label;
    }

    private sealed class ResultRow(SearchHit hit, bool isChecked, string? pstFolderName) : INotifyPropertyChanged
    {
        private bool _checked = isChecked;
        public MailRecord Record { get; } = hit.Record;
        public string? PstFolderName { get; } = pstFolderName;
        public string? PstFolderIdentity => Record.PstMessage is { } message
            ? $"{message.FolderPath} · {L("폴더 NID", "Folder NID")} {message.FolderNid} · "
                + $"{L("메시지 NID", "Message NID")} {message.MessageNid}{Environment.NewLine}{Record.SourcePath}" : null;
        public IReadOnlyList<SearchMatch> SearchMatches { get; } = hit.Matches;
        public string Timestamp => Record.Date?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            ?? L("확인되지 않음", "Unknown");
        public string Matches => string.Join(", ", SearchMatches.Select(match =>
            match.PartPath is { } part ? $"{FieldName(match.Field)} {part}" : FieldName(match.Field)));
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
            }
        }
        public bool Bookmarked { get; private set; }
        public bool Pinned { get; private set; }
        public bool Viewed { get; private set; }
        public bool HasAnomaly { get; private set; }
        public string AccessibleName => HasAnomaly
            ? $"{Record.Subject}, {ViewedLabel}, {L("이상 징후 있음", "Has anomalies")}" : $"{Record.Subject}, {ViewedLabel}";
        private string ViewedLabel => Viewed ? L("읽음", "Read") : L("읽지 않음", "Unread");

        public void SetViewed()
        {
            if (Viewed) return;
            Viewed = true;
            Notify(nameof(Viewed));
            Notify(nameof(AccessibleName));
        }

        public void SetAnomaly()
        {
            if (HasAnomaly) return;
            HasAnomaly = true;
            Notify(nameof(HasAnomaly));
            Notify(nameof(AccessibleName));
        }
        public string BookmarkAction => Bookmarked ? L("북마크 해제", "Remove bookmark") : L("북마크 추가", "Bookmark message");
        public string PinAction => Pinned ? L("상단 고정 해제", "Unpin message") : L("상단에 고정", "Pin message");
        public string DateSection => Pinned ? L("고정됨", "Pinned") : MailboxDateSection(Record.Date);
        public string PinSection => Pinned ? L("고정됨", "Pinned") : "";
        public string LocalListDate => Record.Date?.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

        public void SetMarks(bool bookmarked, bool pinned)
        {
            if (Bookmarked != bookmarked)
            {
                Bookmarked = bookmarked;
                Notify(nameof(Bookmarked));
                Notify(nameof(BookmarkAction));
            }
            if (Pinned != pinned)
            {
                Pinned = pinned;
                Notify(nameof(Pinned));
                Notify(nameof(PinAction));
                Notify(nameof(DateSection));
                Notify(nameof(PinSection));
            }
        }

        private MailAttachment? FirstAttachment => Record.Attachments.FirstOrDefault(item =>
            !item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) ?? Record.Attachments.FirstOrDefault();
        public RowThumbnail? ListThumbnail { get; private set; }
        public MailAttachment? ListAttachment => FirstAttachment is { } item && item.Id != ListThumbnail?.AttachmentId ? item : null;
        public int AdditionalAttachments => Record.Attachments.Count - (ListAttachment is null ? 0 : 1)
            - (ListThumbnail?.AttachmentId is null ? 0 : 1);
        public string MoreAttachmentsLabel => AdditionalAttachments > 0 ? $"+{AdditionalAttachments:N0}" : "";
        public string AttachmentsAction => L($"첨부파일 {Record.Attachments.Count:N0}개 보기", $"View {Record.Attachments.Count:N0} attachments");
        public string ThumbnailAction => ListThumbnail?.AttachmentId is null
            ? L("본문 이미지 보기", "View image in message") : L("이미지 첨부 보기", "View image attachment");

        public void SetListThumbnail(RowThumbnail? thumbnail)
        {
            if (Equals(ListThumbnail, thumbnail)) return;
            ListThumbnail = thumbnail;
            Notify(nameof(ListThumbnail));
            Notify(nameof(ListAttachment));
            Notify(nameof(MoreAttachmentsLabel));
            Notify(nameof(ThumbnailAction));
        }

        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void RefreshLanguage()
        {
            Notify(nameof(AccessibleName));
            Notify(nameof(PinAction));
            Notify(nameof(BookmarkAction));
            Notify(nameof(DateSection));
            Notify(nameof(PinSection));
            Notify(nameof(AttachmentsAction));
            Notify(nameof(ThumbnailAction));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Timestamp)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Matches)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PstFolderIdentity)));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
