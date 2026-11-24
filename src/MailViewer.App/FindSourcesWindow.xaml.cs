using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Win32;

namespace MailViewer.App;

public partial class FindSourcesWindow : Window
{
    private readonly Func<string, string, string> text;
    private readonly OutlookLocationSet outlook;
    private sealed record LocationOption(string Label, string? Path, FolderSearchTarget? Target);
    private FoundSource[] hits = [];
    private string? location;
    private CancellationTokenSource? currentWork;

    public FindSourcesWindow(Func<string, string, string> text) : this(text, OutlookSourceDiscovery.Find()) { }

    internal FindSourcesWindow(Func<string, string, string> text, OutlookLocationSet outlook)
    {
        this.text = text;
        this.outlook = outlook;
        InitializeComponent();
        Appearance.ApplyToWindow(this);
        Heading.Text = Title = text("메일 파일 찾기", "Find mail sources");
        KnownLocations.Content = text("이 PC의 Outlook 저장 위치", "Outlook locations on this PC");
        CustomLocation.Content = text("드라이브 또는 폴더 선택", "Choose a drive or folder");
        RecurseLocation.Content = text("하위 폴더 포함", "Include subfolders");
        ChooseLocationButton.Content = text("위치 선택…", "Choose location…");
        IncludeColumn.Header = text("포함", "Add");
        NameColumn.Header = text("파일명", "File name");
        TypeColumn.Header = text("확장자", "Type");
        PathColumn.Header = text("경로", "Path");
        ScanIssues.Header = text("읽지 못한 위치", "Unread locations");
        CloseButton.Content = text("닫기", "Close");
        AcceptButton.Content = text("선택한 자료 추가", "Add checked sources");
        AutomationProperties.SetName(HitsGrid, text("찾은 메일 파일", "Mail sources found"));
        InitializeOutlookScope();
        CopyPathButton.Content = text("검색 경로 복사", "Copy search path");
        RefreshScope();
        Loaded += (_, _) => KnownLocations.Focus();
    }

    internal IReadOnlyList<FolderSearchHit> SelectedSources { get; private set; } = [];

    private void InitializeOutlookScope()
    {
        int count = outlook.Targets.Count;
        AutomationProperties.SetName(OutlookScope, text("Outlook 검색 위치", "Outlook search location"));
        OutlookScope.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SingleOutlookLocation.Visibility = count > 1 ? Visibility.Collapsed : Visibility.Visible;
        SingleOutlookLocation.Text = count == 1 ? outlook.Targets[0].Path :
            text("확인할 로컬 Outlook 저장 위치가 없습니다.", "No local Outlook locations found.");
        if (count <= 1) return;
        LocationOption[] options = [
            new(text($"모든 위치 ({count:N0}곳)", $"All locations ({count:N0})"), null, null),
            .. outlook.Targets.Select(target => new LocationOption(FormatLocation(target.Path), target.Path, target))
        ];
        OutlookScope.ItemsSource = options;
        OutlookScope.SelectedIndex = 0;
    }

    private static string FormatLocation(string path) =>
        Path.GetDirectoryName(path) is { } parent ? $"{Path.GetFileName(path)}  ·  {parent}" : path;

    private IReadOnlyList<FolderSearchTarget> SelectedOutlookTargets =>
        OutlookScope.SelectedItem is LocationOption { Target: { } target } ? [target] : outlook.Targets;

    private void ChangeOutlookLocation(object sender, SelectionChangedEventArgs e)
    {
        if (OutlookScope.SelectedItem is LocationOption option) OutlookScope.ToolTip = option.Path;
        if (!IsLoaded) return;
        ResetHits();
        RefreshScope();
    }

