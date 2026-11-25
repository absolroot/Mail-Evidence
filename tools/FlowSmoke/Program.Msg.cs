using JournalDesk;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunMsg(string workspace, string output, string? theme)
    {
        var inputsDirectory = Path.Combine(workspace, "Mail-Viewer-Research", "evidence", "followup", "inputs");
        var mail = Path.Combine(inputsDirectory, "tika-test-outlook.msg");
        var attachments = Path.Combine(inputsDirectory, "poi-attachment-msg-pdf.msg");
        var originalHashes = new[] { mail, attachments }.ToDictionary(path => path, Hash);
        var oraclePath = Path.Combine(workspace, "Mail-Viewer", "tests", "fixtures", "msg-oracle.json");
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(oraclePath));
        var ansiOracle = oracle.RootElement.GetProperty("tikaAnsi").GetProperty("header");
        var poiOracle = oracle.RootElement.GetProperty("poiUnicode");
        var unicodeOracle = poiOracle.GetProperty("header");
        var pdfOracle = poiOracle.GetProperty("attachment");
        var steps = new List<object>();
        var window = new MainWindow([mail, attachments]) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        await Idle();
        if (theme is not null)
        {
            ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
            await Idle();
        }
        Click(window, "AnalyzeButton");
        var tabs = Field<TabControl>(window, "WorkspaceTabs");
        var grid = Field<DataGrid>(window, "ResultsGrid");
        var details = Field<TabControl>(window, "DetailTabs");
        var raw = Field<HexEvidenceView>(window, "RawBytesBox");
        await WaitFor(() => tabs.SelectedItem == Field<TabItem>(window, "InvestigationTab") &&
            grid.Items.Count == 3, "공개 MSG 두 건과 내장 메시지 읽기");
        var menuHeight = Field<Menu>(window, "MainMenu").ActualHeight;
        grid.UpdateLayout();
        double rowHeight = (grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow)?.ActualHeight ?? 0;
        Require(Math.Abs(menuHeight - 25) <= 1 && rowHeight >= 60 && grid.HeadersVisibility == DataGridHeadersVisibility.None,
            $"공용 메뉴 높이 또는 메일 목록 행 배치가 다릅니다. 메뉴 {menuHeight:F2}, 행 {rowHeight:F2}, 머리글 {grid.ColumnHeaderHeight:F2} DIP");

        await Search(window, SearchField.Subject, "Outlook Express 6", 1);
        grid.SelectedIndex = 0;
        var ansi = RecordAt(grid, 0);
        await Idle();
        var subject = Field<CopyText>(window, "ReaderSubject");
        subject.ApplyTemplate();
        var copySubject = (Button)subject.Template.FindName("PART_Copy", subject);
        Require(copySubject is not null && copySubject.Visibility == Visibility.Hidden,
            "선택하지 않은 제목에 복사 아이콘이 상시 표시됩니다.");
        string? copied = null;
        DataObjectCopyingEventHandler checkCopy = (_, args) =>
        {
            copied = args.DataObject.GetData(DataFormats.UnicodeText) as string;
            args.CancelCommand();
        };
        DataObject.AddCopyingHandler(subject, checkCopy);
        copySubject!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, copySubject));
        DataObject.RemoveCopyingHandler(subject, checkCopy);
        Require(copied == ansi.Subject, "제목의 복사 명령이 원래 값을 전달하지 않습니다.");
        Require(ansi.Format == MailFormat.Msg && ansi.TransportHeaderState == HeaderEvidenceState.Present &&
            ansi.DateRaw.Contains("-0700", StringComparison.Ordinal), "ANSI 전송 헤더와 원본 시간대가 다릅니다.");
        await CheckHeader(ansi, "Date", ansiOracle, "01-ansi-header.png");

        const string pdfQuery = "smbprn";
        await Search(window, SearchField.AttachmentName, pdfQuery, 1);
        grid.SelectedIndex = 0;
        var poi = RecordAt(grid, 0);
        Require(poi.Format == MailFormat.Msg && poi.SourcePath == attachments &&
            poi.TransportHeaderState == HeaderEvidenceState.Present, "PDF 이름 검색에 다른 MSG가 반환됐습니다.");
        await CheckHeader(poi, "Received", unicodeOracle, "02-unicode-header.png");

        details.SelectedItem = Field<TabItem>(window, "AttachmentsTab");
        var attachmentGrid = Field<DataGrid>(window, "AttachmentsGrid");
        await Idle();
        var pdf = poi.Attachments.Single(item => item.Name.Contains(pdfQuery, StringComparison.OrdinalIgnoreCase));
        Require((attachmentGrid.SelectedItem as MailAttachment)?.Id == pdf.Id, "검색으로 찾은 첨부가 먼저 선택되지 않았습니다.");
        var embedded = poi.Attachments.Single(item => item.CompoundStoragePath is not null);
        attachmentGrid.SelectedIndex = Array.IndexOf(poi.Attachments.ToArray(), embedded);
        Require(EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value.Contains("3701000D", StringComparison.Ordinal)) &&
            !Field<MenuItem>(window, "ExportAttachmentButton").IsEnabled &&
            !Field<Button>(window, "SaveAttachmentFileButton").IsEnabled,
            "중첩 MSG의 저장소나 저장 가능 여부가 모호합니다.");
        Capture(window, output, "03-embedded-storage.png");
        attachmentGrid.SelectedIndex = Array.IndexOf(poi.Attachments.ToArray(), pdf);
        RequireLocator(pdf.CompoundStream, pdfOracle);
        Require(pdf.Sha256 == pdfOracle.GetProperty("sha256").GetString() &&
            pdf.DecodedLength == pdfOracle.GetProperty("length").GetInt64(), "PDF 첨부의 크기 또는 해시가 다릅니다.");
        Require(attachmentGrid.Columns.Count == 3 &&
            EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value == pdf.CompoundStream!.TagHex) &&
            EvidenceFields(window, "AttachmentDetailFields").Any(field => field.Value == pdf.Sha256),
            "the selected attachment exposes stream details and the complete hash separately");
        Capture(window, output, "04-attachment.png");
        Click(window, "OpenAttachmentBytesButton");
        await WaitFor(() => details.SelectedItem == Field<TabItem>(window, "RawTab") &&
            raw.Text.Contains("%PDF-", StringComparison.Ordinal), "PDF 스트림 Hex");
        RequireRawContext(window, pdf.CompoundStream!);
        var independentPdf = pdfOracle.GetProperty("base64").GetBytesFromBase64();
        var visiblePdf = DisplayedBytes(raw.Text, 0);
        Require(visiblePdf.Length >= 512 && independentPdf.AsSpan(0, visiblePdf.Length).SequenceEqual(visiblePdf),
            "PDF 스트림 Hex가 독립 CFB 판독과 다릅니다.");
        Capture(window, output, "05-attachment-stream.png");
        Click(window, "RawMessageButton");
        var fileView = DisplayedBytes(raw.Text, 0);
        Require(Field<TextBlock>(window, "RawStatus").Text.Contains("파일 오프셋", StringComparison.Ordinal) &&
            fileView.AsSpan(0, 4).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }) &&
            Field<Expander>(window, "RawLocationExpander").IsVisible &&
            EvidenceFields(window, "RawLocationFields").Any(field => field.Value == attachments) &&
            EvidenceFields(window, "RawLocationFields").All(field => field.Value != pdf.CompoundStream!.TagHex),
            "MSG 전체 파일 주소와 첨부 스트림 주소를 구분하지 못했습니다.");
        PressEscape(window);
        await Idle();
        Require(details.SelectedItem == Field<TabItem>(window, "AttachmentsTab") &&
            (attachmentGrid.SelectedItem as MailAttachment)?.Id == pdf.Id, "원본에서 돌아왔을 때 첨부가 달라졌습니다.");
        var directPdf = await SaveAttachmentDirectly(window, output, pdf);
        Require(File.ReadAllBytes(directPdf).SequenceEqual(independentPdf),
            "직접 저장한 PDF가 원본 CFB 스트림과 다릅니다.");
        var sortMethod = typeof(DataGrid).GetMethod("PerformSort",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WPF 정렬 명령을 찾을 수 없습니다.");
        var nameColumn = attachmentGrid.Columns[0];
        string[] attachmentOrder = attachmentGrid.Items.Cast<MailAttachment>().Select(item => item.Name).ToArray();
        foreach (var expectedSort in new System.ComponentModel.ListSortDirection?[]
        {
            System.ComponentModel.ListSortDirection.Ascending, System.ComponentModel.ListSortDirection.Descending, null
        })
        {
            sortMethod.Invoke(attachmentGrid, [nameColumn]);
            await Idle();
            Require(nameColumn.SortDirection == expectedSort, "첨부 열 정렬 표시가 세 단계로 순환하지 않았습니다.");
        }
        Require(attachmentGrid.Items.Cast<MailAttachment>().Select(item => item.Name).SequenceEqual(attachmentOrder)
            && (attachmentGrid.SelectedItem as MailAttachment)?.Id == pdf.Id, "기본 정렬 복귀에서 첨부 순서나 선택이 달라졌습니다.");
        Click(window, "ExportAttachmentButton");
        await WaitFor(() => Field<TextBox>(window, "DestinationBox").IsVisible, "첨부 저장 확인 화면");
        Require(!Field<ComboBox>(window, "ExportKindBox").IsVisible, "고정된 첨부 형식에 불필요한 선택 상자가 보입니다.");
        PressEscape(window);
        await WaitFor(() => !Field<FrameworkElement>(window, "ExportPanel").IsVisible, "첨부 저장 확인 취소");
        var attachmentFocus = FocusManager.GetFocusedElement(window);
        Require(details.SelectedItem == Field<TabItem>(window, "AttachmentsTab") &&
            (attachmentGrid.SelectedItem as MailAttachment)?.Id == pdf.Id &&
            (attachmentFocus == Field<MenuItem>(window, "ExportMenu") ||
                attachmentFocus == attachmentGrid || attachmentFocus is DataGridRow focusRow &&
                focusRow.Item == attachmentGrid.SelectedItem), "저장 확인을 취소한 뒤 원래 첨부 조작 위치로 돌아가지 못했습니다.");
        var attachmentPackage = await Export(window, "ExportAttachmentButton", output,
            captureName: "06-attachment-verification.png");
        var pdfFile = Directory.EnumerateFiles(attachmentPackage, "*.pdf").Single();
        Require(File.ReadAllBytes(pdfFile).SequenceEqual(independentPdf), "내보낸 PDF가 원본 CFB 스트림과 다릅니다.");
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(attachmentPackage, "manifest.json"))))
        {
            var item = manifest.RootElement.GetProperty("items")[0];
            Require(item.GetProperty("nature").GetString() == "msg-attachment-stream" &&
                item.GetProperty("compoundStream").GetProperty("displayPath").GetString() == pdf.CompoundStream!.DisplayPath,
                "PDF 매니페스트에 CFB 근거가 빠졌습니다.");
        }
        RequireReturn(window, grid, poi.Id, pdfQuery);
        steps.Add(new { Check = "public POI MSG attachment and verification", pdf.CompoundStream, pdf.Sha256,
            ExactBytes = independentPdf.Length, VisibleBytesCompared = visiblePdf.Length, Package = attachmentPackage,
            DirectFile = directPdf, FileAndStreamOffsetsSeparated = true, AttachmentCancelRestored = true });

        var msgPackage = await Export(window, "ExportSearchButton", output, ExportKind.MessageBytes,
            "07-original-msg-verification.png");
        var savedMsg = Directory.GetFiles(msgPackage, "*.msg").Single();
        Require(Hash(savedMsg) == originalHashes[attachments] && File.ReadAllBytes(savedMsg).SequenceEqual(File.ReadAllBytes(attachments)),
            "선택한 MSG 원본 사본이 입력과 다릅니다.");
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(msgPackage, "manifest.json"))))
        {
            var record = manifest.RootElement.GetProperty("records")[0];
            Require(record.GetProperty("id").GetString() == poi.Id &&
                record.GetProperty("headerRangeKind").GetString() == "compound-stream-offset",
                "원본 내보내기 기록의 MSG 위치 유형이 다릅니다.");
        }
        RequireReturn(window, grid, poi.Id, pdfQuery);
        Capture(window, output, "08-return.png");
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(enter);
        await Idle();
        Require(enter.Handled && Field<FrameworkElement>(window, "MailboxReaderPane").IsVisible,
            "복귀 후 Enter가 같은 MSG의 읽기 창을 열지 못했습니다.");
        await ShowRawHeaders(window);
        await Idle();
        Require(details.SelectedItem == Field<TabItem>(window, "HeadersTab") &&
            RawHeaderNames(window).SequenceEqual(poi.Headers.Select(header => header.Name), StringComparer.OrdinalIgnoreCase), "복귀 후 Enter가 선택했던 MSG의 헤더를 열지 못했습니다.");
        steps.Add(new { Check = "byte-exact original MSG export and return", Sha256 = originalHashes[attachments],
            Package = msgPackage, SelectedRecordPreserved = true, EnterOpenedSelectedHeaders = true });

        await ReturnToMailboxList(window);
        await Search(window, SearchField.Any, "", 3);
        var ansiRow = grid.Items.Cast<object>().Select((row, index) => (Record: RecordFromRow(row), Index: index))
            .Single(item => item.Record.Id == ansi.Id).Index;
        grid.SelectedIndex = ansiRow;
        var mode = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "SelectionModeButton");
        var exportChecked = Field<Button>(window, "ExportCheckedButton");
        var checkColumn = (DataGridColumn)window.FindName("MailboxCheckColumn");
        Require(mode.IsChecked != true && checkColumn.Visibility == Visibility.Collapsed && !exportChecked.IsVisible,
            "기본 메일 목록에 체크 상자나 선택 내보내기가 보입니다.");
        var checkKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(checkKey);
        Require(checkKey.Handled && mode.IsChecked == true && checkColumn.Visibility == Visibility.Visible,
            "Space로 선택 모드를 열고 조사 행을 체크할 수 없습니다.");
        await Idle();
        Require(exportChecked is { IsVisible: true, IsEnabled: true },
            "메일을 체크했지만 목록에 내보내기 버튼이 나타나지 않습니다.");
        Capture(window, output, "11-multiple-selection.png");
        grid.SelectedIndex = ansiRow == 0 ? 1 : 0;
        Require(RecordAt(grid, grid.SelectedIndex).Id == poi.Id, "선택과 체크를 구분하는 대조 행이 다릅니다.");
        var selectedPackage = await Export(window, "ExportSelectedButton", output, ExportKind.MessageBytes);
        var checkedPackage = await Export(window, "ExportCheckedButton", output, ExportKind.MessageBytes);
        Require(Hash(Directory.GetFiles(selectedPackage, "*.msg").Single()) == originalHashes[attachments] &&
            Hash(Directory.GetFiles(checkedPackage, "*.msg").Single()) == originalHashes[mail],
            "선택한 행과 체크한 행이 서로 다른 상황에서 잘못된 MSG가 저장됐습니다.");
        steps.Add(new { Check = "selected and checked scopes remain distinct", Selected = poi.Id, Checked = ansi.Id,
            SelectedPackage = selectedPackage, CheckedPackage = checkedPackage });

        var checkSecond = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(checkSecond);
        Require(checkSecond.Handled && exportChecked.IsVisible && exportChecked.IsEnabled &&
            Field<TextBlock>(window, "SearchSummary").Text.Contains("2건 선택", StringComparison.Ordinal),
            "메일 두 건을 체크했을 때 선택 건수와 내보내기를 표시하지 못했습니다.");
        Capture(window, output, "11-multiple-selection.png");
        var both = await Export(window, "ExportCheckedButton", output, ExportKind.MessageBytes);
        Require(Directory.GetFiles(both, "*.msg").Select(Hash).ToHashSet()
            .SetEquals(new[] { originalHashes[attachments], originalHashes[mail] }),
            "목록에서 체크한 메일 두 건을 그대로 저장하지 못했습니다.");
        mode.IsChecked = false;
        mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, mode));
        Require(checkColumn.Visibility == Visibility.Collapsed && !exportChecked.IsVisible && !exportChecked.IsEnabled,
            "선택 모드를 끝냈지만 체크 상자나 선택 내보내기가 남아 있습니다.");
        await Search(window, SearchField.AttachmentName, pdfQuery, 1);
        grid.SelectedIndex = 0;
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        window.Width = 780;
        window.Height = 600;
        await Idle();
        await ReturnToMailboxList(window);
        Require(Field<Button>(window, "SearchButton").IsVisible
            && RecordAt(grid, grid.SelectedIndex).Id == poi.Id, "좁은 창에서 검색과 선택 행을 확인할 수 없습니다.");
        var read = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(read);
        await Idle();
        await ShowRawHeaders(window);
        await Idle();
        var headerBox = details.SelectedContent is DependencyObject content
            ? FindVisualDescendant<TextBox>(content, "RawHeaderText") : null;
        Require(headerBox is { IsVisible: true, ActualHeight: >= 90 } && headerBox.Text.Contains("Date:", StringComparison.OrdinalIgnoreCase),
            "좁은 메일 읽기 창에서 전송 헤더 원문을 충분히 표시하지 못했습니다.");
        Capture(window, output, "09-narrow.png");
        var narrowHeaderHeight = headerBox!.ActualHeight;
        var detailHeight = details.ActualHeight;
        details.SelectedItem = Field<TabItem>(window, "AttachmentsTab");
        var attachmentPicker = Field<ComboBox>(window, "AttachmentPicker");
        var previewPane = Field<FrameworkElement>(window, "AttachmentPreviewPane");
        await WaitFor(() => attachmentPicker.IsVisible && previewPane.IsVisible,
            "좁은 창의 첨부파일 선택과 미리보기");
        await Idle();
        Require(Math.Abs(details.ActualHeight - detailHeight) <= 1 &&
            attachmentPicker.SelectedItem is MailAttachment selectedAttachment && selectedAttachment.Id == pdf.Id &&
            !attachmentGrid.IsVisible && attachmentPicker.ActualHeight >= 24 && previewPane.ActualHeight >= 120,
            "좁은 창에서 선택한 첨부파일이나 미리보기 공간이 유지되지 않았습니다.");
        double narrowAttachmentPreviewHeight = previewPane.ActualHeight;
        Capture(window, output, "10-narrow-attachments.png");
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        SelectRawHeader(window, poi, poi.Headers[0]);
        Click(window, "RawHeaderBytesButton");
        await Idle();
        Require(raw.IsVisible && raw.ActualHeight >= 135 &&
            !Field<StackPanel>(window, "RawPager").IsVisible, "좁은 창의 Hex 표 높이나 페이지 표시가 올바르지 않습니다.");
        Capture(window, output, "12-narrow-hex.png");
        Require(originalHashes.All(item => Hash(item.Key) == item.Value), "입력 MSG의 SHA-256이 달라졌습니다.");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            Checks = steps, Theme = theme, OriginalsUnchanged = true, MenuHeight = menuHeight,
            RowHeight = rowHeight, grid.ColumnHeaderHeight, NarrowRawHeaderHeight = narrowHeaderHeight,
            NarrowAttachmentPreviewHeight = narrowAttachmentPreviewHeight, SplitPositionPreserved = true,
            Sources = originalHashes.ToDictionary(item => Path.GetFileName(item.Key), item => item.Value)
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();

        async Task CheckHeader(MailRecord record, string field, JsonElement reference, string screenshot)
        {
            RequireLocator(record.TransportHeaderStream, reference);
            details.SelectedItem = Field<TabItem>(window, "HeadersTab");
            await Idle();
            Require(RawHeaderNames(window).Length == reference.GetProperty("fields").GetArrayLength(),
                "화면의 전송 헤더 줄 수가 다릅니다.");
            var expectedField = reference.GetProperty("fields").EnumerateArray()
                .First(value => value.GetProperty("name").GetString() == field);
            var index = record.Headers.ToList().FindIndex(header => header.Name == field);
            Require(index >= 0, $"MSG 헤더에 {field}가 없습니다.");
            var range = record.Headers[index].Range;
            Require(range.Offset == expectedField.GetProperty("offset").GetInt64() &&
                range.Length == expectedField.GetProperty("length").GetInt64(), "전송 헤더 필드의 상대 위치가 다릅니다.");
            var firstSelection = SelectRawHeader(window, record, record.Headers[index]);
            Click(window, "RawHeaderBytesButton");
            await WaitFor(() => details.SelectedItem == Field<TabItem>(window, "RawTab"), "전송 헤더 Hex");
            RequireRawContext(window, record.TransportHeaderStream!);
            var expected = reference.GetProperty("base64").GetBytesFromBase64();
            Require(expected.AsSpan((int)range.Offset, (int)range.Length).SequenceEqual(DisplayedBytes(raw.Text, range.Offset)),
                "전송 헤더 Hex가 독립 CFB 판독과 다릅니다.");
            Capture(window, output, screenshot);
            Click(window, "RawBackButton");
            Require(details.SelectedItem == Field<TabItem>(window, "HeadersTab") && Field<TextBox>(window, "RawHeaderText").SelectedText == firstSelection,
                "원본 확인 뒤 전송 헤더 행이 바뀌었습니다.");
            steps.Add(new { Check = "MSG transport header", record.TransportHeaderStream,
                Field = field, Range = range, StreamSha256 = reference.GetProperty("sha256").GetString(),
                HeaderSelectionRestored = true });
        }
    }

    private static void PressEscape(MainWindow window)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
            Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(key);
        Require(key.Handled, "현재 화면이 Esc 동작을 처리하지 않았습니다.");
    }

    private static void RequireReturn(MainWindow window, DataGrid grid, string id, string query)
    {
        var focus = FocusManager.GetFocusedElement(window);
        Require(Field<TextBox>(window, "SearchQueryBox").Text == query && grid.Items.Count == 1 &&
            RecordAt(grid, grid.SelectedIndex).Id == id &&
            (focus == grid || focus is DataGridRow row && row.Item == grid.SelectedItem),
            "MSG 내보내기 뒤 검색, 선택 또는 논리 포커스가 달라졌습니다.");
    }

    private static void RequireRawContext(MainWindow window, CompoundStreamLocator locator)
    {
        var status = Field<TextBlock>(window, "RawStatus").Text;
        var source = EvidenceFields(window, "RawLocationFields");
        Require(status.Contains("스트림 오프셋", StringComparison.Ordinal) &&
            source.Any(field => field.Value == locator.DisplayPath) &&
            source.Any(field => field.Value == locator.TagHex),
            "원본 창에서 CFB 스트림 또는 MAPI 태그를 식별할 수 없습니다.");
    }

    private static void RequireLocator(CompoundStreamLocator? locator, JsonElement reference)
    {
        Require(locator is not null &&
            locator.StreamName == reference.GetProperty("streamName").GetString() &&
            locator.PropertyTag == reference.GetProperty("propertyTag").GetUInt32() &&
            locator.Length == reference.GetProperty("length").GetInt64() &&
            locator.StoragePath.SequenceEqual(reference.GetProperty("storagePath").EnumerateArray()
                .Select(item => item.GetString())),
            "CFB 근거의 스트림, 태그 또는 크기가 독립 판독 결과와 다릅니다.");
    }

    private static byte[] DisplayedBytes(string text, long offset)
    {
        var bytes = new List<byte>();
        foreach (var line in text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            int addressWidth = line.IndexOf("  ", StringComparison.Ordinal);
            Require(addressWidth >= 8 && line.Length >= addressWidth + 51 &&
                long.Parse(line.AsSpan(0, addressWidth), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    == offset + bytes.Count, "Hex 줄의 위치가 예상 오프셋과 다릅니다.");
            bytes.AddRange(line.Substring(addressWidth + 2, 47).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Convert.ToByte(value, 16)));
        }
        return bytes.ToArray();
    }
}
