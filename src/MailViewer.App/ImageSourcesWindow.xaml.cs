using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EvidenceImage;
using MailViewer.Core;
using Microsoft.Win32;

namespace MailViewer.App;

public partial class ImageSourcesWindow : Window
{
    internal static readonly string[] MailExtensions = [".eml", ".msg", ".pst", ".ost", ".mbox"];
    internal static readonly string[] CommonFolders = [@"AppData\Local\Microsoft\Outlook", @"Documents\Outlook Files",
        @"OneDrive\Documents\Outlook Files", @"AppData\Local\Microsoft\Olk", "Desktop", "Downloads"];
    private readonly string imagePath;
    private readonly Func<string, string, string> text;
    private MailCandidate[] candidates = [];
    private CancellationTokenSource? active;
    private bool scannedEntireVolume;

    internal ImageSourcesWindow(string imagePath, Func<string, string, string> text)
    {
        this.imagePath = imagePath;
        this.text = text;
        InitializeComponent();
        Appearance.ApplyToWindow(this);
        Heading.Text = Title = text("이미지에서 메일 찾기", "Find mail in a disk image");
        ImageName.Text = imagePath;
        VolumeLabel.Content = text("볼륨", "Volume");
        WholeVolumeButton.Content = text("볼륨 전체 검색", "Search entire volume");
        CheckColumn.Header = text("포함", "Add");
        NameColumn.Header = text("파일명", "File name");
        PathColumn.Header = text("이미지 내부 경로", "Path in image");
        SizeColumn.Header = text("크기 (바이트)", "Size (bytes)");
        ExtractButton.Content = text("선택한 파일 추출…", "Extract selected files…");
        RefreshActions();
        Loaded += ReadImage;
    }

    internal IReadOnlyList<(string Path, DiskImageOrigin Origin)> ExtractedFiles { get; private set; } = [];

    private async void ReadImage(object sender, RoutedEventArgs e)
    {
        Loaded -= ReadImage;
        ImageProbe? probe = null;
        await Run(text("이미지의 NTFS 볼륨을 확인하는 중입니다.", "Checking the image for NTFS volumes."), async (ct, _) =>
        {
            probe = await ImageEvidence.ProbeAsync(imagePath, ct);
            if (probe.Warnings.Count > 0)
            {
                Warnings.Text = string.Join(Environment.NewLine, probe.Warnings);
                Warnings.Visibility = Visibility.Visible;
            }

            if (probe.Format.Contains("Logical", StringComparison.OrdinalIgnoreCase))
            {
                Status.Text = text("이 화면에서는 물리 디스크 이미지의 NTFS 볼륨을 검색합니다.",
                    "This window searches NTFS volumes in physical disk images.");
                probe = null;
            }
            else if (probe.Volumes.Count == 0)
                Status.Text = text("읽을 수 있는 NTFS 볼륨이 없습니다.", "No readable NTFS volumes.");
        });
        if (probe?.Volumes.Count > 0 && IsVisible)
        {
            Volumes.ItemsSource = probe.Volumes.Select(volume => new VolumeChoice(volume,
                $"{volume.Label}  ·  0x{volume.Offset:X}  ·  {volume.Length / (1024.0 * 1024):N0} MiB")).ToArray();
            Volumes.SelectedIndex = 0;
        }
    }