    private void SizeOutlookDropDown(object? sender, EventArgs e)
    {
        if (OutlookScope.Template.FindName("PART_Popup", OutlookScope) is Popup { Child: FrameworkElement content })
            content.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = OutlookScope });
    }

    private void ChangeScope(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || HitsGrid is null) return;
        ResetHits();
        RefreshScope();
    }

    private void RefreshScope()
    {
        bool known = KnownLocations.IsChecked == true;
        OutlookLocationPanel.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        ManualLocationPanel.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.Content = text("찾기", "Find");
        ScanButton.IsEnabled = known ? SelectedOutlookTargets.Count > 0 : location is not null;
        ScanStatus.Text = known
            ? OutlookStatus(SelectedOutlookTargets.Count == 0)
            : "";
        CopyPathButton.Visibility = location is null ? Visibility.Collapsed : Visibility.Visible;
        SelectedPath.Text = location ?? text("아직 선택한 위치가 없습니다.", "Choose where to look.");
    }

    private string OutlookStatus(bool noLocations)
    {
        if (noLocations)
            return text("메일 파일을 저장한 드라이브나 폴더를 지정할 수 있습니다.",
                "You can choose the drive or folder where your mail files are saved.");
        return outlook.HasNewOutlook
            ? text("새 Outlook 저장 위치를 찾았습니다. 선택한 위치에서 PST·OST 파일을 검색합니다.",
                "Found new Outlook storage locations. Search the selected locations for PST and OST files.")
            : text("선택한 위치에서 PST·OST 파일을 검색합니다.", "Search the selected locations for PST and OST files.");
    }

    private void CopyLocation(object sender, RoutedEventArgs e)
    {
        if (location is null) return;
        try { Clipboard.SetText(location); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ScanStatus.Text = text("경로를 클립보드에 복사하지 못했습니다.", "Could not copy the location.");
        }
    }

    private void ChooseLocation(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = text("검색할 드라이브 또는 폴더", "Choose the drive or folder to search") };
        if (dialog.ShowDialog(this) != true) return;
        ResetHits();
        if (!FolderSourceSearch.IsLocal(dialog.FolderName))
        {
            ScanStatus.Text = text("이 PC에 연결된 로컬 드라이브나 폴더를 선택하세요.", "Choose a local drive or folder on this PC.");
            return;
        }
        location = dialog.FolderName;
        RefreshScope();
    }

    private async void StartScan(object sender, RoutedEventArgs e)
    {
        if (currentWork is { } previous)
        {
            previous.Cancel();
            ScanButton.IsEnabled = false;
            return;
        }
        bool known = KnownLocations.IsChecked == true;
        IReadOnlyList<FolderSearchTarget> targets = known ? SelectedOutlookTargets :
            location is { } path ? [new(path, RecurseLocation.IsChecked == true)] : [];
        if (targets.Count == 0) return;
        ResetHits();
        using var work = new CancellationTokenSource();
        currentWork = work;
        KnownLocations.IsEnabled = CustomLocation.IsEnabled = ManualLocationPanel.IsEnabled = false;
        OutlookScope.IsEnabled = CloseButton.IsEnabled = false;
        ScanButton.Content = text("찾기 취소", "Cancel search");
        ScanStatus.Text = known
            ? text("PST·OST 파일을 찾는 중입니다.", "Finding PST and OST files.")
            : text("지원하는 메일 파일을 찾는 중입니다.", "Finding supported mail files.");
        try
        {
            FolderSearchResult result = await Task.Run(() => FolderSourceSearch.Scan(targets, outlookDataOnly: known, work.Token));
            hits = result.Hits.Select(hit => new FoundSource(hit)).ToArray();
            foreach (FoundSource hit in hits) hit.PropertyChanged += OnCheckChanged;
            HitsGrid.ItemsSource = hits;
            RefreshSelection(result.Canceled);
            if (result.Issues.Count > 0)
            {
                IssuesBox.Text = string.Join(Environment.NewLine, result.Issues);
                ScanIssues.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            currentWork = null;
            KnownLocations.IsEnabled = CustomLocation.IsEnabled = ManualLocationPanel.IsEnabled = true;
            OutlookScope.IsEnabled = CloseButton.IsEnabled = true;
            ScanButton.Content = text("찾기", "Find");
            ScanButton.IsEnabled = true;
        }
    }

    private void ResetHits()
    {
        foreach (FoundSource hit in hits) hit.PropertyChanged -= OnCheckChanged;
        hits = [];
        HitsGrid.ItemsSource = hits;
        AcceptButton.IsEnabled = false;
        ScanIssues.Visibility = Visibility.Collapsed;
        ScanIssues.IsExpanded = false;
        IssuesBox.Clear();
    }

    private bool wasCanceled;

    private void OnCheckChanged(object? sender, PropertyChangedEventArgs e) => RefreshSelection(wasCanceled);

    private void RefreshSelection(bool canceled)
    {
        wasCanceled = canceled;
        int selected = hits.Count(hit => hit.Included);
        AcceptButton.IsEnabled = selected > 0;
        if (hits.Length == 0 && !canceled)
        {
            ScanStatus.Text = EmptyResultsMessage();
            return;
        }
        ScanStatus.Text = text($"후보 {hits.Length:N0}개 · {selected:N0}개 선택", $"{hits.Length:N0} found · {selected:N0} checked");
        if (canceled)
            ScanStatus.Text += " · " + text("취소 전 확인한 범위", "Scope scanned before cancellation");
    }

    private string EmptyResultsMessage()
    {
        if (KnownLocations.IsChecked != true)
            return text("선택한 위치에서 지원하는 메일 파일을 찾지 못했습니다.",
                "No supported mail files found in the selected location.");
        string result = text("확인한 위치에서 PST·OST 파일을 찾지 못했습니다.",
            "No PST or OST files found in the locations checked.");
        if (outlook.HasNewOutlook)
            result += " " + text("새 Outlook에서 설정 → 파일 → 내보내기로 PST 파일을 저장할 수 있습니다.",
                "To save a PST in new Outlook, go to Settings → Files → Export.");
        return result;
    }

    private void AcceptSources(object sender, RoutedEventArgs e)
    {
        SelectedSources = hits.Where(hit => hit.Included).Select(hit => hit.Source).ToArray();
        if (SelectedSources.Count > 0) DialogResult = true;
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (currentWork is not { } work) return;
        work.Cancel();
        e.Cancel = true;
    }

    private sealed class FoundSource(FolderSearchHit source) : INotifyPropertyChanged
    {
        private bool included;
        public FolderSearchHit Source { get; } = source;
        public string Name => System.IO.Path.GetFileName(Source.Path);
        public string Extension => System.IO.Path.GetExtension(Source.Path).TrimStart('.').ToUpperInvariant();
        public string Path => Source.Path;
        public bool Included
        {
            get => included;
            set
            {
                if (value == included) return;
                included = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Included)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
