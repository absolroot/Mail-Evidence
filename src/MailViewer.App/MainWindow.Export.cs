using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MailViewer.Core;
using Microsoft.Win32;

namespace MailViewer.App;

public partial class MainWindow
{
    private ExportRequest? _pendingExport;
    private ExportResult? _exportResult;
    private FrameworkElement? _exportCommand;
    private ResultRow? _returnRow;
    private double _returnVertical;
    private double _returnHorizontal;

    private void PrepareSelectedExport(object sender, RoutedEventArgs e)
    {
        if (CanChooseSources && _activeRow is { } selected)
            ShowExportPlan(Plan(ExportScope.SelectedMessages, DefaultMessageExport([selected]), [selected]), [selected],
                L("선택한 메일 1건", "One selected message"), ExportMenu);
    }

    private void PrepareCheckedExport(object sender, RoutedEventArgs e)
    {
        if (!ExportCheckedButton.IsEnabled) return;
        ResultRow[] rows = ResultRows().Where(row => row.Checked).ToArray();
        if (rows.Length > 0)
            ShowExportPlan(Plan(ExportScope.SelectedMessages, DefaultMessageExport(rows), rows), rows,
                L($"현재 결과에서 체크한 메일 {rows.Length:N0}건", $"Checked messages in these results: {rows.Length:N0}"),
                ExportCheckedButton);
    }

    private void PrepareSearchExport(object sender, RoutedEventArgs e)
    {
        ResultRow[] rows = ResultRows().ToArray();
        if (rows.Length > 0 && CanChooseSources)
        {
            string scope = _result is { Issues.Count: > 0 } ? L("판독된 검색 결과", "Readable search results")
                : L("검색 결과", "Search results");
            ShowExportPlan(Plan(ExportScope.SearchResults, ExportKind.Csv, rows), rows,
                $"{scope} {rows.Length:N0}{L("건", "")}", ExportMenu);
        }
    }

    private void PrepareAttachmentExport(object sender, RoutedEventArgs e)
    {
        if (!ExportAttachmentButton.IsEnabled || _activeRow is not { } row
            || AttachmentsGrid.SelectedItem is not MailAttachment { Sha256: not null } attachment)
        {
            return;
        }
        ExportRequest request = Plan(ExportScope.SelectedAttachments, ExportKind.Attachments, [row]) with
        {
            AttachmentIds = [new AttachmentSelection(row.Record.Id, attachment.Id)]
        };
        string evidence = attachment.PstProperty is { } property ? FormatPstLocation(property)
            : attachment.CompoundStream is { } stream ? FormatStreamLocation(stream) : $"MIME {attachment.PartPath}";
        ShowExportPlan(request, [row],
            $"{L("선택 첨부 1개", "One selected attachment")} · {attachment.Name}\n{evidence}\nSHA-256 {attachment.Sha256}",
            ExportMenu);
    }

    private async void SaveAttachmentFile(object sender, RoutedEventArgs e)
    {
        if (!SaveAttachmentFileButton.IsEnabled || _session is not { } session || _activeRow is not { } row
            || AttachmentsGrid.SelectedItem is not MailAttachment attachment)
            return;

        var dialog = new SaveFileDialog
        {
            Title = L("첨부파일 저장", "Save attachment"),
            FileName = ExportService.SuggestedAttachmentFileName(attachment.Name),
            Filter = L("모든 파일|*.*", "All files|*.*"),
            AddExtension = false,
            OverwritePrompt = false
        };
        if (Directory.Exists(DestinationBox.Text))
            dialog.InitialDirectory = DestinationBox.Text;
        if (dialog.ShowDialog(this) != true)
            return;
        if (File.Exists(dialog.FileName))
        {
            WorkStatus.Text = L("같은 이름의 파일이 있습니다. 다른 이름으로 저장하세요.",
                "A file with this name already exists. Save with a different name.");
            return;
        }

        var work = StartWork(L("첨부파일을 저장하고 해시를 대조하는 중입니다.", "Saving the attachment and verifying its hash."));
        try
        {
            await Task.Run(() => ExportService.SaveAttachment(session, row.Record.Id, attachment, dialog.FileName, work.Token),
                work.Token);
            string name = Path.GetFileName(dialog.FileName);
            WorkStatus.Text = L($"첨부파일 저장 완료 · {name} · SHA-256 일치",
                $"Attachment saved · {name} · SHA-256 verified");
        }
        catch (OperationCanceledException)
        {
            WorkStatus.Text = L("첨부파일 저장을 취소했습니다.", "Attachment save cancelled.");
        }
        catch (Exception error) when (IsExpected(error) || error is InvalidDataException)
        {
            WorkStatus.Text = L($"첨부파일을 저장하지 못했습니다. {error.Message}",
                $"Could not save the attachment. {error.Message}");
        }
        finally
        {
            EndWork(work);
        }
    }

