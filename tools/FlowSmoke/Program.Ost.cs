using JournalDesk;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunOst(string workspace, string output, string? theme)
    {
        var fixtures = Path.Combine(workspace, "tests", "fixtures");
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "ost-oracle.json")));
        using var pstOracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "pst-oracle.json")));
        var sample = oracle.RootElement.GetProperty("primary");
        var ostPath = Path.Combine(fixtures, "ost", sample.GetProperty("file").GetString()!);
        var secondary = oracle.RootElement.GetProperty("secondary");
        var secondaryFixture = Path.Combine(fixtures, "ost", secondary.GetProperty("file").GetString()!);
        var secondPath = Path.Combine(output, "second-collection", Path.GetFileName(ostPath));
        Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
        File.Copy(secondaryFixture, secondPath);
        var pstSample = pstOracle.RootElement.GetProperty("tika");
        var pstPath = Path.Combine(workspace, "tests", "fixtures", "external", pstSample.GetProperty("file").GetString()!);
        var originalHashes = new[] { ostPath, secondPath, pstPath }.ToDictionary(path => path, Hash);
        Require(originalHashes[ostPath] == sample.GetProperty("sha256").GetString() &&
            originalHashes[secondPath] == secondary.GetProperty("sha256").GetString() &&
            originalHashes[pstPath] == pstSample.GetProperty("sha256").GetString(), "입력이 독립 비교에 사용한 원본과 다릅니다.");

        const string query = "single PDF";
        const string attachmentName = "Clinometer Usage.pdf";
        var allMessages = sample.GetProperty("messages").EnumerateArray().ToArray();
        var expected = allMessages.Single(message => message.GetProperty("subject").GetString() == query);
        var folderNid = expected.GetProperty("folderNid").GetUInt32();
        var inboxCount = allMessages.Count(message => message.GetProperty("folderNid").GetUInt32() == folderNid);
        var secondaryMessages = secondary.GetProperty("messages").EnumerateArray().ToArray();
        var window = new MainWindow([ostPath, secondPath, pstPath]) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        if (theme is not null)
        {
            ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
            await Idle();
        }
        Click(window, "AnalyzeButton");
        var grid = Field<DataGrid>(window, "ResultsGrid");
        var details = Field<TabControl>(window, "DetailTabs");
        var tree = Field<TreeView>(window, "PstFolderTree");
        var scope = Field<TextBlock>(window, "PstScopeStatus");
        var folderToggle = Field<ToggleButton>(window, "PstFoldersToggle");
        var queryBox = Field<TextBox>(window, "SearchQueryBox");
        var raw = Field<HexEvidenceView>(window, "RawBytesBox");
        var attachmentGrid = Field<DataGrid>(window, "AttachmentsGrid");
        var busy = Field<Button>(window, "CancelWorkButton");
        var allCount = allMessages.Length + secondaryMessages.Length + pstSample.GetProperty("messages").GetArrayLength();
        await WaitFor(() => Field<TabControl>(window, "WorkspaceTabs").SelectedItem == Field<TabItem>(window, "InvestigationTab") &&
            grid.Items.Count == allCount && !busy.IsEnabled, "OST와 PST 동시 개방");
        double menuHeightDip = Field<Menu>(window, "MainMenu").ActualHeight;
        Require(Math.Abs(menuHeightDip - 25) <= 1, $"메뉴 높이가 NTFS Tracer의 25 DIP 기준에 맞지 않습니다. 측정 {menuHeightDip:F2} DIP");
        Require(grid.Items.Cast<object>().Select(RecordFromRow).Select(record => record.Format).ToHashSet()
            .SetEquals([MailFormat.Ost, MailFormat.Pst]), "OST와 PST가 서로 다른 자료 종류로 표시되지 않습니다.");
        var initialRows = grid.Items.Cast<object>().Select(RecordFromRow).ToArray();
        var primaryId = initialRows.First(record => record.SourcePath == ostPath).SourceId;
        var secondaryId = initialRows.First(record => record.SourcePath == secondPath).SourceId;
        Require(primaryId != secondaryId, "이름이 같은 OST 두 개를 하나의 자료로 합쳤습니다.");
        var csvPackage = await Export(window, "ExportSearchButton", output, ExportKind.Csv, "00-mixed-csv.png");
        var csv = File.ReadAllText(Directory.GetFiles(csvPackage, "*.csv").Single());
        Require(csv.Contains("StoreMessageNid", StringComparison.Ordinal) &&
            csv.Contains(",\"Ost\",", StringComparison.Ordinal) && csv.Contains(",\"Pst\",", StringComparison.Ordinal),
            "혼합 CSV에 자료 유형과 메시지 NID가 빠졌습니다.");

        if (folderToggle.IsChecked != true) folderToggle.IsChecked = true;
        await Idle();
        Require(tree.IsVisible, "OST 폴더 창이 열리지 않습니다.");
        var sourceRoots = tree.Items.OfType<TreeViewItem>().Where(node =>
            node.ToolTip?.ToString() == ostPath || node.ToolTip?.ToString() == secondPath).ToArray();
        static string SourceLabel(TreeViewItem node) => node.Header is TextBlock label
            ? new System.Windows.Documents.TextRange(label.ContentStart, label.ContentEnd).Text
            : node.Header?.ToString() ?? "";
        Require(sourceRoots.Length == 2 && sourceRoots.Select(SourceLabel).Distinct().Count() == 2,
            "같은 파일명으로 열린 OST 두 개를 트리의 원본 행에서 구별할 수 없습니다. " +
            string.Join(" | ", tree.Items.OfType<TreeViewItem>().Select(node => $"{SourceLabel(node)} · {node.ToolTip}")));
        var parent = FindFolder(tree, 8514, "IPM_SUBTREE", primaryId);
        var inbox = FindFolder(tree, folderNid, "Inbox", primaryId);
        var parentFolder = (PstFolder)parent.DataContext;
        var inboxFolder = (PstFolder)inbox.DataContext;
        Require(inboxFolder.ParentNodeId == parentFolder.NodeId && parentFolder.SourceId == inboxFolder.SourceId,
            "OST Inbox의 상위 폴더 또는 자료 식별자가 다릅니다.");
        int parentDirect = allMessages.Count(message => message.GetProperty("folderNid").GetUInt32() == 8514);
        var expectedParents = sample.GetProperty("folders").EnumerateArray()
            .Where(folder => folder.TryGetProperty("parentNid", out var parentNid) && parentNid.ValueKind == JsonValueKind.Number)
            .ToLookup(folder => folder.GetProperty("parentNid").GetUInt32(), folder => folder.GetProperty("nid").GetUInt32());
        var descendantIds = new HashSet<uint> { parentFolder.NodeId };
        var pendingFolders = new Queue<uint>();
        pendingFolders.Enqueue(parentFolder.NodeId);
        while (pendingFolders.TryDequeue(out uint ancestor))
            foreach (uint child in expectedParents[ancestor])
                if (descendantIds.Add(child)) pendingFolders.Enqueue(child);
        int parentTotal = allMessages.Count(message => descendantIds.Contains(message.GetProperty("folderNid").GetUInt32()));
        int parentCount = parentDirect > 0 ? parentDirect : parentTotal;
        await ApplyFolder(parent, parentCount);
        Require(MainWindow.GetMailboxCount(parent) == $"[{parentCount:N0}]", "OST 폴더 수가 기본 조회 범위와 다릅니다.");
        queryBox.Text = query;
        await ApplyFolder(inbox, inboxCount);
        Require(queryBox.Text == query &&
            Field<TextBlock>(window, "SearchSummary").Text.Contains("미적용", StringComparison.Ordinal),
            "폴더를 변경하면서 미적용 검색 조건을 잃었습니다.");
        Require(scope.Text.Contains("Inbox", StringComparison.Ordinal) &&
            grid.Items.Cast<object>().Select(RecordFromRow).All(record => record.SourcePath == ostPath),
            "OST Inbox에서 PST 결과가 섞였거나 적용 폴더가 보이지 않습니다.");
        Capture(window, output, "01-ost-folders.png");

        await Search(window, SearchField.Subject, query, 1);
        grid.SelectedIndex = 0;
        var result = RecordAt(grid, 0);
        Require(result.Format == MailFormat.Ost && result.SourcePath == ostPath && result.PstMessage is { } location &&
            location.FolderNodeId == folderNid && location.MessageNodeId == expected.GetProperty("messageNid").GetUInt32(),
            "제목 검색 결과가 독립 판독에서 확인한 OST 메시지와 다릅니다.");
        var headerOracle = expected.GetProperty("header");
        var header = result.TransportHeaderProperty ?? throw new InvalidOperationException("OST 전송 헤더 속성이 없습니다.");
        Require(result.TransportHeaderState == HeaderEvidenceState.Present &&
            header.Message == result.PstMessage && header.PropertyTag == headerOracle.GetProperty("tag").GetUInt32() &&
            header.Length == headerOracle.GetProperty("length").GetInt64(), "OST 전송 헤더의 태그, 메시지 위치 또는 길이가 다릅니다.");
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        var received = result.Headers.Where(value => value.Name.Equals("Received", StringComparison.OrdinalIgnoreCase)).ToArray();
        var headerFields = headerOracle.GetProperty("fields");
        var expectedReceived = headerFields.EnumerateArray().Where(field =>
            field.GetProperty("name").GetString()!.Equals("Received", StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(received.Length == expectedReceived.Length && result.Headers.Count == headerFields.GetArrayLength() &&
            RawHeaderNames(window).Length == result.Headers.Count, "OST 전송 헤더 일부가 화면에서 빠졌습니다.");
        var firstSelection = SelectRawHeader(window, result, received[0]);
        Click(window, "RawHeaderBytesButton");
        await WaitFor(() => details.SelectedItem == Field<TabItem>(window, "RawTab") && raw.Text.Length > 0, "OST 전송 헤더 속성 Hex");
        RequirePstRawContext(window, header);
        Require(EvidenceFields(window, "RawLocationFields").Any(field => field.Value == ostPath) &&
            EvidenceFields(window, "RawLocationFields").Any(field => field.Value.Equals(originalHashes[ostPath], StringComparison.OrdinalIgnoreCase)),
            "원본 보기에서 OST 자료명과 해시를 확인할 수 없습니다.");
        var rawHeader = DisplayedBytes(raw.Text, received[0].Range.Offset);
        Require(received[0].Range.Offset == expectedReceived[0].GetProperty("offset").GetInt64() &&
            rawHeader.Length == expectedReceived[0].GetProperty("length").GetInt64() &&
            ByteHash(rawHeader) == expectedReceived[0].GetProperty("sha256").GetString(),
            "화면에 표시한 Received가 독립 판독 바이트와 다릅니다.");
        Capture(window, output, "02-ost-transport-property.png");
        ClickMenu(window, "EnglishLanguageMenuItem");
        await Idle();
        Require(Field<TextBlock>(window, "RawStatus").Text.Contains("Property", StringComparison.OrdinalIgnoreCase) &&
            details.SelectedItem == Field<TabItem>(window, "RawTab") && inbox.IsSelected &&
            RecordAt(grid, grid.SelectedIndex).Id == result.Id && queryBox.Text == query &&
            DisplayedBytes(raw.Text, received[0].Range.Offset).SequenceEqual(rawHeader),
            "표시 언어를 바꾸면서 검색 위치, 원본 근거 또는 속성 오프셋이 달라졌습니다.");
        Capture(window, output, "12-ost-english-transport.png");
        ClickMenu(window, "KoreanLanguageMenuItem");
        await Idle();
        PressEscape(window);
        await Idle();
        Require(details.SelectedItem == Field<TabItem>(window, "HeadersTab") && Field<TextBox>(window, "RawHeaderText").SelectedText == firstSelection,
            "전송 헤더 원본 확인을 닫았을 때 선택한 행이 바뀌었습니다.");
        var headerPackage = await Export(window, "ExportSearchButton", output, ExportKind.OstEvidence,
            "03-ost-message-verification.png");
        var headerSaved = Directory.GetFiles(headerPackage, "*-transport-header.bin").Single();
        Require(Hash(headerSaved) == headerOracle.GetProperty("sha256").GetString(),
            "내보낸 OST 전송 헤더가 독립 판독 해시와 다릅니다.");
        VerifyManifest(headerPackage, "ost-transport-header-property", result.PstMessage!, originalHashes[ostPath]);
        RequirePstReturn(window, grid, inbox, result.Id, query);
        var openFromSearch = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(openFromSearch);
        Require(openFromSearch.Handled && details.SelectedItem == Field<TabItem>(window, "HeadersTab") &&
            RawHeaderNames(window).SequenceEqual(result.Headers.Select(header => header.Name), StringComparer.OrdinalIgnoreCase),
            "검색으로 돌아온 뒤 Enter를 눌렀을 때 같은 메일의 헤더가 열리지 않았습니다.");

        await Search(window, SearchField.AttachmentName, attachmentName, 1);
        grid.SelectedIndex = 0;
        Require(RecordAt(grid, 0).Id == result.Id, "첨부명 검색이 해당 OST 메시지를 찾지 못했습니다.");
        var attachmentOracle = expected.GetProperty("attachments").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == attachmentName);
        var propertyOracle = attachmentOracle.GetProperty("property");
        var attachment = result.Attachments.Single(item => item.Name == attachmentName);
        var attachmentProperty = attachment.PstProperty ?? throw new InvalidOperationException("OST 첨부 속성이 없습니다.");
        Require(attachmentProperty.AttachmentNodeId == attachmentOracle.GetProperty("attachmentNid").GetUInt32() &&
            attachmentProperty.PropertyTag == propertyOracle.GetProperty("tag").GetUInt32() &&
            attachmentProperty.Length == propertyOracle.GetProperty("length").GetInt64(),
            "첨부의 NID, 태그 또는 길이가 독립 판독 결과와 다릅니다.");
        details.SelectedItem = Field<TabItem>(window, "AttachmentsTab");
        await Idle();
        attachmentGrid.SelectedIndex = Array.IndexOf(result.Attachments.ToArray(), attachment);
        Capture(window, output, "04-ost-attachments.png");
        Click(window, "OpenAttachmentBytesButton");
        await WaitFor(() => details.SelectedItem == Field<TabItem>(window, "RawTab") &&
            raw.Text.Contains("%PDF-", StringComparison.Ordinal), "OST PDF 첨부 속성 Hex");
        RequirePstRawContext(window, attachmentProperty);
        var rawAttachment = DisplayedBytes(raw.Text, 0);
        var firstRange = propertyOracle.GetProperty("ranges")[0];
        Require(rawAttachment.Length >= 512 &&
            ByteHash(rawAttachment.AsSpan(0, firstRange.GetProperty("length").GetInt32())) == firstRange.GetProperty("sha256").GetString(),
            "첨부 Hex의 첫 페이지가 독립 판독 바이트와 다릅니다.");
        Capture(window, output, "05-ost-attachment-property.png");
        PressEscape(window);
        await Idle();
        Require(details.SelectedItem == Field<TabItem>(window, "AttachmentsTab") &&
            attachmentGrid.SelectedItem is MailAttachment selected && selected.Id == attachment.Id,
            "첨부 원본 보기에서 돌아온 뒤 같은 첨부가 선택되지 않았습니다.");
        var attachmentPackage = await Export(window, "ExportAttachmentButton", output,
            captureName: "06-ost-attachment-verification.png");
        var attachmentSaved = Directory.GetFiles(attachmentPackage, "*.pdf").Single();
        Require(Hash(attachmentSaved) == propertyOracle.GetProperty("sha256").GetString() &&
            rawAttachment.SequenceEqual(File.ReadAllBytes(attachmentSaved).AsSpan(0, rawAttachment.Length).ToArray()),
            "내보낸 OST 첨부의 해시 또는 화면에서 대조한 첨부 바이트가 다릅니다.");
        VerifyManifest(attachmentPackage, "ost-attachment-property", result.PstMessage!, originalHashes[ostPath]);
        RequirePstReturn(window, grid, inbox, result.Id, attachmentName);
        Capture(window, output, "07-ost-search-return.png");

        window.Width = 780;
        window.Height = 600;
        await Idle();
        await ReturnToMailboxList(window);
        Require(Field<Button>(window, "SearchButton").IsVisible && RecordAt(grid, grid.SelectedIndex).Id == result.Id &&
            scope.Text.IndexOf("Inbox", StringComparison.Ordinal) is >= 0 and < 55,
            "좁은 창에서 검색, 첨부 또는 선택 폴더를 확인할 수 없습니다.");
        Capture(window, output, "08-ost-narrow.png");
        folderToggle.IsChecked = false;
        await Idle();
        Require(!tree.IsVisible && inbox.IsSelected && RecordAt(grid, grid.SelectedIndex).Id == result.Id,
            "폴더 창을 접었을 때 적용 범위나 선택 행이 달라졌습니다.");
        Capture(window, output, "09-ost-collapsed.png");

        await Search(window, SearchField.Subject, "Welcome to your new Outlook.com account", 1);
        grid.SelectedIndex = 0;
        var noHeader = RecordAt(grid, 0);
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        Require(noHeader.TransportHeaderState == HeaderEvidenceState.Missing,
            "원본 전송 헤더가 없는 OST 시험 메일을 찾지 못했습니다.");
        var openMissingHeader = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(openMissingHeader);
        await Idle();
        var emptyHeader = Field<TextBlock>(window, "RawHeaderEmpty");
        var headerLocation = Field<TextBlock>(window, "RawHeaderStatus");
        Require(Field<FrameworkElement>(window, "MailboxReaderPane").IsVisible &&
            emptyHeader.IsVisible && emptyHeader.Text.Contains("없", StringComparison.Ordinal) &&
            !headerLocation.IsVisible && headerLocation.Text == "" &&
            !Field<Button>(window, "RawHeaderBytesButton").IsEnabled,
            "좁은 화면에서 헤더가 없는 메일에 이전 헤더 위치 또는 원본 보기 동작이 남았습니다.");
        Capture(window, output, "10-ost-missing-header.png");
        await ReturnToMailboxList(window);
        await Search(window, SearchField.Any, "", inboxCount);
        Require(!Field<Button>(window, "PstAllFoldersButton").IsVisible, "필터를 열기 전에 폴더 조건 명령이 보입니다.");
        InFilterDialog(window, dialog =>
        {
            Click(window, "PstAllFoldersButton");
        });
        await WaitFor(() => grid.Items.Count == allMessages.Length && !busy.IsEnabled, "이 OST의 모든 폴더");
        Require(grid.Items.Cast<object>().Select(RecordFromRow).All(record => record.SourcePath == ostPath),
            "이 OST의 모든 폴더에 다른 원본이 포함되었습니다.");
        InFilterDialog(window, _ => Click(window, "PstAllSourcesButton"));
        await WaitFor(() => grid.Items.Count == allCount && !busy.IsEnabled, "PST·OST 전체 자료");
        folderToggle.IsChecked = true;
        await Idle();
        var secondInbox = FindFolder(tree, folderNid, "Inbox", secondaryId);
        await ApplyFolder(secondInbox, secondaryMessages.Count(message => message.GetProperty("folderNid").GetUInt32() == folderNid));
        await Search(window, SearchField.Subject, "Get started with OneDrive", 1);
        grid.SelectedIndex = 0;
        var sameNid = RecordAt(grid, 0);
        Require(sameNid.PstMessage?.MessageNodeId == result.PstMessage!.MessageNodeId &&
            sameNid.PstMessage.FolderNodeId == result.PstMessage.FolderNodeId &&
            sameNid.Id != result.Id && sameNid.SourcePath == secondPath,
            "원본의 이름과 메시지·폴더 NID가 모두 같을 때 다른 OST의 결과를 열었습니다.");
        var sourceBox = Field<ComboBox>(window, "SearchSourceBox");
        sourceBox.SelectedItem = sourceBox.Items.Cast<object>().Single(option =>
            option.GetType().GetProperty("Value")?.GetValue(option) as string == primaryId);
        await Idle();
        var accessibleScope = AutomationProperties.GetName(scope);
        Require(!Field<MenuItem>(window, "ExportSelectedButton").IsEnabled &&
            !Field<MenuItem>(window, "ExportSearchButton").IsEnabled &&
            !Field<MenuItem>(window, "ExportAttachmentButton").IsEnabled &&
            scope.Focusable &&
            scope.Text.StartsWith("범위 미적용", StringComparison.Ordinal) &&
            accessibleScope.Contains(ostPath, StringComparison.OrdinalIgnoreCase) &&
            accessibleScope.Contains(secondPath, StringComparison.OrdinalIgnoreCase),
            "미적용 자료를 바꾼 상태에서 적용 범위를 구별할 수 없거나 이전 결과의 저장이 활성화되어 있습니다.");
        Capture(window, output, "11-ost-same-name-pending.png");
        Require(originalHashes.All(item => Hash(item.Key) == item.Value), "원본 자료의 SHA-256이 바뀌었습니다.");
        Require(Hash(secondaryFixture) == originalHashes[secondPath], "동명 검증에 사용한 두 번째 OST가 원본과 다릅니다.");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            Theme = theme, MenuHeightDip = menuHeightDip, InboxCount = inboxCount, AllCount = allCount, result.PstMessage,
            Header = new { header.PropertyTag, Bytes = header.Length, Received = received.Length, Sha256 = Hash(headerSaved), Package = headerPackage },
            Attachment = new { attachment.Name, attachmentProperty.AttachmentNodeId, attachmentProperty.PropertyTag,
                Bytes = attachmentProperty.Length, Sha256 = Hash(attachmentSaved), Package = attachmentPackage },
            MixedCsv = csvPackage, SearchReturnVerified = true, OriginalsUnchanged = true,
            IdenticalNameAndNidsSeparated = true, PendingSourceExportDisabled = true, LanguageSwitchPreservedEvidence = true,
            Sources = originalHashes
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();

        async Task ApplyFolder(TreeViewItem item, int count)
        {
            item.IsSelected = true;
            await WaitFor(() => item.IsSelected && grid.Items.Count == count && !busy.IsEnabled, $"OST 폴더 {count}건");
        }
    }

    private static string ByteHash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
