using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MailViewer.Core;
using EvidenceImage;
using Microsoft.Win32;

namespace MailViewer.App;

public partial class MainWindow
{
    private readonly ObservableCollection<InputCandidate> _candidates = [];
    private readonly Dictionary<string, string> _intakeFolders = new(StringComparer.OrdinalIgnoreCase);
    private IInputElement? _intakeReturnFocus;
    private Window? _startupIntake;
    private Window IntakeOwner => _startupIntake ?? this;

    internal void ShowStartupIntake()
    {
        if (_session is not null || _startupIntake is not null || _work is not null ||
            !IsVisible || WorkspaceTabs.SelectedItem != SourcesTab) return;

        var surface = new DockPanel();
        surface.SetResourceReference(Panel.BackgroundProperty, "Viewer.Canvas");
        var progress = new DockPanel { Margin = new Thickness(24, 0, 24, 10) };
        var cancelWork = new Button
        {
            Content = L("작업 취소", "Cancel work"), Style = (Style)FindResource("QuietAction")
        };
        cancelWork.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = CancelWorkButton });
        cancelWork.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled)) { Source = CancelWorkButton });
        cancelWork.Click += CancelWork;
        DockPanel.SetDock(cancelWork, Dock.Right);
        progress.Children.Add(cancelWork);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = WorkStatus });
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        progress.Children.Add(status);
        DockPanel.SetDock(progress, Dock.Bottom);
        surface.Children.Add(progress);

        Rect area = SystemParameters.WorkArea;
        var dialog = new Window
        {
            Owner = this, Title = L("메일 자료 열기", "Open mail sources"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Icon = Icon, FontFamily = FontFamily, Language = Language,
            Width = Math.Min(760, area.Width - 24), Height = Math.Min(520, area.Height - 24),
            MinWidth = Math.Min(520, area.Width - 24), MinHeight = Math.Min(190, area.Height - 24),
            MaxHeight = area.Height - 24, SizeToContent = _candidates.Count == 0 ? SizeToContent.Height : SizeToContent.Manual
        };
        Appearance.ApplyToWindow(dialog);
        dialog.SetResourceReference(BackgroundProperty, "Viewer.Canvas");
        dialog.SetResourceReference(ForegroundProperty, "Viewer.Text");
        NameScope.SetNameScope(dialog, NameScope.GetNameScope(this));
        System.Windows.Automation.AutomationProperties.SetAutomationId(dialog, "StartupIntakeWindow");
        dialog.Loaded += (_, _) =>
        {
            FocusManager.SetFocusedElement(dialog, OpenFilesButton);
            OpenFilesButton.Focus();
        };
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                dialog.Close();
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ChooseFiles(dialog, e);
            }
        };
        dialog.Closing += (_, e) =>
        {
            if (_work is null || _session is not null) return;
            e.Cancel = true;
            CancelWork(dialog, new RoutedEventArgs());
        };

        _startupIntake = dialog;
        try
        {
            SourcesTab.Content = null;
            DisplaySettingsSection.Visibility = Visibility.Collapsed;
            surface.Children.Add(IntakePane);
            dialog.Content = surface;
            RefreshIntakeCommands();
            dialog.ShowDialog();
        }
        finally
        {
            surface.Children.Remove(IntakePane);
            dialog.Content = null;
            SourcesTab.Content = IntakePane;
            DisplaySettingsSection.Visibility = Visibility.Visible;
            _startupIntake = null;
            RefreshIntakeCommands();
        }
    }

    private void InitializeIntake(IReadOnlyList<string> paths)
    {
        CandidateGrid.ItemsSource = _candidates;
        CandidateGrid.PreviewKeyDown += OnCandidateKeyDown;
        AddCandidates(paths.Where(path => !Directory.Exists(path) && !IsDiskImage(path)));
        string[] folders = paths.Where(Directory.Exists).ToArray();
        string[] images = paths.Where(path => !Directory.Exists(path) && IsDiskImage(path)).ToArray();
        Loaded += async (_, _) =>
        {
            if (WorkspaceTabs.SelectedItem != SourcesTab) return;
            if (folders.Length > 0)
                await AddFoldersAsync(folders);
            OpenDiskImages(images);
        };
    }

    private void OpenIntake(object sender, RoutedEventArgs e)
    {
        if (CanChooseSources) ShowIntake();
    }

    private void ShowIntake()
    {
        if (WorkspaceTabs.SelectedItem != SourcesTab)
        {
            _intakeReturnFocus = Keyboard.FocusedElement;
            WorkspaceTabs.SelectedItem = SourcesTab;
        }
    }

    private void CancelIntake(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources) return;
        if (_startupIntake is { } dialog)
        {
            dialog.Close();
            return;
        }
        if (_candidates.Count > 0) ClearPendingFiles(sender, e);
        else WorkStatus.Text = "";
        if (_session is not null) ReturnToInvestigation();
    }

    private void ReturnToInvestigation()
    {
        _startupIntake?.Close();
        WorkspaceTabs.SelectedItem = ActiveMailboxTab;
        IInputElement? previous = _intakeReturnFocus;
        _intakeReturnFocus = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (WorkspaceTabs.SelectedItem != ActiveMailboxTab) return;
            if (previous is FrameworkElement { IsVisible: true, IsEnabled: true } and not GridSplitter &&
                !ReferenceEquals(previous, PstFoldersToggle) &&
                Keyboard.Focus(previous) == previous)
                return;
            if (!ResultsGrid.Focus())
                SearchQueryBox.Focus();
        }));
    }

    private void ChooseFiles(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources) return;
        var dialog = new OpenFileDialog
        {
            Title = L("원본 메일 파일 선택", "Select original mail files"),
            Filter = L("메일 파일과 저장소", "Mail files and stores") + "|"
                + string.Join(';', EvidenceSession.MailFileExtensions.Select(ext => "*" + ext).Append("winmail.dat").Append(".mh_sequences"))
                + "|" + DiskImageFilter() + "|"
                + L("모든 파일 (*.*)", "All files (*.*)") + "|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(IntakeOwner) != true) return;
        ShowIntake();
        AddCandidates(dialog.FileNames.Where(path => !IsDiskImage(path)));
        OpenDiskImages(dialog.FileNames.Where(IsDiskImage).ToArray());
    }

    private static string DiskImageFilter() =>
        L("디스크 이미지", "Disk images") + "|" + ImageFileTypes.PickerPatterns;

    private static bool IsDiskImage(string path) => ImageFileTypes.HasImageExtension(path);

    private void ChooseDiskImage(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources) return;
        var picker = new OpenFileDialog
        {
            Title = L("디스크 이미지 선택", "Select a disk image"),
            Filter = DiskImageFilter() + "|" + L("모든 파일 (*.*)", "All files (*.*)") + "|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (picker.ShowDialog(IntakeOwner) == true) OpenDiskImages(picker.FileNames);
    }

    private void OpenDiskImages(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !CanChooseSources) return;
        if (ImageFileTypes.FirstSelectedSegment(paths) is { } first) paths = [first];
        ShowIntake();
        foreach (string path in paths)
        {
            var dialog = new ImageSourcesWindow(path, L) { Owner = IntakeOwner, Icon = Icon };
            if (dialog.ShowDialog() == true)
                foreach (var file in dialog.ExtractedFiles)
                    AddInputCandidates([file.Path], null, false, file.Origin);
        }
    }

    private async void ChooseFolder(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources) return;
        var dialog = new OpenFolderDialog { Title = L("메일이 들어 있는 폴더 선택", "Select a mail folder") };
        if (dialog.ShowDialog(IntakeOwner) != true) return;
        ShowIntake();
        await AddFoldersAsync([dialog.FolderName]);
    }

    private void OpenFindSources(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources) return;
        var dialog = new FindSourcesWindow(L) { Owner = IntakeOwner, Icon = Icon };
        if (dialog.ShowDialog() == true)
            foreach (var hit in dialog.SelectedSources)
                AddCandidates([hit.Path], hit.Root, located: true);
    }

    private void OnIntakeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanChooseSources && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnIntakeDrop(object sender, DragEventArgs e)
    {
        if (!CanChooseSources || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        AddCandidates(paths.Where(path => !Directory.Exists(path) && !IsDiskImage(path)));
        string[] folders = paths.Where(Directory.Exists).ToArray();
        if (folders.Length > 0)
            await AddFoldersAsync(folders);
        OpenDiskImages(paths.Where(path => !Directory.Exists(path) && IsDiskImage(path)).ToArray());
    }

    private async Task AddFoldersAsync(IReadOnlyList<string> roots)
    {
        var work = StartWork(L("폴더에서 메일 파일을 찾는 중입니다.", "Finding mail files in the selected folders."));
        try
        {
            FolderSearchResult result = await Task.Run(() => FolderSourceSearch.Scan(roots, true, work.Token), work.Token);
            work.Token.ThrowIfCancellationRequested();
            foreach (var group in result.Hits.GroupBy(hit => hit.Root))
                AddCandidates(group.Select(hit => hit.Path), group.Key);
            WorkStatus.Text = result.Hits.Count == 0
                ? L("선택한 폴더에서 지원하는 메일 파일을 찾지 못했습니다.", "No supported mail files were found in the selected folders.")
                : L($"폴더에서 찾은 후보 {result.Hits.Count:N0}개를 확인하세요.", $"Review {result.Hits.Count:N0} files found in the selected folders.");
            if (result.Issues.Count > 0)
            {
                WorkStatus.Text += " " + L($"읽지 못한 위치 {result.Issues.Count:N0}개", $"{result.Issues.Count:N0} unread locations");
                string scanReport = string.Join(Environment.NewLine, result.Issues);
                SourcesReportBox.Text = string.IsNullOrWhiteSpace(SourcesReportBox.Text)
                    ? scanReport : scanReport + Environment.NewLine + Environment.NewLine + SourcesReportBox.Text;
                SourcesReportPanel.Visibility = Visibility.Visible;
                SourcesReportPanel.IsExpanded = true;
            }
        }
        catch (OperationCanceledException)
        {
            WorkStatus.Text = L("폴더 검색을 취소했습니다.", "Folder scan canceled.");
            if (_session is not null)
                ReturnToInvestigation();
        }
        finally
        {
            EndWork(work);
        }
    }

    private void AddCandidates(IEnumerable<string> paths, string? root = null, bool located = false) =>
        AddInputCandidates(paths, root, located, null);

    private void AddInputCandidates(IEnumerable<string> paths, string? root, bool located, DiskImageOrigin? imageOrigin)
    {
        var existing = _candidates.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var loaded = _session?.Sources
            .SelectMany(source => new[] { source.Path, source.SelectedPath ?? source.Path })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int alreadyLoaded = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var file = new FileInfo(path);
                if (loaded?.Contains(file.FullName) == true)
                {
                    alreadyLoaded++;
                    continue;
                }
                if (existing.TryGetValue(file.FullName, out InputCandidate? previous))
                {
                    CandidateGrid.SelectedItem = previous;
                    continue;
                }
                var item = new InputCandidate(file.FullName, root, located, file.Exists, imageOrigin);
                item.PropertyChanged += OnCandidateChanged;
                _candidates.Add(item);
                existing.Add(item.Path, item);
            }
            catch (Exception error) when (IsExpected(error))
            {
                WorkStatus.Text = L($"파일을 확인하지 못했습니다. {error.Message}", $"Could not check the file. {error.Message}");
            }
        }
        if (alreadyLoaded > 0)
            WorkStatus.Text = L($"이미 분석한 파일 {alreadyLoaded:N0}개를 확인했습니다.", $"{alreadyLoaded:N0} files are already loaded.");
        else if (_candidates.Count > 0)
            WorkStatus.Text = L($"선택한 자료 {_candidates.Count:N0}개를 확인하세요.", $"Review {_candidates.Count:N0} selected sources.");
        RefreshIntakeCommands();
    }

    private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InputCandidate.Included))
            RefreshIntakeCommands();
    }

    private void OnCandidateSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveCandidateButton.IsEnabled = CanChooseSources && CandidateGrid.SelectedItem is InputCandidate;

    private void RemoveCandidate(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources || CandidateGrid.SelectedItem is not InputCandidate item) return;
        item.PropertyChanged -= OnCandidateChanged;
        _candidates.Remove(item);
        RefreshIntakeCommands();
    }

    private void OnCandidateKeyDown(object sender, KeyEventArgs e)
    {
        if (!CanChooseSources || CandidateGrid.SelectedItem is not InputCandidate item) return;
        if (e.Key == Key.Delete)
        {
            RemoveCandidate(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.OriginalSource is not CheckBox && item.Exists)
        {
            item.Included = !item.Included;
            e.Handled = true;
        }
    }

    private void ClearPendingFiles(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources) return;
        foreach (InputCandidate item in _candidates)
            item.PropertyChanged -= OnCandidateChanged;
        _candidates.Clear();
        _unappliedSourceReport = null;
        RefreshSourceReport();
        WorkStatus.Text = L("파일 목록을 비웠습니다.", "Cleared the selected sources.");
        RefreshIntakeCommands();
    }

    private void RefreshIntakeCommands()
    {
        if (_startupIntake is { SizeToContent: SizeToContent.Height } dialog && _candidates.Count > 0)
        {
            dialog.SizeToContent = SizeToContent.Manual;
            dialog.Height = Math.Min(520, dialog.MaxHeight);
        }
        IntakeButtons.IsEnabled = CanChooseSources;
        SettingsLanguageBox.IsEnabled = CanChooseSources;
        CandidateGrid.IsEnabled = _work is null;
        RemoveCandidateButton.IsEnabled = CanChooseSources && CandidateGrid.SelectedItem is InputCandidate;
        ClearPendingButton.IsEnabled = CanChooseSources && _candidates.Count > 0;
        AnalyzeButton.IsEnabled = CanChooseSources && _candidates.Any(item => item.Exists && item.Included);
        AnalyzeButton.IsDefault = AnalyzeButton.IsEnabled;
        AnalyzeButton.Visibility = _session is null || _candidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelIntakeButton.IsEnabled = CanChooseSources;
        CancelIntakeButton.Visibility = _startupIntake is not null || _session is not null || _candidates.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        CancelIntakeButton.Content = _startupIntake is not null ? L("닫기", "Close") :
            _session is not null ? L("메일 목록으로 돌아가기", "Back to mail") : L("선택 취소", "Cancel selection");
    }

    private async void Analyze(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources || WorkspaceTabs.SelectedItem != SourcesTab) return;
        InputCandidate[] inputs = _candidates.Where(item => item.Included && item.Exists).ToArray();
        if (inputs.Length == 0) return;

        var work = StartWork(L($"{inputs.Length:N0}개 파일의 해시를 계산하고 메일을 읽는 중입니다.",
            $"Reading messages and hashing {inputs.Length:N0} sources."));
        EvidenceSession? candidate = null;
        var previous = _session;
        try
        {
            string[] paths = inputs.Select(item => item.Path).ToArray();
            var imageOrigins = inputs.Where(item => item.ImageOrigin is not null)
                .ToDictionary(item => item.Path, item => item.ImageOrigin!, StringComparer.OrdinalIgnoreCase);
            candidate = await Task.Run(() => EvidenceSession.Open(paths, imageOrigins, work.Token), work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (candidate.Records.Count == 0 && candidate.PstFolders.Count == 0)
            {
                _unappliedSourceReport = (candidate.Sources, candidate.Issues);
                RefreshSourceReport();
                WorkStatus.Text = candidate.Sources.Any(source => source.IndexSummary is not null)
                    ? L("메일함 색인 정보는 분석 결과에서 확인할 수 있습니다.", "The read details show the mailbox index information.")
                    : L("읽을 수 있는 메일이 없습니다. 분석 결과를 확인하세요.",
                        "No mail was read. See the read details below.");
                return;
            }
            if (previous is not null && candidate.Issues.Count > 0 && !ConfirmPartialSources(candidate))
            {
                _unappliedSourceReport = (candidate.Sources, candidate.Issues);
                RefreshSourceReport();
                WorkStatus.Text = L("기존 메일 목록을 유지했습니다.", "The new sources were not applied.");
                return;
            }

            SearchRequest request = previous is null ? new SearchRequest() : _appliedRequest;
            SearchRequest draft = CaptureSearch();
            EvidenceSession pending = candidate;
            if (previous is null)
            {
                SearchResult result = await Task.Run(() => pending.Search(request, work.Token), work.Token);
                work.Token.ThrowIfCancellationRequested();
                _session = pending;
                candidate = null;
                ApplyIntakeResults(pending, request, result, draft, inputs, initial: true);
            }
            else
            {
                MailSourceGroup? appliedGroup = request.SourceGroup;
                request = ExpandBookmarksOnIntake(ExpandSourceGroupOnIntake(request, pending.Sources, inputs), pending);
                draft = ReferenceEquals(draft.SourceGroup, appliedGroup)
                    ? draft with { SourceGroup = request.SourceGroup }
                    : ExpandSourceGroupOnIntake(draft, pending.Sources, inputs);
                var (combined, result) = await Task.Run(() => previous.Append(pending, request, work.Token), work.Token);
                _session = combined;
                candidate = null;
                ApplyIntakeResults(combined, request, result, draft, inputs, initial: false);
            }
        }
        catch (OperationCanceledException)
        {
            WorkStatus.Text = L("분석을 취소했습니다.", "Analysis canceled.");
            if (previous is not null) ReturnToInvestigation();
        }
        catch (Exception error) when (IsExpected(error))
        {
            WorkStatus.Text = L($"자료를 분석하지 못했습니다. {error.Message}", $"Could not analyze the sources. {error.Message}");
        }
        finally
        {
            candidate?.Dispose();
            EndWork(work);
        }
    }

    private static SearchRequest ExpandSourceGroupOnIntake(SearchRequest request,
        IReadOnlyList<SourceInfo> incoming, IReadOnlyList<InputCandidate> inputs)
    {
        if (request.SourceGroup is not { } group) return request;
        var roots = inputs.ToDictionary(input => input.Path, input => input.Root, StringComparer.OrdinalIgnoreCase);
        var ids = group.SourceIds.ToHashSet(StringComparer.Ordinal);
        foreach (SourceInfo source in incoming)
        {
            string path = source.SelectedPath ?? source.Path;
            if (!roots.TryGetValue(path, out string? root)) continue;
            bool belongs = group.DirectoryPath is { } directory
                ? root is not null && IsInsideDirectory(root, directory) && IsInsideDirectory(directory, path)
                : root is null && source.Format is MailFormat.Eml or MailFormat.Msg;
            if (belongs) ids.Add(source.Id);
        }
        return ids.Count == group.SourceIds.Count ? request
            : request with { SourceGroup = group with { SourceIds = Array.AsReadOnly(ids.ToArray()) } };
    }

    private static bool IsInsideDirectory(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private bool ConfirmPartialSources(EvidenceSession session)
    {
        string details = FormatIssues(session.Issues.Take(8), session.Sources);
        string summary = L($"메일 {session.Records.Count:N0}통을 읽었습니다. 확인하지 못한 항목이 {session.Issues.Count:N0}건 있습니다.",
            $"Read {session.Records.Count:N0} messages. There are {session.Issues.Count:N0} read issues.")
            + Environment.NewLine + Environment.NewLine + details + Environment.NewLine + Environment.NewLine
            + L("읽은 자료를 조사에 추가하시겠습니까?", "Add the readable sources to the investigation?");
        return MessageBox.Show(IntakeOwner, summary, L("일부 자료 판독", "Sources partially read"), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void ApplyIntakeResults(EvidenceSession session, SearchRequest request, SearchResult result, SearchRequest draft,
        IReadOnlyList<InputCandidate> inputs, bool initial)
    {
        foreach (var item in inputs)
        {
            if (item.Root is { } root)
                _intakeFolders.TryAdd(item.Path, root);
        }
        RefreshBookmarkScope();
        request = WithCurrentBookmarks(request);
        draft = WithCurrentBookmarks(draft);
        PopulateSourceOptions(preserveSelection: !initial);
        RebuildPstFolderTree();
        if (initial)
        {
            ResetSearchInputs();
            _checkedRecords.Clear();
        }
        else
        {
            RestorePstTreeSelection(draft);
        }
        foreach (InputCandidate item in _candidates)
            item.PropertyChanged -= OnCandidateChanged;
        _candidates.Clear();
        _unappliedSourceReport = null;
        RefreshSourceReport();
        InvestigationTab.IsEnabled = true;
        InvestigationTab.Visibility = Visibility.Visible;
        AnomalyMailboxTab.Visibility = Visibility.Visible;
        BookmarksMailboxTab.Visibility = Visibility.Visible;
        WorkspaceTabs.SelectedItem = ActiveMailboxTab;
        SetResults(request, result, selectFirst: initial);
        WorkStatus.Text = "";
        ReturnToInvestigation();
        if (initial && result.Hits.Count == 1)
            OpenReaderForSingleResult();
        CountMailboxAnomalies(session);
    }

    private sealed class InputCandidate(string path, string? root, bool located, bool exists, DiskImageOrigin? imageOrigin) : INotifyPropertyChanged
    {
        private bool _included = exists;

        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        public string? Root { get; } = root;
        public bool Exists { get; } = exists;
        public DiskImageOrigin? ImageOrigin { get; } = imageOrigin;
        public string Kind => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
        {
            ".eml" or ".msg" or ".emlx" or ".nws" or ".mime" or ".rfc822" => L("개별 메일", "Message file"),
            ".mbox" or ".pst" or ".ost" or ".dbx" or ".mbx" or ".olm" or ".mmdf" or ".babyl" or ".rmail" => L("메일함 파일", "Mailbox file"),
            ".zip" or ".tnef" or ".p7m" or ".p7s" => L("컨테이너", "Container"),
            _ => L("형식을 확인할 수 없음", "Format unconfirmed")
        };
        public string Origin => ImageOrigin is not null ? L("이미지에서 추출", "Extracted from image")
            : located ? L("자료 찾기", "Source finder")
            : Root is not null ? L("폴더에서 발견", "Folder scan") : L("직접 선택", "File selection");
        public string Status => Exists ? L("분석 전", "Pending analysis") : L("파일에 접근할 수 없음", "File unavailable");
        public bool Included
        {
            get => _included;
            set
            {
                if (value == _included || !Exists) return;
                _included = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Included)));
            }
        }

        public void RefreshLanguage()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Origin)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
