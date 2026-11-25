using JournalDesk;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunPst(string workspace, string output, string? theme)
    {
        var fixtures = Path.Combine(workspace, "Mail-Viewer", "tests", "fixtures");
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "pst-oracle.json")));
        var tika = oracle.RootElement.GetProperty("tika");
        var attachmentSample = oracle.RootElement.GetProperty("attachment");
        var tikaPath = Path.Combine(workspace, "Mail-Viewer-Research", "fixtures", tika.GetProperty("file").GetString()!);
        var attachmentPath = Path.Combine(fixtures, "pst", attachmentSample.GetProperty("file").GetString()!);
        var originalHashes = new[] { tikaPath, attachmentPath }.ToDictionary(path => path, Hash);
        Require(originalHashes[tikaPath] == tika.GetProperty("sha256").GetString() &&
            originalHashes[attachmentPath] == attachmentSample.GetProperty("sha256").GetString(),
            "PST 입력이 독립 오라클을 만든 원본과 다릅니다.");

        var steps = new List<object>();
        var window = new MainWindow([tikaPath, attachmentPath]) { ShowInTaskbar = false };
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
        var investigation = Field<TabItem>(window, "InvestigationTab");
        var tabs = Field<TabControl>(window, "WorkspaceTabs");
        var grid = Field<DataGrid>(window, "ResultsGrid");
        var tree = Field<TreeView>(window, "PstFolderTree");
        var detail = Field<TabControl>(window, "DetailTabs");
        var raw = Field<HexEvidenceView>(window, "RawBytesBox");
        await WaitFor(() => tabs.SelectedItem == investigation && grid.Items.Count == 8, "두 PST 폴더와 메시지 8건");
        var folderToggle = Field<ToggleButton>(window, "PstFoldersToggle");
        if (folderToggle.IsChecked != true)
            folderToggle.IsChecked = true;
        await Idle();
        Require(tree.IsVisible, "PST 폴더 탐색을 열 수 없습니다.");

        var tikaMessage = tika.GetProperty("messages").EnumerateArray()
            .Single(message => message.GetProperty("subject").GetString() == "Re: Feature Generators");
        var tikaFolder = FindFolder(tree, tikaMessage.GetProperty("folderNid").GetUInt32(),
            "Début du fichier de données Outlook");
        await ApplyFolder(tikaFolder, 6);
        const string draft = "unapplied PST query";
        Field<TextBox>(window, "SearchQueryBox").Text = draft;
        await ApplyFolder(FindFolder(tree, 32866, "Éléments supprimés"), 0);
        await ApplyFolder(tikaFolder, 6);
        Require(Field<TextBox>(window, "SearchQueryBox").Text == draft &&
            Field<TextBlock>(window, "SearchSummary").Text.Contains("미적용", StringComparison.Ordinal),
            "PST 폴더를 탐색하면서 미적용 검색 조건을 잃었거나 몰래 적용했습니다.");
        Capture(window, output, "01-pst-folders.png");
        const string headerQuery = "Re: Feature Generators";
        await Search(window, SearchField.Subject, headerQuery, 1);
        grid.SelectedIndex = 0;
        var result = RecordAt(grid, 0);
        var tikaLocation = RequireMessage(result, tikaMessage, tikaPath);
        var expectedHeader = tikaMessage.GetProperty("header");
        var headerBytes = expectedHeader.GetProperty("base64").GetBytesFromBase64();
        var property = result.TransportHeaderProperty;
        Require(result.TransportHeaderState == HeaderEvidenceState.Present &&
            property is not null && property.Message == tikaLocation &&
            property.PropertyTag == expectedHeader.GetProperty("tag").GetUInt32() &&
            property.Length == headerBytes.Length, "PST 전송 헤더의 메시지·속성 근거가 독립 판독과 다릅니다.");
        detail.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        var received = result.Headers.Where(row => row.Name == "Received").ToArray();
        Require(received.Length == tikaMessage.GetProperty("receivedCount").GetInt32() &&
            RawHeaderNames(window).Length == result.Headers.Count, "원본 전송 헤더의 Received 순서 또는 화면 행 수가 다릅니다.");
        var first = received[0];
        var expectedStart = Encoding.Unicode.GetString(headerBytes).IndexOf("Received:", StringComparison.Ordinal) * 2;
        Require(first.Range.Offset == expectedStart, "Received의 Unicode 속성 상대 오프셋이 다릅니다.");
        var firstSelection = SelectRawHeader(window, result, first);
        Click(window, "RawHeaderBytesButton");
        await WaitFor(() => detail.SelectedItem == Field<TabItem>(window, "RawTab") && raw.Text.Length > 0,
            "PST 전송 헤더 속성 Hex");
        RequirePstRawContext(window, property!);
        var fullProperty = Field<Button>(window, "RawMessageButton");
        Require(!fullProperty.IsVisible || fullProperty.Content?.ToString() == "전체 속성",
            "PST 메시지에 물리 파일 범위 명령이 보입니다.");
        Require(headerBytes.AsSpan((int)first.Range.Offset, (int)first.Range.Length)
            .SequenceEqual(DisplayedBytes(raw.Text, first.Range.Offset)), "화면 Received가 독립 PST 바이트와 다릅니다.");
        Capture(window, output, "02-transport-property.png");
        Click(window, "RawBackButton");
        Require(detail.SelectedItem == Field<TabItem>(window, "HeadersTab") && Field<TextBox>(window, "RawHeaderText").SelectedText == firstSelection,
            "헤더 속성에서 돌아온 뒤 원래 행이 달라졌습니다.");
        var headerPackage = await Export(window, "ExportSearchButton", output, ExportKind.PstEvidence,
            "03-pst-message-verification.png");
        var savedHeader = Directory.GetFiles(headerPackage, "*-transport-header.bin").Single();
        Require(File.ReadAllBytes(savedHeader).SequenceEqual(headerBytes), "저장한 PST 전송 헤더 속성 바이트가 다릅니다.");
        VerifyManifest(headerPackage, "pst-transport-header-property", tikaLocation, originalHashes[tikaPath]);
        RequirePstReturn(window, grid, tikaFolder, result.Id, headerQuery);
        var afterReturn = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(afterReturn);
        Require(afterReturn.Handled && detail.SelectedItem == Field<TabItem>(window, "HeadersTab") &&
            RawHeaderNames(window).SequenceEqual(result.Headers.Select(header => header.Name), StringComparer.OrdinalIgnoreCase), "복귀 후 Enter가 같은 PST 메시지의 헤더를 열지 못했습니다.");
        steps.Add(new { Check = "folder search and original PST transport header", tikaLocation,
            HeaderBytes = headerBytes.Length, Received = received.Length, Package = headerPackage, ReturnVerified = true });

        await Search(window, SearchField.Any, "", 6);
        var personalFolder = FindFolder(tree, 32802, "Top of Personal Folders");
        await ApplyFolder(personalFolder, 2);
        var include = Field<ToggleButton>(window, "PstSubfoldersBox");
        var countBadge = (TextBlock)personalFolder.Template.FindName("FolderCount", personalFolder);
        Require(include.IsChecked == true && countBadge.Text == "[2]" && countBadge.IsVisible,
            "직접 저장된 메일이 없는 중간 PST 폴더에서 하위 메일과 개수를 표시하지 못했습니다.");
        include.IsChecked = false;
        await WaitFor(() => grid.Items.Count == 0 && !Field<Button>(window, "CancelWorkButton").IsEnabled,
            "하위 폴더를 끄면 중간 폴더 자체의 범위만 표시");
        include.IsChecked = true;
        await WaitFor(() => grid.Items.Count == 2 && !Field<Button>(window, "CancelWorkButton").IsEnabled,
            "명시적으로 하위 폴더를 포함한 검색");
        Require(grid.Items.Cast<object>().Select(RecordFromRow).Select(row => row.PstMessage?.FolderNodeId)
            .ToHashSet().SetEquals([33090u, 33410u]), "하위 폴더 포함 범위가 다른 자료와 섞였습니다.");
        const string attachmentQuery = "1.jpg";
        await Search(window, SearchField.AttachmentName, attachmentQuery, 2);
        Capture(window, output, "10-pst-duplicate-matches.png");

        var expectedMessage = attachmentSample.GetProperty("messages").EnumerateArray()
            .Single(message => message.GetProperty("folderNid").GetUInt32() == 33090);
        var inbox = FindFolder(tree, 33090, "Inbox");
        await ApplyFolder(inbox, 1);
        await Search(window, SearchField.AttachmentName, attachmentQuery, 1);
        grid.SelectedIndex = 0;
        var parent = RecordAt(grid, 0);
        var attachmentLocation = RequireMessage(parent, expectedMessage, attachmentPath);
        Require(attachmentLocation.MessageNodeId == tikaLocation.MessageNodeId && parent.Id != result.Id,
            "서로 다른 PST의 같은 메시지 NID가 병합됐습니다.");
        detail.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        Require(parent.TransportHeaderState == HeaderEvidenceState.Missing &&
            Field<TextBlock>(window, "RawHeaderEmpty").Text.Contains("없", StringComparison.Ordinal) &&
            !Field<Button>(window, "RawHeaderBytesButton").IsEnabled,
            "헤더 속성이 없는 PST 메일에서 원본 헤더 상태가 모호합니다.");
        var attachmentOracle = expectedMessage.GetProperty("attachments").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == attachmentQuery);
        var expectedAttachment = attachmentOracle.GetProperty("property");
        var attachmentBytes = expectedAttachment.GetProperty("base64").GetBytesFromBase64();
        detail.SelectedItem = Field<TabItem>(window, "AttachmentsTab");
        await Idle();
        var table = Field<DataGrid>(window, "AttachmentsGrid");
        var matched = table.SelectedItem as MailAttachment ?? throw new InvalidOperationException("PST 첨부가 선택되지 않았습니다.");
        Require(matched.Name == attachmentQuery && matched.PstProperty is { } found &&
            found.AttachmentNodeId == attachmentOracle.GetProperty("attachmentNid").GetUInt32() &&
            found.PropertyTag == expectedAttachment.GetProperty("tag").GetUInt32(),
            "첨부 검색이 해당 메시지의 첨부 NID와 원본 속성을 가리키지 않습니다.");
        var locationDisclosure = Field<Expander>(window, "AttachmentLocationExpander");
        Require(table.Columns.Count == 3 && !locationDisclosure.IsExpanded &&
            EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value == matched!.PstProperty!.AttachmentNid) &&
            EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value == matched!.PstProperty!.TagHex) &&
            EvidenceFields(window, "AttachmentDetailFields").Any(field => field.Value == matched!.Sha256),
            "PST location is available separately and the complete hash has its own row");
        var attachmentFields = Field<ItemsControl>(window, "AttachmentDetailFields");
        if (table.Items.Count > 1)
        {
            var other = table.Items.Cast<MailAttachment>().First(attachment => attachment.Id != matched.Id);
            table.SelectedItem = other;
            await Idle();
            VerifyAlternatingRows(table);
            Require(EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value == other.PstProperty!.AttachmentNid) &&
                !EvidenceFields(window, "AttachmentDetailFields").Any(field => field.Value == matched!.Sha256),
                "changing the row updates both the location and selected attachment hash");
            table.SelectedItem = matched;
            await Idle();
        }
        Field<Expander>(window, "AttachmentInfoToggle").IsExpanded = true;
        await WaitFor(() => attachmentFields.IsVisible, "펼친 첨부파일 정보");
        VerifyAlternatingRows(table);
        Capture(window, output, "04-attachment-list.png");
        locationDisclosure.IsExpanded = true;
        await Task.Delay(550);
        await Idle();
        var locationFields = Field<ItemsControl>(window, "AttachmentLocationFields");
        var locationScroll = Field<ScrollViewer>(window, "AttachmentInfoScroll");
        var firstLocation = locationFields.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        double firstLocationTop = firstLocation?.TranslatePoint(new Point(), locationScroll).Y ?? double.PositiveInfinity;
        Require(firstLocation is { IsVisible: true } && firstLocationTop >= -1 &&
            firstLocationTop + Math.Min(firstLocation.ActualHeight, locationScroll.ViewportHeight) <= locationScroll.ViewportHeight + 1 &&
            EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value == matched!.PstProperty!.AttachmentNid),
            $"the selected attachment's location starts at {firstLocationTop:F1} DIP in the {locationScroll.ViewportHeight:F1} DIP viewport");
        Console.WriteLine($"Attachment source first row: {firstLocationTop:F1} DIP in {locationScroll.ViewportHeight:F1} DIP viewport.");
        Capture(window, output, "04b-attachment-source-location.png");
        locationDisclosure.IsExpanded = false;
        var hexButton = Field<Button>(window, "OpenAttachmentBytesButton");
        Require(hexButton.Content as string == "HEX" && hexButton.Tag is System.Windows.Media.Geometry &&
            hexButton.ToolTip is string tooltip && tooltip.Contains("MAPI", StringComparison.Ordinal),
            "첨부파일 HEX 명령에서 열리는 속성을 확인할 수 없습니다.");
        Click(window, "OpenAttachmentBytesButton");
        await WaitFor(() => detail.SelectedItem == Field<TabItem>(window, "RawTab") && raw.Text.Length > 0,
            "PST 첨부 속성 Hex");
        RequirePstRawContext(window, matched!.PstProperty!);
        Require(DisplayedBytes(raw.Text, 0).SequenceEqual(attachmentBytes), "PST 첨부 Hex가 독립 판독 바이트와 다릅니다.");
        var location = Field<Expander>(window, "RawLocationExpander");
        Require(location.IsVisible && !location.IsExpanded &&
            Field<TextBlock>(window, "RawLocationSummary").Text.Contains(matched.PstProperty!.AttachmentNid!, StringComparison.Ordinal),
            "PST property overview identifies the attachment while keeping Hex visible");
        Capture(window, output, "05-attachment-property.png");
        location.IsExpanded = true;
        await Task.Delay(550);
        await Idle();
        var rawFields = Field<ItemsControl>(window, "RawLocationFields");
        Require(rawFields.IsVisible && EvidenceFields(window, "RawLocationFields").Length >= 7,
            "expanded property location exposes separate evidence fields");
        Capture(window, output, "05b-attachment-property-location.png");
        PressEscape(window);
        await Idle();
        Require(table.SelectedItem is MailAttachment selected && selected.Id == matched.Id,
            "PST 첨부 속성에서 돌아왔을 때 선택이 바뀌었습니다.");
        var attachmentPackage = await Export(window, "ExportAttachmentButton", output,
            captureName: "06-attachment-verification.png");
        Require(File.ReadAllBytes(Directory.GetFiles(attachmentPackage, "*.jpg").Single()).SequenceEqual(attachmentBytes),
            "저장한 PST 첨부가 원본 속성 바이트와 다릅니다.");
        VerifyManifest(attachmentPackage, "pst-attachment-property", attachmentLocation, originalHashes[attachmentPath]);
        RequirePstReturn(window, grid, inbox, parent.Id, attachmentQuery);
        Capture(window, output, "07-pst-search-return.png");
        steps.Add(new { Check = "PST folder scope, attachment and verified return", attachmentLocation,
            Attachment = matched.Name, Bytes = attachmentBytes.Length, matched.Sha256,
            Property = matched.PstProperty, Package = attachmentPackage, ReturnVerified = true });

        var readerSplitter = Field<GridSplitter>(window, "MailboxSplitter");
        var readerSource = PresentationSource.FromVisual(readerSplitter);
        var resultsPane = Field<Grid>(window, "MailboxResultsPane");
        double originalResultsWidth = resultsPane.ActualWidth;
        for (int i = 0; i < 3; i++)
        {
            readerSplitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, readerSource, Environment.TickCount, Key.Right)
                { RoutedEvent = Keyboard.KeyDownEvent });
            readerSplitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, readerSource, Environment.TickCount, Key.Right)
                { RoutedEvent = Keyboard.KeyUpEvent });
        }
        await Idle();
        double keyboardResultsWidth = resultsPane.ActualWidth;
        double windowWidth = window.Width;
        window.Width += 100;
        await Idle();
        double resizedResultsWidth = resultsPane.ActualWidth;
        Require(keyboardResultsWidth > originalResultsWidth + 5 && Math.Abs(resizedResultsWidth - keyboardResultsWidth) < 1,
            $"Mail list width changed {originalResultsWidth:F1} → {keyboardResultsWidth:F1} → {resizedResultsWidth:F1} DIP.");
        Console.WriteLine($"Mail list width persisted: {originalResultsWidth:F1} → {keyboardResultsWidth:F1} → {resizedResultsWidth:F1} DIP.");
        Capture(window, output, "07c-pst-results-width-preserved.png");
        window.Width = windowWidth;
        await Idle();

        var folderSplitter = Field<GridSplitter>(window, "MailboxFolderSplitter");
        var folderPane = Field<Border>(window, "PstFolderPane");
        double folderWidth = folderPane.ActualWidth;
        folderSplitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        folderSplitter.RaiseEvent(new DragDeltaEventArgs(84, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        folderSplitter.RaiseEvent(new DragCompletedEventArgs(84, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Idle();
        Require(folderPane.ActualWidth > folderWidth + 70 && inbox.IsSelected &&
            RecordAt(grid, grid.SelectedIndex).Id == parent.Id, "메일함 폭을 끌어 조절한 뒤 폴더나 메일 선택이 달라졌습니다.");

        double widerFolderWidth = folderPane.ActualWidth;
        var keySource = PresentationSource.FromVisual(folderSplitter);
        folderSplitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keySource, Environment.TickCount, Key.Right)
            { RoutedEvent = Keyboard.KeyDownEvent });
        folderSplitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keySource, Environment.TickCount, Key.Right)
            { RoutedEvent = Keyboard.KeyUpEvent });
        await Idle();
        Require(folderPane.ActualWidth > widerFolderWidth + 8, "방향키로 메일함 폭을 늘리지 못했습니다.");
        widerFolderWidth = folderPane.ActualWidth;
        Capture(window, output, "07b-pst-resized-mailboxes.png");
        double wideWindowWidth = window.Width;
        window.Width = 950;
        await Idle();
        Require(folderPane.ActualWidth < widerFolderWidth && grid.ActualWidth >= 280 &&
            Field<Grid>(window, "MailboxReaderPane").IsVisible, "창을 줄일 때 메일 목록과 읽기 창에 필요한 폭을 확보하지 못했습니다.");
        window.Width = wideWindowWidth;
        await Idle();
        Require(Math.Abs(folderPane.ActualWidth - widerFolderWidth) < 2, "창을 넓혔을 때 조절한 메일함 폭이 복구되지 않았습니다.");

        window.Width = 780;
        window.Height = 600;
        await Idle();
        await ReturnToMailboxList(window);
        Require(Field<Button>(window, "SearchButton").IsVisible && RecordAt(grid, grid.SelectedIndex).Id == parent.Id,
            "좁은 창에서 검색이나 첨부 확인 영역이 사라졌습니다.");
        var narrowScope = Field<TextBlock>(window, "PstScopeStatus").Text;
        Require(narrowScope.IndexOf("Inbox", StringComparison.Ordinal) is >= 0 and < 55,
            "좁은 창의 적용 범위에서 선택한 폴더 이름 앞에 전체 경로가 표시됩니다.");
        Capture(window, output, "08-pst-narrow.png");
        var focusScope = FocusManager.GetFocusScope(folderToggle);
        FocusManager.SetFocusedElement(focusScope, folderToggle);
        folderToggle.Focus();
        folderToggle.IsChecked = false;
        folderToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, folderToggle));
        await Idle();
        Require(folderToggle.IsVisible && ReferenceEquals(FocusManager.GetFocusedElement(focusScope), folderToggle),
            "메일함을 접은 뒤 같은 버튼으로 다시 펼칠 수 있도록 포커스가 유지되어야 합니다.");
        Require(!tree.IsVisible && !folderSplitter.IsVisible && inbox.IsSelected && grid.Items.Count == 1 &&
            RecordAt(grid, grid.SelectedIndex).Id == parent.Id, "폴더 창을 접을 때 적용한 PST 범위나 결과가 달라졌습니다.");
        Capture(window, output, "09-pst-collapsed.png");
        tabs.SelectedItem = Field<TabItem>(window, "SourcesTab");
        await Idle();
        tabs.SelectedItem = investigation;
        await Idle();
        folderToggle.IsChecked = true;
        await Idle();
        var rootLabel = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "AllMailScopeButton");
        var inboxLabel = inbox.Header as TextBlock;
        Require(tree.IsVisible && folderSplitter.IsVisible && Math.Abs(folderPane.ActualWidth - widerFolderWidth) < 2 &&
            rootLabel is { ActualWidth: > 75 } && inboxLabel is { ActualWidth: > 42 } &&
            inbox.IsSelected && RecordAt(grid, grid.SelectedIndex).Id == parent.Id,
            "자료 탭을 다녀온 뒤 메일함을 펼칠 때 제목이나 선택이 사라집니다.");
        Capture(window, output, "12-pst-reopened-after-sources.png");

        await Search(window, SearchField.Any, "", 1);
        InFilterDialog(window, _ => Field<TextBox>(window, "FromFilterBox").Text = "cancelled@example.test", apply: false);
        Require(Field<TextBox>(window, "FromFilterBox").Text == "" && grid.Items.Count == 1,
            "필터 취소 뒤에 조건이 남거나 결과가 바뀌었습니다.");
        Require(!Field<Button>(window, "PstAllFoldersButton").IsVisible, "필터를 열기 전에 폴더 조건 명령이 보입니다.");
        InFilterDialog(window, dialog =>
        {
            Capture(dialog, output, "11-filters-open.png");
            Click(window, "PstAllFoldersButton");
        });
        await WaitFor(() => grid.Items.Count == 2 && !Field<Button>(window, "CancelWorkButton").IsEnabled,
            "한 PST의 모든 폴더 적용");
        Require(grid.Items.Cast<object>().Select(RecordFromRow).All(row => row.SourcePath == attachmentPath),
            "이 PST의 모든 폴더 명령으로 다른 원본까지 조회했습니다.");
        var sources = Field<ComboBox>(window, "SearchSourceBox");
        sources.SelectedItem = sources.Items.Cast<object>().Single(option =>
            option.GetType().GetProperty("Value")?.GetValue(option) as string == result.SourceId);
        await Idle();
        Require(!Field<MenuItem>(window, "ExportSelectedButton").IsEnabled &&
            !Field<MenuItem>(window, "ExportSearchButton").IsEnabled &&
            Field<TextBlock>(window, "PstScopeStatus").Text.Contains("미적용", StringComparison.Ordinal),
            "다른 원본을 아직 적용하지 않은 상태에서 이전 결과의 저장이 활성 상태입니다.");
        InFilterDialog(window, _ => Click(window, "PstAllSourcesButton"));
        await WaitFor(() => grid.Items.Count == 8 && !Field<Button>(window, "CancelWorkButton").IsEnabled,
            "전체 자료 범위의 명시적 적용");
        Require(originalHashes.All(item => Hash(item.Key) == item.Value), "PST 입력의 SHA-256이 달라졌습니다.");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            Checks = steps, Theme = theme, OriginalsUnchanged = true, FolderCountsVerified = true,
            DraftQueryPreserved = true, ClosedTreeScopePreserved = true, ResizedFolderWidth = widerFolderWidth,
            FileAndGlobalScopesSeparated = true, PendingSourceExportDisabled = true,
            Sources = originalHashes.ToDictionary(item => Path.GetFileName(item.Key), item => item.Value)
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();

        async Task ApplyFolder(TreeViewItem item, int expected)
        {
            item.IsSelected = true;
            await WaitFor(() => item.IsSelected && grid.Items.Count == expected &&
                !Field<Button>(window, "CancelWorkButton").IsEnabled, $"PST 폴더 적용 후 {expected}건");
        }
    }

    private static TreeViewItem FindFolder(TreeView tree, uint nid, string name, string? sourceId = null)
    {
        static IEnumerable<TreeViewItem> Walk(ItemsControl parent) =>
            parent.Items.OfType<TreeViewItem>().SelectMany(node => new[] { node }.Concat(Walk(node)));
        var item = Walk(tree).Single(node => node.DataContext is PstFolder folder &&
            folder.NodeId == nid && folder.Name == name && (sourceId is null || folder.SourceId == sourceId));
        item.BringIntoView();
        return item;
    }

    private static PstMessageLocator RequireMessage(MailRecord record, JsonElement expected, string path)
    {
        Require(record.Format == MailFormat.Pst && record.SourcePath == path &&
            record.PstMessage is { } location && location.MessageNodeId == expected.GetProperty("messageNid").GetUInt32() &&
            location.FolderNodeId == expected.GetProperty("folderNid").GetUInt32(),
            "표시된 PST 메시지의 원본, 폴더 또는 메시지 NID가 다릅니다.");
        return record.PstMessage!;
    }

    private static void RequirePstRawContext(MainWindow window, PstPropertyLocator property)
    {
        var status = Field<TextBlock>(window, "RawStatus").Text;
        var where = EvidenceFields(window, "RawLocationFields");
        Require(status.Contains("속성", StringComparison.Ordinal) && status.Contains("오프셋", StringComparison.Ordinal) &&
            where.Any(field => field.Value == property.Message.FolderNid) &&
            where.Any(field => field.Value == property.Message.MessageNid) &&
            where.Any(field => field.Value == property.TagHex),
            "PST Hex에서 파일 오프셋과 속성 위치를 구분하는 폴더·메시지·속성 근거가 보이지 않습니다.");
    }

    private static void RequirePstReturn(MainWindow window, DataGrid grid, TreeViewItem folder, string id, string query,
        int count = 1)
    {
        var focus = FocusManager.GetFocusedElement(window);
        Require(folder.IsSelected && Field<TextBox>(window, "SearchQueryBox").Text == query &&
            grid.Items.Count == count && RecordAt(grid, grid.SelectedIndex).Id == id &&
            (focus == grid || focus is DataGridRow row && row.Item == grid.SelectedItem),
            "저장 결과에서 돌아온 뒤 PST 폴더, 검색어, 선택 행 또는 논리 포커스가 달라졌습니다.");
    }

    private static void VerifyManifest(string package, string nature, PstMessageLocator expected, string sourceSha256)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package, "manifest.json")));
        var item = manifest.RootElement.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("nature").GetString() == nature);
        var location = item.GetProperty("pstProperty").GetProperty("message");
        var record = manifest.RootElement.GetProperty("records")[0];
        var source = manifest.RootElement.GetProperty("sources").EnumerateArray().Single(value =>
            value.GetProperty("id").GetString() == record.GetProperty("sourceId").GetString());
        Require(location.GetProperty("messageNodeId").GetUInt32() == expected.MessageNodeId &&
            location.GetProperty("folderNodeId").GetUInt32() == expected.FolderNodeId &&
            record.GetProperty("messageRange").ValueKind == JsonValueKind.Null &&
            source.GetProperty("sha256").GetString() == sourceSha256,
            "PST 매니페스트의 원본 해시, 논리 위치 또는 물리 범위 표현이 다릅니다.");
    }
}