    private ExportRequest Plan(ExportScope scope, ExportKind kind, IReadOnlyList<ResultRow> rows) =>
        new("", kind, scope, rows.Select(row => row.Record.Id).ToArray(),
            Search: _appliedRequest, SearchIssues: _result?.Issues,
            ExaminedCount: _result?.ExaminedCount, DateUnknownCount: _result?.DateUnknownCount, DateTimeZone: "UTC");

    private void ShowExportPlan(ExportRequest plan, IReadOnlyList<ResultRow> rows, string scope, FrameworkElement command)
    {
        CaptureReturnPosition();
        _pendingExport = plan;
        _exportResult = null;
        _exportCommand = command;
        ExportTitle.Text = L("내보내기", "Export");
        Choice<ExportKind>[] formats = ExportFormats(plan.Scope, rows);
        ExportOptionsPanel.Visibility = formats.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        DestinationPanel.Visibility = Visibility.Visible;
        SaveExportButton.Visibility = Visibility.Visible;
        VerifyExportButton.Visibility = Visibility.Collapsed;
        ReturnToSearchButton.Content = ExportReturnLabel();
        ExportKindBox.ItemsSource = formats;
        ExportKindBox.SelectedItem = formats.FirstOrDefault(option => option.Value == plan.Kind) ?? formats[0];

        var report = new StringBuilder().AppendLine(scope).AppendLine();
        if (formats.Length == 1)
            report.AppendLine(((Choice<ExportKind>)ExportKindBox.SelectedItem).Label).AppendLine();
        if (plan.Scope == ExportScope.SearchResults)
        {
            report.AppendLine(DescribeSearch(_appliedRequest));
            report.AppendLine(L($"판독 범위  {plan.ExaminedCount:N0}건", $"Examined  {plan.ExaminedCount:N0}"));
            if (plan.DateUnknownCount > 0)
                report.AppendLine(L($"시각 미확정  {plan.DateUnknownCount:N0}건", $"Unknown date  {plan.DateUnknownCount:N0}"));
            if (plan.SearchIssues is { Count: > 0 } issues)
            {
                report.AppendLine(L("확인하지 못한 범위", "Unverified coverage"))
                    .AppendLine(FormatIssues(issues, _session?.Sources ?? []));
            }
            report.AppendLine();
        }
        foreach (ResultRow row in rows.Take(50))
        {
            string location = row.Record.PstMessage is { } message
                ? $"{StoreFormatName(row.Record.Format)} · {message.FolderPath} · {L("폴더 NID", "Folder NID")} {message.FolderNid}"
                    + $" · {L("메시지 NID", "Message NID")} {message.MessageNid}"
                : row.Record.MessageRange.ToString();
            report.AppendLine(row.Record.Subject).AppendLine($"  {row.Record.SourcePath} · {location}");
        }
        if (rows.Count > 50) report.AppendLine(L($"이 외 {rows.Count - 50:N0}건", $"{rows.Count - 50:N0} more messages"));
        ExportReportBox.Text = report.ToString();
        ShowExportPanel();
        DestinationBox.Focus();
        DestinationBox.SelectAll();
    }

    private static ExportKind DefaultMessageExport(IReadOnlyList<ResultRow> rows)
    {
        if (rows.All(row => row.Record.Format == MailFormat.Pst)) return ExportKind.PstEvidence;
        if (rows.All(row => row.Record.Format == MailFormat.Ost)) return ExportKind.OstEvidence;
        return rows.Any(row => !row.Record.CanReadMessage) ? ExportKind.Csv : ExportKind.MessageBytes;
    }

