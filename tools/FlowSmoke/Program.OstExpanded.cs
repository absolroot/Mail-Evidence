using JournalDesk;
using System.IO;
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
    private static async Task RunExpandedOst(string workspace, string output, string? theme, bool useVersion21)
    {
        var fixtures = Path.Combine(workspace, "Mail-Viewer", "tests", "fixtures");
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "ost-expanded-oracle.json")));
        var sample = oracle.RootElement.GetProperty("samples").EnumerateArray()
            .Single(value => value.GetProperty("file").GetString() == "SampleOstFile.ost");
        var input = useVersion21 ? oracle.RootElement.GetProperty("controls").EnumerateArray()
            .Single(value => value.GetProperty("file").GetString() == "generated-aspose-v21.ost") : sample;
        int version = useVersion21 ? 21 : 23;
        var path = Path.Combine(fixtures, "ost", input.GetProperty("file").GetString()!);
        var originalHash = Hash(path);
        Require(originalHash == input.GetProperty("sha256").GetString() &&
            input.GetProperty("version").GetInt32() == version && input.GetProperty("encoding").GetInt32() == 1,
            "출처가 고정된 구형 치환 인코딩 OST가 아닙니다.");

        const string query = "newsletter copy";
        const string attachmentName = "image001.jpg";
        var messages = sample.GetProperty("messages").EnumerateArray().ToArray();
        var expected = messages.Single(message => message.GetProperty("messageNid").GetUInt32() == 0x214D84 &&
            message.GetProperty("subject").GetString() == query);
        var folderNid = expected.GetProperty("folderNid").GetUInt32();
        var expectedInboxCount = messages.Count(message => message.GetProperty("folderNid").GetUInt32() == folderNid);
        var expectedMatches = messages.Where(message => message.GetProperty("folderNid").GetUInt32() == folderNid &&
            message.GetProperty("subject").GetString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Select(message => message.GetProperty("messageNid").GetUInt32()).ToHashSet();
        var window = new MainWindow([path]) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        if (theme is not null)
        {
            ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
            await Idle();
        }
        Click(window, "AnalyzeButton");
        var workspaceTabs = Field<TabControl>(window, "WorkspaceTabs");
        var investigation = Field<TabItem>(window, "InvestigationTab");
        var grid = Field<DataGrid>(window, "ResultsGrid");
        var details = Field<TabControl>(window, "DetailTabs");
        var tree = Field<TreeView>(window, "PstFolderTree");
        var toggle = Field<ToggleButton>(window, "PstFoldersToggle");
        var raw = Field<HexEvidenceView>(window, "RawBytesBox");
        var rawStatus = Field<TextBlock>(window, "RawStatus");
        var busy = Field<Button>(window, "CancelWorkButton");
        await WaitFor(() => workspaceTabs.SelectedItem == investigation && !busy.IsEnabled &&
            grid.Items.Count == messages.Length, "구형 치환 인코딩 OST 개방");

        workspaceTabs.SelectedItem = Field<TabItem>(window, "SourcesTab");
        await Idle();
        var report = Field<TextBox>(window, "SourcesReportBox").Text;
        Require(report.Contains($"Unicode v{version}", StringComparison.Ordinal) &&
            report.Contains("512 B", StringComparison.Ordinal) && report.Contains("0x01", StringComparison.Ordinal) &&
            report.Contains(originalHash, StringComparison.OrdinalIgnoreCase) &&
            !report.Contains("판독 범위 제한", StringComparison.Ordinal),
            "OST 레이아웃과 SHA-256이 표시되고 정상 첨부 개체는 원본 판독 제한으로 집계되지 않아야 합니다.");
        Capture(window, output, "01-ost-layout-and-coverage.png");
        workspaceTabs.SelectedItem = investigation;
        await Idle();
        toggle.IsChecked = true;
        await Idle();
        Require(tree.IsVisible, "구형 OST 폴더를 볼 수 없습니다.");
        var expectedFolder = sample.GetProperty("folders").EnumerateArray().Single(folder =>
            folder.GetProperty("nid").GetUInt32() == folderNid);
        var inbox = FindFolder(tree, folderNid, expectedFolder.GetProperty("name").GetString()!);
        Require(inbox.DataContext is PstFolder
            {
                ParentNodeId: { } parent
            } && parent == expectedFolder.GetProperty("parentNid").GetUInt32(), "Inbox의 상위 폴더 NID가 다릅니다.");
        inbox.IsSelected = true;
        await WaitFor(() => inbox.IsSelected && !busy.IsEnabled && grid.Items.Count == expectedInboxCount,
            "구형 OST의 Inbox 직접 범위");
        Capture(window, output, "02-ost-folders.png");

        await Search(window, SearchField.Subject, query, expectedMatches.Count);
        var rows = grid.Items.Cast<object>().ToArray();
        Require(rows.Select(RecordFromRow).Select(record => record.PstMessage?.MessageNodeId ?? 0)
            .ToHashSet().SetEquals(expectedMatches), "제목 검색 결과가 독립 판독과 다릅니다.");
        grid.SelectedItem = rows.Single(row => RecordFromRow(row).PstMessage?.MessageNodeId ==
            expected.GetProperty("messageNid").GetUInt32());
        var message = RecordAt(grid, grid.SelectedIndex);
        var messageLocation = message.PstMessage ?? throw new InvalidOperationException("OST 메시지 위치가 없습니다.");
        Require(message.Format == MailFormat.Ost && message.SourcePath == path && messageLocation.FolderNodeId == folderNid &&
            messageLocation.MessageNodeId == expected.GetProperty("messageNid").GetUInt32(),
            "Inbox의 제목 검색이 독립 판독에서 확인한 OST 메일을 찾지 못했습니다.");

        var expectedHeader = expected.GetProperty("header");
        var expectedRows = expectedHeader.GetProperty("fields").EnumerateArray().ToArray();
        var header = message.TransportHeaderProperty ?? throw new InvalidOperationException("원본 전송 헤더 속성이 없습니다.");
        Require(message.TransportHeaderState == HeaderEvidenceState.Present && header.Message == messageLocation &&
            header.Length == expectedHeader.GetProperty("length").GetInt64() &&
            header.PropertyTag == expectedHeader.GetProperty("tag").GetUInt32(),
            "치환 인코딩에서 읽은 헤더의 길이, 태그 또는 NID가 독립 판독과 다릅니다.");
        var headerTab = Field<TabItem>(window, "HeadersTab");
        details.SelectedItem = headerTab;
        await Idle();
        Require(RawHeaderNames(window).Length == expectedRows.Length && message.Headers.Select(row => row.Name)
            .SequenceEqual(expectedRows.Select(row => row.GetProperty("name").GetString())), "헤더 화면의 필드 순서나 수가 다릅니다.");
        var index = Array.FindIndex(expectedRows, row => row.GetProperty("name").GetString() == "Received");
        Require(index >= 0, "독립 판독에 Received 행이 없습니다.");
        var firstSelection = SelectRawHeader(window, message, message.Headers[index]);
        Click(window, "RawHeaderBytesButton");
        await WaitFor(() => details.SelectedItem == Field<TabItem>(window, "RawTab") && raw.Text.Length > 0,
            "구형 OST 전송 헤더 속성 Hex");
        RequirePstRawContext(window, header);
        Require(rawStatus.Text.Contains("블록 디코딩 후", StringComparison.Ordinal),
            "치환 인코딩 OST의 원본 보기에서 속성값을 어떤 기준으로 표시하는지 알 수 없습니다.");
        var expectedRow = expectedRows[index];
        var headerOffset = expectedRow.GetProperty("offset").GetInt64();
        var visibleHeader = DisplayedBytes(raw.Text, headerOffset);
        Require(visibleHeader.Length == expectedRow.GetProperty("length").GetInt32() &&
            ByteHash(visibleHeader) == expectedRow.GetProperty("sha256").GetString(),
            "구형 OST Hex의 Received 필드가 독립 판독과 다릅니다.");
        Capture(window, output, "03-ost-header-property.png");
        ClickMenu(window, "EnglishLanguageMenuItem");
        await Idle();
        Require(rawStatus.Text.Contains("after block decoding", StringComparison.Ordinal) &&
            DisplayedBytes(raw.Text, headerOffset).SequenceEqual(visibleHeader) && inbox.IsSelected &&
            RecordAt(grid, grid.SelectedIndex).Id == message.Id, "영어 화면으로 전환하면서 원본 근거 또는 선택을 잃었습니다.");
        Capture(window, output, "08-ost-english-decoded-property.png");
        ClickMenu(window, "KoreanLanguageMenuItem");
        await Idle();
        PressEscape(window);
        await Idle();
        Require(details.SelectedItem == headerTab && Field<TextBox>(window, "RawHeaderText").SelectedText == firstSelection, "원본에서 같은 헤더 행으로 돌아오지 않았습니다.");

        var evidence = await Export(window, "ExportSelectedButton", output, ExportKind.OstEvidence, "04-ost-evidence-verified.png");
        var headerFile = Directory.GetFiles(evidence, "*-transport-header.bin").Single();
        Require(Hash(headerFile) == expectedHeader.GetProperty("sha256").GetString(),
            "저장한 구형 OST 전송 헤더가 독립 판독 해시와 다릅니다.");
        VerifyManifest(evidence, "ost-transport-header-property", messageLocation, originalHash);
        RequirePstReturn(window, grid, inbox, message.Id, query, expectedMatches.Count);

        var expectedAttachment = expected.GetProperty("attachments").EnumerateArray().Single(item =>
            item.GetProperty("attachmentNid").GetUInt32() == 0x82A5 && item.GetProperty("name").GetString() == attachmentName);
        var property = expectedAttachment.GetProperty("property");
        var attachment = message.Attachments.Single(item => item.PstProperty?.AttachmentNodeId == 0x82A5);
        var storage = attachment.PstProperty ?? throw new InvalidOperationException("원본 첨부 속성이 없습니다.");
        Require(attachment.Name == attachmentName && storage.PropertyTag == property.GetProperty("tag").GetUInt32() &&
            storage.Length == property.GetProperty("length").GetInt64(), "구형 OST 첨부의 태그, 길이 또는 이름이 다릅니다.");
        var attachmentTab = Field<TabItem>(window, "AttachmentsTab");
        var attachmentGrid = Field<DataGrid>(window, "AttachmentsGrid");
        details.SelectedItem = attachmentTab;
        await Idle();
        attachmentGrid.SelectedIndex = Array.IndexOf(message.Attachments.ToArray(), attachment);
        Click(window, "OpenAttachmentBytesButton");
        await WaitFor(() => details.SelectedItem == Field<TabItem>(window, "RawTab") && raw.Text.Length > 0,
            "구형 OST 첨부 속성 Hex");
        RequirePstRawContext(window, storage);
        var first = property.GetProperty("ranges")[0];
        var visibleAttachment = DisplayedBytes(raw.Text, 0);
        Require(visibleAttachment.Length >= 512 && ByteHash(visibleAttachment.AsSpan(0, first.GetProperty("length").GetInt32())) ==
            first.GetProperty("sha256").GetString(), "치환 인코딩 OST의 첨부 Hex가 독립 판독과 다릅니다.");
        Capture(window, output, "05-ost-attachment-property.png");
        PressEscape(window);
        await Idle();
        Require(details.SelectedItem == attachmentTab && (attachmentGrid.SelectedItem as MailAttachment)?.Id == attachment.Id,
            "원본에서 같은 첨부로 돌아오지 않았습니다.");
        var attachmentEvidence = await Export(window, "ExportAttachmentButton", output, captureName: "06-ost-attachment-verified.png");
        var attachmentFile = Directory.GetFiles(attachmentEvidence, "*.jpg").Single();
        Require(Hash(attachmentFile) == property.GetProperty("sha256").GetString() &&
            File.ReadAllBytes(attachmentFile).AsSpan(0, visibleAttachment.Length).SequenceEqual(visibleAttachment),
            "저장한 첨부가 독립 판독 해시 또는 화면에서 확인한 값과 다릅니다.");
        VerifyManifest(attachmentEvidence, "ost-attachment-property", messageLocation, originalHash);
        RequirePstReturn(window, grid, inbox, message.Id, query, expectedMatches.Count);
        foreach (string package in new[] { evidence, attachmentEvidence })
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package, "manifest.json")));
            var layout = manifest.RootElement.GetProperty("sources")[0].GetProperty("storeLayout");
            Require(layout.GetProperty("version").GetInt32() == version && layout.GetProperty("pageSize").GetInt32() == 512 &&
                layout.GetProperty("blockEncoding").GetInt32() == 1, "내보낸 매니페스트에 OST 입력 레이아웃이 없습니다.");
        }
        var reopen = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(reopen);
        Require(reopen.Handled && RecordAt(grid, grid.SelectedIndex).Id == message.Id &&
            details.SelectedItem is TabItem selected && (selected == headerTab || selected == attachmentTab),
            "검색으로 돌아온 뒤 Enter로 같은 대표 메일의 근거를 확인할 수 없습니다.");
        window.Width = 780;
        window.Height = 600;
        await Idle();
        await ReturnToMailboxList(window);
        Require(Field<Button>(window, "SearchButton").IsVisible && inbox.IsSelected &&
            RecordAt(grid, grid.SelectedIndex).Id == message.Id, "좁은 창에서 조사 위치가 달라졌습니다.");
        Capture(window, output, "07-ost-return.png");
        Require(Hash(path) == originalHash, "판독 이후 구형 OST 원본의 SHA-256이 바뀌었습니다.");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            Theme = theme, Source = path, SourceSha256 = originalHash, Version = version, PageSize = 512, BlockEncoding = 1,
            Messages = messages.Length, InboxCount = expectedInboxCount, SearchHits = expectedMatches.Count, Message = messageLocation,
            Header = new { header.PropertyTag, header.Length, Rows = expectedRows.Length, Sha256 = Hash(headerFile), Package = evidence },
            Attachment = new { attachment.Name, storage.AttachmentNodeId, storage.PropertyTag,
                storage.Length, Sha256 = Hash(attachmentFile), Package = attachmentEvidence },
            SearchReturnVerified = true, OriginalUnchanged = true, OpenedWithoutPartialReadWarning = true,
            LanguageSwitchPreservedEvidence = true
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }
}
