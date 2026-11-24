using JournalDesk;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private const string TikaAttachmentHash = "710b0ea810c12c93cae9982b60ca333e5302e72b8948b194f611513d4737c97d";
    private static int exitCode = 1;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length is < 2 or > 4)
        {
            Console.Error.WriteLine("FlowSmoke <Mail Evidence checkout> <new output folder> [--light|--dark] [--msg|--pst|--ost|--ost-expanded|--investigation|--mailbox-folders|--mailbox-marks|--reader-attachments]");
            return 2;
        }
        var workspace = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        var options = args[2..];
        var theme = options.FirstOrDefault(option => option is "--light" or "--dark");
        Directory.CreateDirectory(output);
        var app = new MailViewer.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                if (options.Contains("--investigation"))
                {
                    await RunInvestigation(output, theme);
                    await RunAllFindings(workspace, output, theme);
                    await RunConversationHistory(workspace, output, theme);
                }
                else if (options.Contains("--ost-expanded"))
                    await RunExpandedOst(workspace, output, theme);
                else if (options.Contains("--ost"))
                    await RunOst(workspace, output, theme);
                else if (options.Contains("--pst"))
                    await RunPst(workspace, output, theme);
                else if (options.Contains("--msg"))
                    await RunMsg(workspace, output, theme);
                else if (options.Contains("--mailbox-folders"))
                    await RunMailboxFolders(output, theme);
                else if (options.Contains("--mailbox-marks"))
                    await RunMailboxMarks(output, theme);
                else if (options.Contains("--mail-fields"))
                    await RunMailFields(workspace, output, theme);
                else if (options.Contains("--attachment-boundaries"))
                    await RunAttachmentBoundaries(workspace, output, theme);
                else if (options.Contains("--reader-attachments"))
                    await RunReaderAttachments(workspace, output, theme);
                else if (options.Contains("--disk-image"))
                    await RunDiskImage(workspace, output, theme);
                else if (options.Contains("--find-sources"))
                    await RunFindSources(workspace, output, theme);
                else if (options.Contains("--startup-focus"))
                    await RunStartupFocus(workspace, output, theme);
                else
                    await Run(workspace, output, theme);
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
            }
            finally
            {
                app.Shutdown();
                app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
            }
        }));
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task Run(string workspace, string output, string? theme)
    {
        var eml = Path.Combine(workspace, "tests", "fixtures", "external",
            "tika-multipart-attachments.eml");
        var synthetic = Path.Combine(workspace, "tests", "fixtures", "synthetic", "synthetic.mbox");
        var archive = Path.Combine(workspace, "tests", "fixtures", "external", "public-apache-tika.mbox");
        var inputs = new[] { eml, synthetic, archive };
        var originalHashes = inputs.ToDictionary(path => path, Hash);
        var log = new List<object>();
        var window = new MainWindow(inputs) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        if (theme is not null)
        {
            ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
            await Idle();
        }

        var panel = Field<TabItem>(window, "InvestigationTab");
        var tabs = Field<TabControl>(window, "WorkspaceTabs");
        var settings = Field<TabItem>(window, "SourcesTab");
        var grid = Field<DataGrid>(window, "ResultsGrid");
        var menu = Field<Menu>(window, "MainMenu");
        var divider = Field<Border>(window, "MainMenuDivider");
        Require(tabs.SelectedItem == settings && settings.Header as string == "설정" && settings.IsVisible &&
            window.OwnedWindows.Count == 0 && panel.Visibility == Visibility.Collapsed,
            "처음 열었을 때 메인 창의 설정 탭이 선택되어야 합니다.");
        double onePixel = 1 / VisualTreeHelper.GetDpi(menu).DpiScaleY;
        Require(Math.Abs(menu.ActualHeight - 25) <= onePixel && divider.BorderThickness.Bottom == 1 &&
            Math.Abs(tabs.TranslatePoint(new Point(), window).Y -
                divider.TranslatePoint(new Point(0, divider.ActualHeight), window).Y) <= onePixel,
            "메뉴 높이와 작업 탭 위 구분선이 기준과 맞지 않습니다.");
        var localizedHeadings = menu.Items.Cast<MenuItem>().Select(item => item.Header as string).ToArray();
        Require(localizedHeadings.SequenceEqual(new[] { "파일(_F)", "편집(_E)", "보기(_V)", "내보내기(_X)" }),
            "한국어 메뉴에 올바른 접근 키가 표시되지 않습니다.");
        Capture(window, output, "00-settings.png");
        Click(window, "AnalyzeButton");
        await WaitFor(() => tabs.SelectedItem == panel && grid.Items.Count > 0, "자료 판독과 첫 목록");
        Require(grid.Items.Count == 40, $"공개 EML 1 + 합성 MBOX 3 + Apache MBOX 36을 예상했습니다. 관측 {grid.Items.Count}");
        VerifyAlternatingRows(grid);
        Capture(window, output, "00-mail-row-bands.png");
        WithMenuModifier(0xA4, () =>
        {
            var scope = PresentationSource.FromVisual(menu);
            Require(AccessKeyManager.IsKeyRegistered(scope, "F") && AccessKeyManager.IsKeyRegistered(scope, "E") &&
                AccessKeyManager.IsKeyRegistered(scope, "V") && AccessKeyManager.IsKeyRegistered(scope, "X"),
                "메뉴 접근 키가 WPF 키보드 입력 범위에 등록되지 않았습니다.");
        });
        var query = Field<TextBox>(window, "SearchQueryBox");
        var searchIcon = Field<Button>(window, "SearchButton");
        Require(ReferenceEquals(query.Parent, searchIcon.Parent) &&
            searchIcon.TranslatePoint(new Point(searchIcon.ActualWidth, 0), query).X < query.Padding.Left,
            "검색 아이콘이 입력란 안에 있거나 텍스트와 분리되는지 확인하지 못했습니다.");
        query.Focus();
        query.Text = "NO_MESSAGES_FOR_THIS_QUERY_517E";
        query.Text = "SECOND_MAIL_1A06";
        await WaitFor(() => grid.Items.Count == 1 && RecordAt(grid, 0).BodyParts.Any(part => part.Text.Contains("SECOND_MAIL_1A06", StringComparison.Ordinal)),
            "Enter 없이 마지막으로 입력한 검색어 적용");
        bool focused = query.IsKeyboardFocusWithin;
        Require(FocusManager.GetFocusedElement(window) == query || focused, "자동 검색 뒤 검색창 포커스를 잃었습니다.");
        await Task.Delay(275);
        Require(grid.Items.Count == 1, "이전 검색어의 결과가 마지막 검색 결과를 덮었습니다.");
        var composition = new TextComposition(InputManager.Current, query, "");
        query.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
        query.Clear();
        await Task.Delay(300);
        Require(grid.Items.Count == 1, "한글 입력 조합 중 검색을 실행했습니다.");
        query.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            { RoutedEvent = TextCompositionManager.TextInputEvent });
        await WaitFor(() => grid.Items.Count == 40, "검색어를 지우고 전체 목록 복구");
        log.Add(new { Check = "live mail search without Enter", LastInputWins = true, ClearsToAll = true, KeyboardFocus = focused });
        var settingsCommand = Field<MenuItem>(window, "ShowSettingsMenuItem");
        settingsCommand.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, settingsCommand));
        await Idle();
        Require(tabs.SelectedItem == settings && settingsCommand.InputGestureText == "Ctrl+,", "보기 메뉴에서 설정을 열지 못했습니다.");
        var themes = Field<ComboBox>(window, "SettingsThemeBox");
        var languages = Field<ComboBox>(window, "SettingsLanguageBox");
        int originalTheme = themes.SelectedIndex;
        Color originalCanvas = ((SolidColorBrush)Application.Current.Resources["Viewer.Canvas"]).Color;
        themes.SelectedIndex = 1 - originalTheme;
        Require(((SolidColorBrush)Application.Current.Resources["Viewer.Canvas"]).Color != originalCanvas,
            "설정 탭에서 테마를 전환하지 못했습니다.");
        themes.SelectedIndex = originalTheme;
        languages.SelectedIndex = 1;
        Require(settings.Header as string == "Settings" && menu.Items.Cast<MenuItem>().First().Header as string == "_File",
            "설정에서 영어로 전환한 뒤 탭이나 메뉴 접근 키가 번역되지 않았습니다.");
        Capture(window, output, "00-settings-en.png");
        languages.SelectedIndex = 0;
        Click(window, "CancelIntakeButton");
        Require(tabs.SelectedItem == panel && grid.Items.Count == 40, "설정을 닫았을 때 메일 목록으로 돌아가지 못했습니다.");


        Require(Field<TextBlock>(window, "WorkStatus").Text.Length == 0, "자료 판독을 마친 뒤 하단에 중복 집계가 표시됩니다.");
        await Search(window, SearchField.Body, "SECOND_MAIL_1A06", 1);
        grid.SelectedIndex = 0;
        var attachmentTab = Field<TabItem>(window, "AttachmentsTab");
        attachmentTab.IsSelected = true;
        await Idle();
        Require(attachmentTab.Header as string == "첨부파일 (0)" &&
            Field<TextBlock>(window, "AttachmentEmpty") is { IsVisible: true, Text: "첨부파일이 없습니다." },
            "첨부가 없는 메일의 탭 건수 또는 빈 상태가 잘못 표시됐습니다.");
        Capture(window, output, "09-no-attachments.png");

        await Search(window, SearchField.Body, "BODY_ONLY_4C7F", 1);
        grid.SelectedIndex = 0;
        Require(attachmentTab.Header as string == "첨부파일 (1)" &&
            Field<TextBlock>(window, "WorkStatus").Text.Length == 0, "현재 메일의 첨부 건수나 검색 완료 상태가 잘못 표시됩니다.");
        var first = RecordAt(grid, 0);
        Require(first.Format == MailFormat.Mbox && first.MessageRange.Offset == 50,
            "합성 MBOX 적중 행의 원본 위치가 다릅니다.");
        grid.SelectedIndex = 0;
        var details = Field<TabControl>(window, "DetailTabs");
        details.SelectedItem = Field<TabItem>(window, "BodyTab");
        await Idle();
        var searchRail = Field<Border>(window, "MailboxSearchRail");
        var evidenceRail = (FrameworkElement)details.Template.FindName("EvidenceTabRail", details)!;
        var allMessages = Field<Grid>(window, "AllMailboxRail");
        double searchRailTop = searchRail.TranslatePoint(new Point(0, 0), window).Y;
        double evidenceRailTop = evidenceRail.TranslatePoint(new Point(), window).Y;
        double allMessagesTop = allMessages.TranslatePoint(new Point(), window).Y;
        bool SamePixel(double left, double right) => Math.Abs(left - right) <= onePixel;
        double evidenceRows = Math.Max(1, Math.Round(evidenceRail.ActualHeight / 36));
        Require(SamePixel(searchRail.ActualHeight, 36) && SamePixel(evidenceRail.ActualHeight, evidenceRows * 36) &&
            SamePixel(allMessages.ActualHeight, searchRail.ActualHeight) &&
            SamePixel(evidenceRailTop, searchRailTop) && SamePixel(allMessagesTop, searchRailTop),
            $"전체 메일 행, 검색 줄, 읽기 탭의 경계가 다릅니다. " +
            $"높이 {allMessages.ActualHeight:F2}/{searchRail.ActualHeight:F2}/{evidenceRail.ActualHeight:F2}, " +
            $"시작 {allMessagesTop:F2}/{searchRailTop:F2}/{evidenceRailTop:F2}");
        Require(Field<Button>(window, "BodyAttachmentsButton") is { IsVisible: true, Tag: Geometry } &&
            Field<TextBlock>(window, "BodyAttachmentCount").Text == "첨부파일 1개" &&
            FindVisualDescendant<StackPanel>(grid, "ResultAttachmentCount")?.Children[0] is System.Windows.Shapes.Path,
            "본문이나 메일 목록에서 첨부 표시를 찾을 수 없습니다.");
        var body = Field<TextBox>(window, "BodyBox");
        var plain = Field<RadioButton>(window, "BodyPlainButton");
        var html = Field<RadioButton>(window, "BodyHtmlButton");
        Require(plain.IsVisible && html is { IsVisible: true, IsChecked: true } &&
            !Field<ComboBox>(window, "BodyRepresentationBox").IsVisible, "HTML 본문이 기본값으로 선택되지 않았습니다.");
        Require(body.Text.Contains("HTML_ONLY_63DE", StringComparison.Ordinal), "HTML 검색용 텍스트가 달라졌습니다.");
        plain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, plain));
        Require(plain.IsChecked == true && body.Text.Contains("BODY_ONLY_4C7F", StringComparison.Ordinal),
            "평문 버튼을 눌러 본문으로 돌아가지 못했습니다.");
        var subject = Field<JournalDesk.CopyText>(window, "ReaderSubject");
        var copySubject = (Button)subject.Template.FindName("PART_Copy", subject);
        double textEnd = subject.GetRectFromCharacterIndex(subject.Text.Length - 1, trailingEdge: true).Right;
        double iconStart = copySubject.TranslatePoint(new Point(), subject).X;
        Require(iconStart - textEnd is >= 0 and <= 24, $"본문 머리글의 복사 아이콘이 글자와 떨어져 있습니다. {iconStart - textEnd:F1} DIP");
        Capture(window, output, "10-body-attachment-icon.png");
        Click(window, "BodyAttachmentsButton");
        Require(details.SelectedItem == attachmentTab, "본문에서 첨부 목록으로 이동하지 못했습니다.");
        await Search(window, SearchField.Body, "SECOND_MAIL_1A06", 1);
        Require(details.SelectedItem == Field<TabItem>(window, "BodyTab") &&
            !Field<Button>(window, "BodyAttachmentsButton").IsVisible &&
            body.Text.Contains("SECOND_MAIL_1A06", StringComparison.Ordinal),
            "첨부가 없는 메일로 이동한 뒤 본문이 바로 표시되지 않습니다.");
        await Search(window, SearchField.Body, "BODY_ONLY_4C7F", 1);
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        var received = first.Headers.Where(x => x.Name.Equals("Received", StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(received.Length == 2 && first.DateRaw.Contains("+0900", StringComparison.Ordinal),
            "Received 순서와 Date 시간대를 확인하지 못했습니다.");
        Require(RawHeaderNames(window).SequenceEqual(first.Headers.Select(header => header.Name), StringComparer.OrdinalIgnoreCase), "헤더 원문에서 필드가 빠졌습니다.");
        var infoTab = details.Items.OfType<TabItem>().Single(tab => AutomationProperties.GetAutomationId(tab) == "MailInfoTab");
        details.SelectedItem = infoTab;
        await Idle();
        var fields = FindVisualDescendant<DataGrid>(details, "MailFieldsGrid")
            ?? throw new InvalidOperationException("메일 정보 표가 보이지 않습니다.");
        var bcc = fields.Items.Cast<object>().Single(field => MailFieldText(field, "Name") == "숨은 참조");
        Require(MailFieldText(bcc, "Value") == "" && MailFieldText(bcc, "Source") == "",
            "숨은 참조가 없는 메일에서 값이나 출처를 표시했습니다.");
        fields.SelectedItem = fields.Items.Cast<object>().First(field => MailFieldText(field, "Source") == "Received #1");
        await Idle();
        Require(!Field<Grid>(window, "ReaderAddressSummary").IsVisible, "메일 정보 탭에 주소 머리글이 중복됩니다.");
        Capture(window, output, "07-message-information.png");
        await UseEvidenceMenu(fields, "OpenMailFieldSource");
        await Idle();
        Require(details.SelectedItem == Field<TabItem>(window, "HeadersTab") &&
            Field<TextBox>(window, "RawHeaderText").SelectedText.StartsWith("Received:", StringComparison.OrdinalIgnoreCase),
            "메일 정보에서 해당 Received 원문을 선택하지 못했습니다.");
        var headerBack = Field<Button>(window, "RawHeaderBackButton");
        var headerBytes = Field<Button>(window, "RawHeaderBytesButton");
        Require(!Field<Border>(window, "ReaderHeader").IsVisible && headerBack.IsVisible &&
            headerBack.TranslatePoint(new Point(0, 0), window).X < headerBytes.TranslatePoint(new Point(0, 0), window).X,
            "헤더 원문이 메일 머리글을 반복하거나 복귀 버튼이 오른쪽에 표시됩니다.");
        Capture(window, output, "08-raw-headers.png");
        Click(window, "RawHeaderBytesButton");
        var raw = Field<HexEvidenceView>(window, "RawBytesBox");
        await WaitFor(() => raw.Text.Contains("Received", StringComparison.Ordinal), "선택한 헤더 원본 바이트");
        Require(details.SelectedItem == Field<TabItem>(window, "RawTab"), "원본 바이트 탭이 열리지 않았습니다.");
        MatchDisplayedBytes(raw.Text, synthetic, received[0].Range);
        Require(!Field<Border>(window, "ReaderHeader").IsVisible && !Field<StackPanel>(window, "RawPager").IsVisible &&
            Field<Button>(window, "RawBackButton").Content as string == "헤더 원문" &&
            raw.SelectedField?.Label == "Received", "한 구간의 헤더 Hex에서 불필요한 이동 버튼 또는 잘못된 출처를 표시했습니다.");
        var hexBody = FindVisualDescendant<HexStrip>(raw, "HexByteGrid")
            ?? throw new InvalidOperationException("Hex 바이트 표가 보이지 않습니다.");
        var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(hexBody),
            Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent };
        hexBody.RaiseEvent(right);
        Require(right.Handled && raw.SelectedOffset == 1, "Hex 바이트를 방향키로 이동하지 못했습니다.");
        raw.SelectRange(7, 2);
        byte[] selected = Convert.FromHexString(raw.SelectedHex.Replace(" ", "", StringComparison.Ordinal));
        Require(raw.SelectedOffset == 2 && raw.SelectionStart == 2 && raw.SelectionLength == 6 &&
            File.ReadAllBytes(synthetic).AsSpan((int)received[0].Range.Offset + 2, 6).SequenceEqual(selected),
            "Hex 범위를 역방향으로 선택했을 때 원본 바이트와 달라졌습니다.");
        Capture(window, output, "01-mbox-header.png");
        log.Add(new { Check = "synthetic MBOX search and header", first.MessageRange, FirstReceived = received[0].Range });

        await Search(window, SearchField.AttachmentName, "Test TxtA.txt", 1);
        var attachmentMail = RecordAt(grid, 0);
        Require(attachmentMail.Format == MailFormat.Eml && attachmentMail.Attachments.Count == 2,
            "실제 EML에서 두 첨부를 찾지 못했습니다.");
        grid.SelectedIndex = 0;
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        var contentType = attachmentMail.Headers.First(x => x.Name == "Content-Type");
        SelectRawHeader(window, attachmentMail, contentType);
        Click(window, "RawHeaderBytesButton");
        MatchDisplayedBytes(raw.Text, eml, contentType.Range);
        details.SelectedItem = Field<TabItem>(window, "AttachmentsTab");
        await Idle();
        var attachmentGrid = Field<DataGrid>(window, "AttachmentsGrid");
        var wanted = attachmentMail.Attachments.Select((a, i) => (Attachment: a, Index: i))
            .Single(x => x.Attachment.Name == "Test TxtA.txt");
        attachmentGrid.SelectedIndex = wanted.Index;
        Require(wanted.Attachment.Sha256 == TikaAttachmentHash && wanted.Attachment.DecodedLength == 33,
            "실제 EML 첨부 해시 또는 크기가 독립 기준과 다릅니다.");
        Require(FindVisualDescendant<Image>(attachmentGrid, "TableAttachmentIcon") is { IsVisible: true, Source: not null },
            "첨부파일 표의 파일 형식 아이콘이 보이지 않습니다.");
        Require(attachmentGrid.Columns.Count == 3 &&
            Field<Expander>(window, "AttachmentLocationExpander") is { IsVisible: true, IsExpanded: false } &&
            EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value == wanted.Attachment.PartPath) &&
            EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value.StartsWith("0x575", StringComparison.Ordinal)),
            "MIME location is available separately from the attachment table");
        Require(EvidenceFields(window, "AttachmentDetailFields").Any(field => field.Value == TikaAttachmentHash),
            "the complete decoded attachment hash is separate");
        VerifyAlternatingRows(attachmentGrid);
        Capture(window, output, "02-eml-attachment.png");
        var directAttachment = await SaveAttachmentDirectly(window, output, wanted.Attachment);
        Require(Hash(directAttachment) == TikaAttachmentHash, "직접 저장한 EML 첨부의 해시가 다릅니다.");
        Require(wanted.Attachment.EncodedRange == new ByteRange(0x575, 45), "실제 EML 첨부 위치가 기준과 다릅니다.");
        Click(window, "OpenAttachmentBytesButton");
        MatchDisplayedBytes(raw.Text, eml, wanted.Attachment.EncodedRange!.Value);
        details.SelectedItem = Field<TabItem>(window, "AttachmentsTab");
        await Idle();
        var attachmentPackage = await Export(window, "ExportAttachmentButton", output);
        var attachmentPath = Directory.EnumerateFiles(attachmentPackage, "*Test TxtA.txt").Single();
        Require(Hash(attachmentPath) == TikaAttachmentHash, "저장한 첨부 해시가 다릅니다.");
        Require(Field<TextBox>(window, "SearchQueryBox").Text == "Test TxtA.txt" &&
            RecordAt(grid, grid.SelectedIndex).Id == attachmentMail.Id, "첨부 내보내기 뒤 조사 위치가 달라졌습니다.");
        log.Add(new { Check = "public EML header, encoded attachment, saved SHA-256 and return",
            ContentTypeRange = contentType.Range, wanted.Attachment.EncodedRange,
            Bytes = new FileInfo(attachmentPath).Length, Sha256 = TikaAttachmentHash,
            DirectFile = directAttachment, Package = attachmentPackage });

        await Search(window, SearchField.Subject, "mime", 4);
        var archiveIds = grid.Items.Cast<object>().Select(RecordFromRow).Select(x => x.Id).ToArray();
        Require(grid.Items.Cast<object>().Select(RecordFromRow).All(x => x.SourcePath == archive),
            "공개 MBOX 제목 검색에 다른 입력의 메일이 포함됐습니다.");
        var archiveRow = grid.Items.Cast<object>().Single(row => RecordFromRow(row).MessageRange.Offset == 32630);
        grid.SelectedItem = archiveRow;
        var archiveFirst = RecordFromRow(archiveRow);
        Require(archiveFirst.MessageRange.Offset == 32630 && archiveFirst.HeaderRange.Length == 2059 &&
            archiveFirst.Headers.Count(x => x.Name == "Received") == 8, "공개 MBOX 헤더가 독립 기준과 다릅니다.");
        var archiveReceived = archiveFirst.Headers.First(x => x.Name == "Received");
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        SelectRawHeader(window, archiveFirst, archiveReceived);
        Click(window, "RawHeaderBytesButton");
        MatchDisplayedBytes(raw.Text, archive, archiveReceived.Range);
        grid.SelectedIndex = 1;
        var selectedId = RecordAt(grid, grid.SelectedIndex).Id;
        var archivePackage = await Export(window, "ExportSearchButton", output);
        var manifestPath = Path.Combine(archivePackage, "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var savedIds = manifest.RootElement.GetProperty("records").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).ToArray();
        Require(savedIds.SequenceEqual(archiveIds), "공개 MBOX 검색 결과와 내보낸 ID·순서가 다릅니다.");
        Require(Field<TextBox>(window, "SearchQueryBox").Text == "mime" && grid.Items.Count == 4 &&
            RecordAt(grid, grid.SelectedIndex).Id == selectedId, "검색 복귀 뒤 검색어·건수·선택 행이 달라졌습니다.");
        var logicalFocus = FocusManager.GetFocusedElement(window);
        Require(logicalFocus == grid || logicalFocus is DataGridRow focusedRow && focusedRow.Item == grid.SelectedItem,
            "검색 복귀 뒤 논리 포커스가 조사 목록을 가리키지 않습니다.");
        Capture(window, output, "04-return.png");
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
            Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(enter);
        await Idle();
        Require(enter.Handled && Field<FrameworkElement>(window, "MailboxReaderPane").IsVisible
            && RecordAt(grid, grid.SelectedIndex).Id == selectedId, "복귀 후 Enter가 복구한 행을 읽기 창에 열지 못했습니다.");
        await ShowRawHeaders(window);
        await Idle();
        Require(details.SelectedItem == Field<TabItem>(window, "HeadersTab") &&
            RawHeaderNames(window).SequenceEqual(RecordAt(grid, grid.SelectedIndex).Headers.Select(header => header.Name), StringComparer.OrdinalIgnoreCase),
            "읽기 창에서 복구한 행의 원문을 열지 못했습니다.");
        log.Add(new { Check = "public Apache MBOX search, header, export and return", Hits = archiveIds.Length,
            archiveFirst.HeaderRange, ReceivedHeaders = 8,
            SelectedRecordPreserved = true, GridLogicalFocus = true, GridKeyboardFocus = grid.IsKeyboardFocusWithin,
            EnterOpenedReader = true, ReaderOpenedSelectedHeaders = true, Package = archivePackage });

        ClickMenu(window, "EnglishLanguageMenuItem");
        await Idle();
        Require(Field<Button>(window, "SearchButton").Content?.ToString() == "Search" &&
            Field<TextBox>(window, "SearchQueryBox").Text == "mime" &&
            RecordAt(grid, grid.SelectedIndex).Id == selectedId, "언어 전환 뒤 검색 조건이나 선택이 달라졌습니다.");
        Capture(window, output, "06-english.png");
        ClickMenu(window, "KoreanLanguageMenuItem");

        window.Width = 760;
        window.Height = 590;
        await Idle();
        await ReturnToMailboxList(window);
        Require(Field<MenuItem>(window, "ExportMenu").IsVisible &&
            Field<TextBox>(window, "SearchQueryBox").ActualWidth > 0, "좁은 창에서 주요 조사 명령을 찾을 수 없습니다.");
        Capture(window, output, "05-narrow.png");
        await Search(window, SearchField.Subject, "NO_MESSAGES_FOR_THIS_QUERY_517E", 0);
        await Idle();
        Require(Field<TextBlock>(window, "EmptyResultsHint").IsVisible, "빈 검색 결과에 안내가 없습니다.");
        Capture(window, output, "13-empty-search.png");
        WithMenuModifier(0xA2, () =>
        {
            var shortcut = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                Environment.TickCount, Key.OemComma) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(shortcut);
            Require(shortcut.Handled, "Ctrl+,가 설정 단축키로 동작하지 않았습니다.");
        });
        await Idle();
        Require(tabs.SelectedItem == settings && languages.IsVisible && Field<Button>(window, "CancelIntakeButton").IsVisible,
            "좁은 창에서 설정 항목이나 복귀 버튼을 찾을 수 없습니다.");
        Capture(window, output, "00-settings-narrow.png");
        Click(window, "CancelIntakeButton");
        Require(inputs.All(path => originalHashes[path] == Hash(path)), "원본 SHA-256이 달라졌습니다.");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            Checks = log, OriginalsUnchanged = true, Theme = theme,
            Sources = originalHashes.ToDictionary(x => Path.GetFileName(x.Key), x => x.Value)
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }

    private static void WithMenuModifier(int key, Action verify)
    {
        var original = new byte[256];
        Require(GetKeyboardState(original), "WPF 테스트 스레드의 키 상태를 확인하지 못했습니다.");
        byte[] pressed = (byte[])original.Clone();
        pressed[key] |= 0x80;
        Require(SetKeyboardState(pressed), "WPF 테스트 스레드에 조합 키를 설정하지 못했습니다.");
        try { verify(); }
        finally { SetKeyboardState(original); }
    }

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] keys);

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] keys);

    private static T? FindVisualDescendant<T>(DependencyObject parent, string automationId) where T : DependencyObject
    {
        if (parent is T match && AutomationProperties.GetAutomationId(match) == automationId)
            return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindVisualDescendant<T>(VisualTreeHelper.GetChild(parent, i), automationId) is { } child)
                return child;
        }
        return null;
    }

    private static async Task ReturnToMailboxList(MainWindow window)
    {
        var back = Field<Button>(window, "ReaderBackButton");
        if (back.IsVisible)
        {
            Click(window, "ReaderBackButton");
            await Idle();
        }
    }

    private static async Task Search(MainWindow window, SearchField requestedField, string query, int expected)
    {
        var field = Field<ComboBox>(window, "SearchFieldBox");
        field.SelectedItem = field.Items.Cast<object>().Single(x =>
            x.GetType().GetProperty("Value")?.GetValue(x) is SearchField value && value == requestedField);
        Field<TextBox>(window, "SearchQueryBox").Text = query;
        Click(window, "SearchButton");
        await WaitFor(() => Field<DataGrid>(window, "ResultsGrid").Items.Count == expected &&
            !Field<Button>(window, "CancelWorkButton").IsEnabled, $"검색 {query}, 예상 {expected}건");
    }

    private static async Task<string> SaveAttachmentDirectly(MainWindow window, string output, MailAttachment attachment)
    {
        var button = Field<Button>(window, "SaveAttachmentFileButton");
        var attachmentGrid = Field<DataGrid>(window, "AttachmentsGrid");
        var details = Field<TabControl>(window, "DetailTabs");
        Require(button.IsVisible && button.IsEnabled, "첨부파일 저장 버튼을 사용할 수 없습니다.");
        Require(ReferenceEquals(attachmentGrid.Columns[2].HeaderStyle, attachmentGrid.Columns[1].HeaderStyle),
            "크기 머리글 스타일이 다른 열과 다릅니다.");
        string directory = Path.Combine(output, "direct-attachment");
        Directory.CreateDirectory(directory);
        Field<TextBox>(window, "DestinationBox").Text = directory;
        string path = Path.Combine(directory, attachment.Name);
        Click(window, "SaveAttachmentFileButton");
        await WaitFor(() => File.Exists(path) && Field<Button>(window, "SaveAttachmentFileButton").IsEnabled,
            "시스템 저장 대화상자에서 선택한 첨부파일");
        Require(details.SelectedItem == Field<TabItem>(window, "AttachmentsTab") &&
            ReferenceEquals(attachmentGrid.SelectedItem, attachment) &&
            Directory.GetFiles(directory).SequenceEqual([path]),
            "직접 저장 후 선택이 바뀌거나 첨부파일 외에 다른 파일이 생성되었습니다.");
        return path;
    }

    private static async Task<string> Export(MainWindow window, string button, string output,
        ExportKind? requestedKind = null, string? captureName = null)
    {
        var before = Directory.EnumerateDirectories(output, "mail-evidence-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Click(window, button);
        await WaitFor(() => Field<TextBox>(window, "DestinationBox").IsVisible, "내보내기 범위 확인");
        if (requestedKind is { } kind)
        {
            var format = Field<ComboBox>(window, "ExportKindBox");
            format.SelectedItem = format.Items.Cast<object>().Single(option =>
                option.GetType().GetProperty("Value")?.GetValue(option) is ExportKind value && value == kind);
        }
        var destination = Field<TextBox>(window, "DestinationBox");
        destination.Text = output;
        await Idle();
        var exportFormat = Field<ComboBox>(window, "ExportKindBox");
        var panel = Field<Border>(window, "ExportPanel");
        if (exportFormat.IsVisible)
        {
            Require(Math.Abs(destination.TranslatePoint(new Point(), panel).X - exportFormat.TranslatePoint(new Point(), panel).X) <= 1
                && destination.ActualHeight == exportFormat.ActualHeight, "내보내기 형식과 저장 폴더 입력란을 정렬하지 못했습니다.");
            if (!File.Exists(Path.Combine(output, "14-export-plan.png")))
                Capture(window, output, "14-export-plan.png");
        }
        Click(window, "SaveExportButton");
        string[] NewPackages() => Directory.EnumerateDirectories(output, "mail-evidence-*").Where(x => !before.Contains(x)).ToArray();
        var verify = Field<Button>(window, "VerifyExportButton");
        await WaitFor(() => NewPackages().Length == 1 && verify.IsEnabled && verify.IsVisible, "출력 파일 저장");
        Click(window, "VerifyExportButton");
        var report = Field<TextBox>(window, "ExportReportBox");
        await WaitFor(() => !Field<Button>(window, "CancelWorkButton").IsEnabled &&
            Field<TextBlock>(window, "WorkStatus").Text.StartsWith("검증 완료", StringComparison.Ordinal) &&
            report.Text.Contains("검증", StringComparison.Ordinal), "화면에서 결과 재검증");
        var package = NewPackages().Single();
        var outcome = ExportService.Verify(package);
        Require(outcome.Success, string.Join(Environment.NewLine, outcome.Errors));
        if (captureName is not null)
            Capture(window, output, captureName);
        else if (button == "ExportSearchButton")
            Capture(window, output, "03-export-verification.png");
        Click(window, "ReturnToSearchButton");
        await WaitFor(() => !Field<FrameworkElement>(window, "ExportPanel").IsVisible, "검색 복귀");
        return package;
    }

    private static T Field<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) is T control ? control : throw new InvalidOperationException($"화면 요소를 찾을 수 없습니다. {name}");

    private static string? MailFieldText(object field, string property) =>
        field.GetType().GetProperty(property)?.GetValue(field) as string;

    private static string[] RawHeaderNames(MainWindow window) =>
        System.Text.RegularExpressions.Regex.Matches(Field<TextBox>(window, "RawHeaderText").Text, @"^([^:\s]+):",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).ToArray();

    private static string SelectRawHeader(MainWindow window, MailRecord record, MailHeader header)
    {
        var text = Field<TextBox>(window, "RawHeaderText");
        string prefix = header.Name + ":";
        int occurrence = record.Headers.TakeWhile(value => value != header)
            .Count(value => value.Name.Equals(header.Name, StringComparison.OrdinalIgnoreCase));
        var matches = System.Text.RegularExpressions.Regex.Matches(text.Text,
            "^" + System.Text.RegularExpressions.Regex.Escape(prefix),
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Require(occurrence < matches.Count, $"원문에서 헤더를 찾지 못했습니다. {header.Name}");
        text.Select(matches[occurrence].Index, prefix.Length);
        return text.SelectedText;
    }

    private static async Task ShowRawHeaders(MainWindow window)
    {
        var tabs = Field<TabControl>(window, "DetailTabs");
        var tabIds = tabs.Items.Cast<TabItem>().Select(tab =>
            string.IsNullOrEmpty(tab.Name) ? AutomationProperties.GetAutomationId(tab) : tab.Name);
        Require(tabIds.SequenceEqual(["BodyTab", "MailInfoTab", "AttachmentsTab", "ConversationTab",
            "HeadersTab", "RawTab", "InvestigationCluesTab"]), "읽기 창의 조사 탭 구성이나 순서가 다릅니다.");
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => AutomationProperties.GetAutomationId(tab) == "MailInfoTab");
        await Idle();
        Require(Field<DataGrid>(window, "MailFieldsGrid").IsVisible && Field<DataGrid>(window, "OverviewFields").IsVisible &&
            Field<DataGrid>(window, "OverviewFields").Items.Count >= 2, "메일 정보 탭에 필드와 자료 출처가 함께 표시되지 않습니다.");
        tabs.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        Require(Field<Button>(window, "RawHeaderBytesButton").IsVisible,
            "원문 탭에서 바이트 명령을 표시하지 않았습니다.");
    }

    private static void InFilterDialog(MainWindow window, Action<Window> edit, bool apply = true)
    {
        var toggle = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "AddressFiltersToggle");
        Exception? failure = null;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            // Let the native Expander transition finish before taking screenshots.
            await Task.Delay(320);
            Window? dialog = window.OwnedWindows.OfType<Window>().SingleOrDefault(item => item.IsVisible);
            try
            {
                Require(dialog is not null && dialog != window, "검색 조건이 모달로 열리지 않았습니다.");
                edit(dialog!);
                Click(window, apply ? "ApplyFilterButton" : "CancelFilterButton");
            }
            catch (Exception error)
            {
                failure = error;
                dialog?.Close();
            }
        }));
        toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, toggle));
        if (failure is not null) throw failure;
        Require(toggle.IsChecked != true, "검색 조건 창을 닫은 뒤에도 필터 토글이 선택돼 있습니다.");
    }

    private static void Click(MainWindow window, string name)
    {
        if (window.FindName(name) is MenuItem item)
        {
            Require(item.IsEnabled && Field<MenuItem>(window, "ExportMenu").IsVisible,
                $"사용할 수 없는 내보내기 항목입니다. {name}");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            return;
        }
        var control = Field<Button>(window, name);
        Require(control.IsEnabled && control.IsVisible, $"사용할 수 없는 버튼입니다. {name}");
        control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, control));
    }

    private static async Task UseEvidenceMenu(FrameworkElement target, string id)
    {
        var menu = target.ContextMenu ?? throw new InvalidOperationException("Evidence context menu is unavailable.");
        menu.PlacementTarget = target;
        menu.IsOpen = true;
        await Idle();
        var item = menu.Items.OfType<MenuItem>().Single(command => AutomationProperties.GetAutomationId(command) == id);
        Require(item.IsEnabled, $"Evidence menu command is disabled. {id}");
        menu.IsOpen = false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }

    private static void VerifyAlternatingRows(ItemsControl items)
    {
        Require(items.AlternationCount == 2 && items.Items.Count >= 2, "row alternation must be enabled");
        items.UpdateLayout();
        for (int index = 0; index < 2; index++)
        {
            var row = (Control)items.ItemContainerGenerator.ContainerFromIndex(index);
            int parity = ItemsControl.GetAlternationIndex(row);
            bool selected = row is DataGridRow { IsSelected: true } or ListBoxItem { IsSelected: true };
            string brush = selected ? "Viewer.Selection" : parity == 0 ? "Viewer.Canvas" : "Viewer.RowAlternate";
            Require(parity == index && row.Background is SolidColorBrush fill &&
                fill.Color == ((SolidColorBrush)items.FindResource(brush)).Color,
                $"Row {index + 1} must use {brush}, including when it is selected.");
        }
    }

    private static (string Label, string Value)[] EvidenceFields(MainWindow window, string control) =>
        Field<ItemsControl>(window, control).Items.Cast<object>().Select(field =>
            ((string)field.GetType().GetProperty("Label")!.GetValue(field)!,
             (string)field.GetType().GetProperty("Value")!.GetValue(field)!)).ToArray();

    private static void ClickMenu(MainWindow window, string id)
    {
        static IEnumerable<MenuItem> Items(ItemsControl parent) =>
            parent.Items.OfType<MenuItem>().SelectMany(item => new[] { item }.Concat(Items(item)));

        var menu = Items(Field<Menu>(window, "MainMenu")).Single(item =>
            AutomationProperties.GetAutomationId(item) == id);
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
    }

    private static MailRecord RecordAt(DataGrid grid, int index)
    {
        Require(index >= 0 && index < grid.Items.Count, "선택한 행을 찾을 수 없습니다.");
        return RecordFromRow(grid.Items[index]);
    }

    private static MailRecord RecordFromRow(object row) =>
        (row as SearchHit)?.Record ?? row.GetType().GetProperty("Record")?.GetValue(row) as MailRecord
        ?? throw new InvalidOperationException("화면 행에서 메일을 찾을 수 없습니다.");

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void MatchDisplayedBytes(string render, string input, ByteRange range)
    {
        var expected = new byte[checked((int)range.Length)];
        using var stream = File.OpenRead(input);
        stream.Position = range.Offset;
        stream.ReadExactly(expected);
        Require(expected.SequenceEqual(DisplayedBytes(render, range.Offset)),
            "화면에 표시된 Hex 바이트가 원본 파일과 다릅니다.");
    }

    private static async Task WaitFor(Func<bool> predicate, string stage)
    {
        for (var i = 0; i < 150; i++)
        {
            await Idle();
            if (predicate())
                return;
            await Task.Delay(70);
        }
        throw new InvalidOperationException($"화면의 예상 상태를 확인하지 못했습니다. {stage}");
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static void Capture(Window window, string output, string name) =>
        CaptureSurface((FrameworkElement)window.Content, output, name);

    private static void CaptureSurface(FrameworkElement view, string output, string name)
    {
        view.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(view);
        string display = Path.Combine(output, "display-environment.json");
        if (!File.Exists(display))
            File.WriteAllText(display, JsonSerializer.Serialize(new
            {
                dpiX = dpi.PixelsPerInchX, dpiY = dpi.PixelsPerInchY, highContrast = SystemParameters.HighContrast
            }));
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(view.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(view.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name));
        png.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