    private static Choice<ExportKind>[] ExportFormats(ExportScope scope, IReadOnlyList<ResultRow> rows)
    {
        if (scope == ExportScope.SelectedAttachments)
            return [new(ExportKind.Attachments, rows[0].Record.Format switch
            {
                MailFormat.Pst or MailFormat.Ost => L("첨부 속성 바이트와 매니페스트", "Attachment property bytes and manifest"),
                MailFormat.Msg => L("첨부 스트림 사본과 매니페스트", "Attachment stream copy and manifest"),
                _ => L("디코딩한 첨부파일과 매니페스트", "Decoded attachment and manifest")
            })];

        Choice<ExportKind> csv = new(ExportKind.Csv, L("CSV 목록과 매니페스트", "CSV results and manifest"));
        return DefaultMessageExport(rows) switch
        {
            ExportKind.PstEvidence => [csv, new(ExportKind.PstEvidence,
                L("PST 메시지 근거 (JSON · 원본 헤더)", "PST message evidence (JSON · original header)"))],
            ExportKind.OstEvidence => [csv, new(ExportKind.OstEvidence,
                L("OST 메시지 근거 (JSON · 저장된 전송 헤더)", "OST message evidence (JSON · stored transport header)"))],
            ExportKind.MessageBytes => [csv, new(ExportKind.MessageBytes, rows.Any(row => row.Record.ContainerMessage is not null)
                ? L("저장된 메시지 바이트와 매니페스트", "Stored message bytes and manifest") : OriginalFormatNames(rows))],
            _ => [csv]
        };
    }

    private static string OriginalFormatNames(IReadOnlyList<ResultRow> rows) => string.Join(" / ",
        rows.Select(row => row.Record.Format).Distinct().Order().Select(format => format switch
        {
            MailFormat.Msg => L("MSG 원본 사본", "Original MSG copy"),
            MailFormat.Mbox => L("MBOX 원본 구간", "Raw MBOX range"),
            _ => L("EML 원본 사본", "Original EML copy")
        }));

    private void BrowseExportDestination(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        var dialog = new OpenFolderDialog { Title = L("결과를 저장할 기존 폴더 선택", "Choose an existing export folder") };
        if (Directory.Exists(DestinationBox.Text))
        {
            dialog.InitialDirectory = DestinationBox.Text;
        }
        if (dialog.ShowDialog(this) == true)
        {
            DestinationBox.Text = dialog.FolderName;
        }
    }

    private async void SaveExport(object sender, RoutedEventArgs e)
    {
        if (_work is not null || _session is not { } session || _pendingExport is not { } plan
            || ExportKindBox.SelectedItem is not Choice<ExportKind> kind)
        {
            return;
        }
        string directory = DestinationBox.Text.Trim();
        if (!Directory.Exists(directory))
        {
            WorkStatus.Text = "결과를 저장할 기존 폴더를 선택하세요.";
            DestinationBox.Focus();
            return;
        }

        ExportRequest request = plan with { DestinationDirectory = Path.GetFullPath(directory), Kind = kind.Value };
        string confirmation = ExportReportBox.Text;
        var work = StartWork("산출물을 저장하고 해시를 대조하는 중입니다.");
        SetExportInputsEnabled(false);
        try
        {
            ExportResult result = await Task.Run(() => ExportService.Export(session, request, work.Token), work.Token);
            _exportResult = result;
            ExportTitle.Text = L("내보내기 결과", "Export results");
            ExportOptionsPanel.Visibility = Visibility.Collapsed;
            DestinationPanel.Visibility = Visibility.Collapsed;
            SaveExportButton.Visibility = Visibility.Collapsed;
            VerifyExportButton.Visibility = Visibility.Visible;
            ExportReportBox.Text = FormatExportReport(result.PackageDirectory, result.Items,
                L($"저장 완료 · 산출물 {result.Items.Count:N0}개", $"Save complete · {result.Items.Count:N0} files"));
            WorkStatus.Text = "저장을 마쳤습니다. 결과 파일의 해시를 다시 검증할 수 있습니다.";
        }
        catch (OperationCanceledException)
        {
            WorkStatus.Text = "저장을 취소했습니다.";
        }
        catch (Exception error) when (IsExpected(error))
        {
            WorkStatus.Text = "저장하지 못했습니다.";
            ExportReportBox.Text = $"저장 오류  {error.Message}\n\n{confirmation}";
        }
        finally
        {
            SetExportInputsEnabled(true);
            EndWork(work);
        }
        if (_exportResult is not null) VerifyExportButton.Focus();
    }