    private async void OnVolumeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || active is not null || Volumes.SelectedItem is not VolumeChoice) return;
        SetCandidates([]);
        scannedEntireVolume = false;
        await Search(entireVolume: false);
    }

    private async void SearchWholeVolume(object sender, RoutedEventArgs e) => await Search(entireVolume: true);

    private async Task Search(bool entireVolume)
    {
        if (Volumes.SelectedItem is not VolumeChoice selected) return;
        string searching = entireVolume
            ? text("볼륨 전체에서 메일 파일을 찾는 중입니다.", "Finding mail files throughout the volume.")
            : text("사용자 폴더에서 메일 파일을 찾는 중입니다.", "Finding mail files in standard user folders.");
        bool showedFirst = false;
        bool completed = false;
        await Run(searching, async (ct, work) =>
        {
            var first = new Progress<ImageFileEntry>(file =>
            {
                if (!ReferenceEquals(active, work) || work.IsCancellationRequested || completed) return;
                if (candidates.Length == 0)
                {
                    SetCandidates([file]);
                    showedFirst = true;
                }
                Status.Text = searching + " " + text($"첫 후보 {Path.GetFileName(file.Path)}", $"First match {Path.GetFileName(file.Path)}");
            });
            var result = await ImageEvidence.SearchNtfsFilesAsync(imagePath, selected.Volume.Id, MailExtensions,
                entireVolume ? null : CommonFolders, entireVolume ? null : @"\Users", first, ct);
            ct.ThrowIfCancellationRequested();
            SetCandidates(result.Files);
            scannedEntireVolume = entireVolume;
            completed = true;
            Status.Text = entireVolume
                ? text($"볼륨 전체에서 후보 {candidates.Length:N0}개를 찾았습니다.", $"{candidates.Length:N0} candidates across the volume.")
                : text($"기본 위치에서 후보 {candidates.Length:N0}개를 찾았습니다. 다른 폴더는 볼륨 전체 검색에서 확인할 수 있습니다.",
                    $"{candidates.Length:N0} candidates in standard locations. Search the entire volume for other folders.");
            int tooLarge = candidates.Count(candidate => !candidate.CanAnalyze);
            if (tooLarge > 0)
                Status.Text += " " + text($"2 GiB 초과 PST·OST {tooLarge:N0}개는 분석할 수 없습니다.",
                    $"{tooLarge:N0} PST or OST files exceed the 2 GiB limit.");
            if (result.UnreadEntries > 0)
                Status.Text += " " + text($"확인하지 못한 항목 {result.UnreadEntries:N0}개.",
                    $"{result.UnreadEntries:N0} entries could not be checked.");
        });
        if (showedFirst && !completed) SetCandidates([]);
    }

    private void SetCandidates(IReadOnlyList<ImageFileEntry> files)
    {
        var checkedIds = candidates.Where(candidate => candidate.Checked).Select(candidate => candidate.File.FileReference).ToHashSet();
        foreach (var candidate in candidates) candidate.PropertyChanged -= OnChecked;
        candidates = files.Select(file => new MailCandidate(file, text) { Checked = checkedIds.Contains(file.FileReference) }).ToArray();
        foreach (var candidate in candidates) candidate.PropertyChanged += OnChecked;
        Candidates.ItemsSource = candidates;
        RefreshActions();
    }

    private async void ExtractSelected(object sender, RoutedEventArgs e)
    {
        if (Volumes.SelectedItem is not VolumeChoice selected) return;
        ImageFileEntry[] files = candidates.Where(candidate => candidate.Checked).Select(candidate => candidate.File).ToArray();
        if (files.Length == 0) return;
        var destination = new OpenFolderDialog { Title = text("추출 파일을 저장할 폴더", "Select a folder for the extracted files") };
        if (destination.ShowDialog(this) != true) return;
        if (!FolderSourceSearch.IsLocal(destination.FolderName))
        {
            Status.Text = text("로컬 드라이브의 폴더를 선택하세요.", "Choose a folder on a local drive.");
            return;
        }
        await Extract(destination.FolderName, selected, files);
    }

    private async Task Extract(string folder, VolumeChoice selected, ImageFileEntry[] files)
    {
        ArtifactRequest[] requests = CreateRequests(files);
        await Run(text("선택한 메일 파일을 추출하는 중입니다.", "Extracting the selected mail files."), async (ct, work) =>
        {
            var progress = new Progress<ImageReadProgress>(item =>
            {
                if (!ReferenceEquals(active, work) || work.IsCancellationRequested) return;
                string stage = item.Stage == "hash"
                    ? text("이미지 SHA-256 계산", "Hashing the disk image")
                    : text("선택한 파일 추출", "Extracting selected files");
                Progress.IsIndeterminate = item.Total <= 0;
                Progress.Value = item.Total > 0 ? Math.Clamp(100.0 * item.Completed / item.Total, 0, 100) : 0;
                Status.Text = $"{stage} · {Path.GetFileName(item.Item)}" +
                    (item.Total > 0 ? $" · {Progress.Value:F0}%" : "");
            });
            ArtifactResult result = await ImageEvidence.ExtractArtifactsAsync(imagePath, selected.Volume.Id, folder, requests, ct, progress);
            ExtractedFiles = requests.Select(request =>
            {
                var origin = new DiskImageOrigin(imagePath, selected.Volume.Offset, request.NtfsPath,
                    request.ExpectedFileReference!.Value, result.ManifestPath, result.Sha256ByKey[request.Key]);
                return (result.Files[request.Key], origin);
            }).ToArray();
        });
        if (ExtractedFiles.Count > 0) DialogResult = true;
    }

    internal static ArtifactRequest[] CreateRequests(IReadOnlyList<ImageFileEntry> files)
    {
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return files.Select((file, index) =>
        {
            string original = Path.GetFileName(file.Path);
            string extension = Path.GetExtension(original);
            string key = $"mail-{index + 1}";
            string name = original.Length > 180 || original.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(original, @"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? key + extension : original;
            for (int suffix = 1; !outputNames.Add(name); suffix++)
                name = $"{key}-{suffix}{extension}";
            return new ArtifactRequest(key, file.Path, null, name) { ExpectedFileReference = file.FileReference };
        }).ToArray();
    }

    private async Task Run(string starting, Func<CancellationToken, CancellationTokenSource, Task> operation)
    {
        if (active is not null) return;
        using var work = new CancellationTokenSource();
        active = work;
        Progress.IsIndeterminate = true;
        Status.Text = starting;
        RefreshActions();
        try { await operation(work.Token, work); }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            Status.Text = text("작업을 취소했습니다.", "Operation canceled.");
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Status.Text = text("EWF를 읽는 데 필요한 라이브러리를 불러올 수 없습니다. 실행 파일과 배포 폴더의 DLL을 같은 위치에 두세요.",
                "Could not load the EWF library. Keep the DLL from the release folder next to the executable.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            Status.Text = text($"이미지 작업을 완료하지 못했습니다. {error.Message}",
                $"Could not complete the image operation. {error.Message}");
        }
        finally
        {
            active = null;
            RefreshActions();
        }
    }

    private void OnChecked(object? sender, PropertyChangedEventArgs e) => RefreshActions();

    private void RefreshActions()
    {
        bool idle = active is null;
        Volumes.IsEnabled = idle;
        Candidates.IsEnabled = idle;
        WholeVolumeButton.IsEnabled = idle && Volumes.SelectedItem is not null && !scannedEntireVolume;
        ExtractButton.IsEnabled = idle && candidates.Any(candidate => candidate.Checked);
        Progress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Content = idle ? text("닫기", "Close") : text("작업 취소", "Cancel operation");
    }

    private void CloseOrCancel(object sender, RoutedEventArgs e)
    {
        if (active is { } work) work.Cancel();
        else Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (active is not { } work) return;
        e.Cancel = true;
        work.Cancel();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape || e.Handled || Volumes.IsDropDownOpen) return;
        CloseOrCancel(this, e);
        e.Handled = true;
    }

    private sealed record VolumeChoice(ImageVolume Volume, string Label);

    private sealed class MailCandidate(ImageFileEntry file, Func<string, string, string> text) : INotifyPropertyChanged
    {
        private bool isChecked;
        public ImageFileEntry File { get; } = file;
        public string Path => File.Path;
        public string Name => System.IO.Path.GetFileName(Path);
        public long Length => File.Length;
        public bool CanAnalyze => Length <= EvidenceSession.MaximumFolderStoreLength ||
            PathExtension is not (".pst" or ".ost");
        private string PathExtension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
        public string? LimitReason => CanAnalyze ? null :
            text("2 GiB를 넘는 PST·OST는 분석할 수 없습니다.", "PST and OST files over 2 GiB are not supported.");
        public bool Checked
        {
            get => isChecked;
            set
            {
                if (value == isChecked || (value && !CanAnalyze)) return;
                isChecked = value;
                PropertyChanged?.Invoke(this, new(nameof(Checked)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