    private async void VerifyExport(object sender, RoutedEventArgs e)
    {
        if (_work is not null || _exportResult is not { } result) return;
        var work = StartWork("저장된 결과 파일을 다시 읽는 중입니다.");
        try
        {
            VerificationResult outcome = await Task.Run(() => ExportService.Verify(result.PackageDirectory, work.Token), work.Token);
            ExportReportBox.Text = FormatVerification(outcome);
            WorkStatus.Text = outcome.Success ? "검증 완료 · 모든 산출물의 크기와 SHA-256이 일치합니다." : "검증에 실패한 항목을 확인하세요.";
        }
        catch (OperationCanceledException)
        {
            WorkStatus.Text = "결과 검증을 취소했습니다.";
        }
        catch (Exception error) when (IsExpected(error))
        {
            WorkStatus.Text = $"결과를 검증하지 못했습니다. {error.Message}";
        }
        finally
        {
            EndWork(work);
        }
        ReturnToSearchButton.Focus();
    }

    private async void ReviewRecentExports(object sender, RoutedEventArgs e)
    {
        if (!CanChooseSources) return;
        CaptureReturnPosition();
        _pendingExport = null;
        _exportResult = null;
        _exportCommand = null;
        ExportTitle.Text = L("최근 내보내기 검증", "Verify recent exports");
        ExportOptionsPanel.Visibility = Visibility.Collapsed;
        DestinationPanel.Visibility = Visibility.Collapsed;
        SaveExportButton.Visibility = Visibility.Collapsed;
        VerifyExportButton.Visibility = Visibility.Collapsed;
        ReturnToSearchButton.Content = _session is null ? L("닫기", "Close") : ExportReturnLabel();
        ExportReportBox.Clear();
        ShowExportPanel();
        var work = StartWork("최근 내보내기의 기록과 저장 파일을 읽는 중입니다.");
        try
        {
            IReadOnlyList<VerificationResult> history = await Task.Run(() => ExportService.RecoverRecentExports(work.Token), work.Token);
            ExportReportBox.Text = history.Count == 0 ? "확인할 최근 내보내기가 없습니다."
                : string.Join("\n\n", history.Select(FormatVerification));
            WorkStatus.Text = $"최근 내보내기 {history.Count:N0}건을 확인했습니다.";
        }
        catch (OperationCanceledException)
        {
            WorkStatus.Text = "결과 확인을 취소했습니다.";
        }
        catch (Exception error) when (IsExpected(error))
        {
            ExportReportBox.Text = error.Message;
            WorkStatus.Text = "최근 결과를 확인하지 못했습니다.";
        }
        finally
        {
            EndWork(work);
        }
        ReturnToSearchButton.Focus();
    }

    private static string FormatVerification(VerificationResult outcome) =>
        FormatExportReport(outcome.PackageDirectory, outcome.Items,
            outcome.Success ? L($"검증 완료 · 산출물 {outcome.Items.Count:N0}개의 크기와 SHA-256 일치",
                $"Verified · sizes and SHA-256 match for {outcome.Items.Count:N0} files") : L("재확인 필요", "Verification required"))
        + (outcome.Errors.Count == 0 ? "" : "\n" + string.Join(Environment.NewLine, outcome.Errors));

    private static string FormatExportReport(string directory, IReadOnlyList<ExportItem> items, string status)
    {
        var report = new StringBuilder().AppendLine(status).AppendLine(directory).AppendLine();
        foreach (ExportItem item in items)
        {
            report.AppendLine(item.RelativePath).AppendLine($"  {item.Length:N0} {L("바이트", "bytes")} · {ExportNature(item.Nature)}")
                .AppendLine($"  SHA-256  {item.Sha256}");
            if (item.CompoundStream is { } stream)
                report.AppendLine($"  {L("CFB 스트림", "CFB stream")}  {FormatStreamLocation(stream)}");
            else if (item.PstProperty is { } property)
                report.AppendLine($"  {L("MAPI 속성", "MAPI property")}  {FormatPstLocation(property)}");
            else if (item.InputRange is { } range)
                report.AppendLine($"  {L("원본 파일 범위", "Source file range")}  {range}");
        }
        return report.ToString();
    }

    private static string ExportNature(string nature) => nature switch
    {
        "msg-original" => L("MSG 원본 사본", "Original MSG copy"),
        "msg-attachment-stream" => L("첨부 스트림 사본", "Attachment stream copy"),
        "eml-original" => L("EML 원본 사본", "Original EML copy"),
        "mbox-raw-range" => L("MBOX 원본 구간", "Raw MBOX range"),
        "decoded-attachment" => L("디코딩한 첨부", "Decoded attachment"),
        "pst-message-metadata" => L("PST 메시지 근거", "PST message evidence"),
        "pst-transport-header-property" => L("원본 전송 헤더 속성", "Original transport header property"),
        "pst-attachment-property" => L("PST 첨부 속성 바이트", "PST attachment property bytes"),
        "ost-message-metadata" => L("OST 메시지 근거", "OST message evidence"),
        "ost-transport-header-property" => L("OST에 저장된 전송 헤더 속성", "Transport header property stored in the OST"),
        "ost-attachment-property" => L("OST 첨부 속성 바이트", "OST attachment property bytes"),
        "analysis-csv" => "CSV",
        _ => nature
    };

    private void SetExportInputsEnabled(bool enabled)
    {
        DestinationPanel.IsEnabled = enabled;
        ExportOptionsPanel.IsEnabled = enabled;
    }

    private void CaptureReturnPosition()
    {
        _returnRow = ResultsGrid.SelectedItem as ResultRow;
        ScrollViewer? scroll = FindVisualChild<ScrollViewer>(ResultsGrid);
        _returnVertical = scroll?.VerticalOffset ?? 0;
        _returnHorizontal = scroll?.HorizontalOffset ?? 0;
    }

    private void ShowExportPanel()
    {
        ExportPanel.Visibility = Visibility.Visible;
        WorkspaceTabs.IsEnabled = false;
        UpdateCommands();
    }

    private void ReturnToSearch(object sender, RoutedEventArgs e)
    {
        if (_work is not null) return;
        FrameworkElement? command = _exportCommand;
        CloseExportPanel();
        if (_session is null)
        {
            WorkspaceTabs.SelectedItem = SourcesTab;
            return;
        }
        WorkspaceTabs.SelectedItem = ActiveMailboxTab;
        bool resumeMessage = _readerIsNarrow && _readerExpanded;
        WorkStatus.Text = resumeMessage ? L("메일로 돌아왔습니다.", "Returned to the message.")
            : L("검색 결과로 돌아왔습니다.", "Returned to search results.");
        ResultRow? row = _returnRow;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RestoreExportFocus(command, row)));
    }

    private string ExportReturnLabel() => _readerIsNarrow && _readerExpanded
        ? L("메일로 돌아가기", "Return to message")
        : L("검색 결과로 돌아가기", "Return to search");

    private void CancelExportConfirmation()
    {
        if (_exportCommand is not { } command) return;
        CloseExportPanel();
        WorkStatus.Text = L("내보내기 확인을 취소했습니다.", "Export confirmation canceled.");
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (TryFocusExportOrigin(command)) return;
            RestoreExportFocus(null, _returnRow);
        }));
    }

    private void RestoreExportFocus(FrameworkElement? command, ResultRow? row)
    {
        if (_readerIsNarrow && _readerExpanded)
        {
            if (!TryFocusExportOrigin(command))
                TryFocusExportOrigin(ReaderSubject);
            return;
        }
        RestoreResultFocus(row);
    }

    private bool TryFocusExportOrigin(FrameworkElement? element)
    {
        if (element is not { IsVisible: true, IsEnabled: true })
            return false;

        FocusManager.SetFocusedElement(this, element);
        return Keyboard.Focus(element) == element || FocusManager.GetFocusedElement(this) == element;
    }

    private void CloseExportPanel()
    {
        _pendingExport = null;
        _exportResult = null;
        _exportCommand = null;
        ExportPanel.Visibility = Visibility.Collapsed;
        WorkspaceTabs.IsEnabled = true;
        UpdateCommands();
    }

    private void RestoreResultFocus(ResultRow? row)
    {
        if (row is null || !ResultsGrid.Items.Contains(row))
        {
            FocusManager.SetFocusedElement(this, SearchQueryBox);
            SearchQueryBox.Focus();
            return;
        }
        if (!ReferenceEquals(ResultsGrid.SelectedItem, row)) ResultsGrid.SelectedItem = row;
        IInputElement target = ResultsGrid.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow
            ?? (IInputElement)ResultsGrid;
        FocusManager.SetFocusedElement(this, target);
        Keyboard.Focus(target);
        if (FindVisualChild<ScrollViewer>(ResultsGrid) is { } scroll)
        {
            scroll.ScrollToVerticalOffset(_returnVertical);
            scroll.ScrollToHorizontalOffset(_returnHorizontal);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
