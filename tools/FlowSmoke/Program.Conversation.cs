using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunConversationHistory(string workspace, string output, string? theme)
    {
        string folder = Path.Combine(workspace, "Mail-Viewer", "tests", "fixtures", "conversation-history");
        string[] inputs = Directory.GetFiles(folder, "*.eml").Order(StringComparer.Ordinal).ToArray();
        string marks = Path.Combine(output, "conversation-marks");
        var window = new MainWindow(inputs, marks) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        Click(window, "AnalyzeButton");

        var list = Field<DataGrid>(window, "ResultsGrid");
        var details = Field<TabControl>(window, "DetailTabs");
        var anomaly = Field<TabItem>(window, "AnomalyMailboxTab");
        var workspaceTabs = Field<TabControl>(window, "WorkspaceTabs");
        var findings = Field<TabItem>(window, "InvestigationCluesTab");
        var grid = Field<DataGrid>(window, "InvestigationGrid");
        var previousResults = Field<Button>(window, "InvestigationSearchBack");
        var raw = Field<JournalDesk.HexEvidenceView>(window, "RawBytesBox");
        var relatedTab = Field<TabItem>(window, "ConversationTab");
        var body = Field<TabItem>(window, "BodyTab");

        await WaitFor(() => list.Items.Count == 3 && (string)anomaly.Header == "이상 징후 (1건)" && Ready(), "conversation anomaly count");
        workspaceTabs.SelectedItem = anomaly;
        await WaitFor(() => list.Items.Count == 1 && Ready(), "one conversation anomaly");
        Require(RecordAt(list, 0).SourcePath == inputs[1], "only the changed message matches");
        details.SelectedItem = findings;
        await Idle();
        Require((string)findings.Header == "이상 징후 (1)" &&
            Field<TextBlock>(window, "SingleFindingTitle").IsVisible && !Field<DataGrid>(window, "InvestigationFindings").IsVisible,
            "single anomaly uses a title without a selection control");
        Require(details.Items[2] == Field<TabItem>(window, "AttachmentsTab") &&
            details.Items[^1] == findings, "attachments third and anomalies last");
        Require(relatedTab.IsVisible && Equals(relatedTab.Header, "관련 메일 (2건)") &&
            details.Items.IndexOf(relatedTab) == 3, "the count is available next to attachments without using the information tab");
        var selected = list.SelectedItem;
        list.ScrollIntoView(selected);
        await Idle();
        var mailRow = (DataGridRow)list.ItemContainerGenerator.ContainerFromItem(selected);
        var subject = FindVisualDescendant<TextBlock>(mailRow, "MailboxSubject")!;
        Require(subject.FontWeight == FontWeights.SemiBold, "the anomaly stays unread before opening it");
        Field<DockPanel>(window, "FindingDetails").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        await Idle();
        Require(details.SelectedItem == findings && subject.FontWeight == FontWeights.Normal &&
            Directory.GetFiles(Path.Combine(marks, "Viewed"), "*.mark").Length == 1,
            "opening an anomaly in the findings tab marks it read without switching to the body");
        Require(Field<TextBlock>(window, "FindingDescription").Text.Contains("30일", StringComparison.Ordinal),
            "the comparison rule is explained above the values");
        Require(Field<TextBox>(window, "EarlierReplyValue").Text == "case+original@example.com" &&
            Field<TextBox>(window, "CurrentReplyValue").Text == "case+updated@example.com" && !grid.IsVisible,
            "both addresses are visible before opening the detailed evidence");
        Require(window.FindName("OpenClueSource") is null && window.FindName("FindClueMatches") is null &&
            window.FindName("OpenEarlierReplyHeader") is null && window.FindName("OpenCurrentReplyHeader") is null,
            "standalone duplicate commands are gone");
        Capture(window, output, "conversation-01-finding.png");
        await OpenReplyBytes("Earlier Reply-To", "EarlierReplyValue", inputs[0], "conversation-02-previous-bytes.png");
        await OpenReplyBytes("Current Reply-To", "CurrentReplyValue", inputs[1], "conversation-03-current-bytes.png");
        var disclosure = Field<Expander>(window, "ReplyEvidenceToggle");
        disclosure.IsExpanded = true;
        await WaitFor(() => grid.IsVisible && grid.ActualHeight > 40, "comparison evidence expanded");
        Require(grid.IsVisible && grid.Items.Count == 6, "raw findings remain available");
        disclosure.IsExpanded = false;
        await WaitFor(() => !grid.IsVisible || grid.ActualHeight < 1, "comparison evidence collapsed");
        double wide = window.Width;
        window.Width = Math.Min(wide, 1050);
        await Idle();
        Require(Field<System.Windows.Controls.Primitives.UniformGrid>(window, "ReplyComparisonColumns").Columns == 1,
            "comparison becomes vertical in the narrow split pane");
        Capture(window, output, "conversation-06-stacked-comparison.png");
        window.Width = wide;

        details.SelectedItem = relatedTab;
        await Idle();
        await OpenHistory("conversation-04-history-ko.png", inKorean: true, openEarlier: true);
        await WaitFor(() => list.Items.Count == 1 && RecordAt(list, 0).SourcePath == inputs[0] &&
            previousResults.IsVisible && Ready(), "open referenced message");
        Require(details.SelectedItem == body, "the referenced message opens in the body");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => workspaceTabs.SelectedItem == anomaly && RecordAt(list, 0).SourcePath == inputs[1] && Ready(),
            "return to conversation anomaly view");
        Require(details.SelectedItem == relatedTab && ConversationList().SelectedIndex == 0 &&
            FindVisualDescendant<Expander>(details, "ConversationReferenceHeader")!.IsExpanded,
            "restore inline conversation, selected mail and evidence disclosure");

        var package = await Export(window, "ExportSearchButton", output, ExportKind.Csv);
        Require(File.Exists(Path.Combine(package, "reply-address-changes.json")) && ExportService.Verify(package).Success,
            "conversation comparison evidence is part of the verified package");
        Require(workspaceTabs.SelectedItem == anomaly && RecordAt(list, 0).SourcePath == inputs[1],
            "export returns to the anomaly list");
        ClickMenu(window, "EnglishLanguageMenuItem");
        window.Width = window.MinWidth;
        await Idle();
        if (!details.IsVisible)
        {
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Idle();
        }
        Require(!Field<TextBox>(window, "ReaderSubject").IsKeyboardFocusWithin,
            "Opening the narrow reader should not focus the selectable title.");
        details.SelectedItem = relatedTab;
        Require(Equals(relatedTab.Header, "Related mail (2)"), "the related-count title uses the display language");
        await OpenHistory("conversation-05-history-narrow-en.png", inKorean: false, openEarlier: false);
        File.WriteAllText(Path.Combine(output, "conversation-result.json"), JsonSerializer.Serialize(new
        {
            theme, allCount = 3, anomalyCount = 1, previous = inputs[0], current = inputs[1],
            dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).PixelsPerInchX,
            highContrast = SystemParameters.HighContrast, export = package, relatedMailCount = 2, anomalyReadWithoutBody = true
        }));
        window.Close();
        await VerifyConversationCandidates(inputs, output, theme);

        bool Ready() => !Field<Button>(window, "CancelWorkButton").IsEnabled;

        async Task OpenReplyBytes(string field, string value, string path, string capture)
        {
            var result = grid.Items.Cast<object>().Single(item => Observation(item).Field == field);
            var observation = Observation(result);
            await UseEvidenceMenu(Field<TextBox>(window, value), "ViewEvidenceBytes");
            await Idle();
            byte[] bytes = File.ReadAllBytes(path).AsSpan((int)observation.Location!.Range.Offset,
                (int)observation.Location.Range.Length).ToArray();
            Require(raw.ByteCount == bytes.Length && raw.BaseOffset == observation.Location.Range.Offset,
                "original Reply-To byte range is shown");
            Require(EvidenceFields(window, "RawLocationFields").Any(field => field.Value.Equals(path, StringComparison.OrdinalIgnoreCase)),
                "the comparison source path stays visible");
            Capture(window, output, capture);
            Click(window, "RawBackButton");
            await Idle();
            Require(details.SelectedItem == findings && grid.SelectedItem == result, "return to the selected evidence row");
        }

        ListBox ConversationList() => FindVisualDescendant<ListBox>(details, "ConversationMessages")!;

        async Task OpenHistory(string capture, bool inKorean, bool openEarlier)
        {
            await Idle();
            var related = ConversationList();
            var state = FindVisualDescendant<TextBlock>(details, "ConversationComparisonStatus")!;
            var open = FindVisualDescendant<Button>(details, "OpenConversationMessage")!;
            var missing = FindVisualDescendant<Expander>(details, "ConversationMissingReferences")!;
            var evidence = FindVisualDescendant<Expander>(details, "ConversationReferenceHeader")!;
            Require(related.Items.Count == 2 && open.IsEnabled && related.SelectedIndex == 0,
                "mail records are grouped and the comparison source is selected");
            VerifyAlternatingRows(related);
            var selectedRow = (ListBoxItem)related.ItemContainerGenerator.ContainerFromIndex(0);
            var selectedSurface = (Border)selectedRow.Template.FindName("EvidenceListRowBorder", selectedRow);
            Require(selectedSurface.Background == window.FindResource("Viewer.Selection") &&
                selectedRow.Foreground == window.FindResource("Viewer.SelectionText"),
                "the actual selected row background and text use the current theme");
            Require(missing.Header.ToString()!.Contains("1", StringComparison.Ordinal) && !window.OwnedWindows.OfType<Window>().Any(w => w.IsVisible),
                "missing references are counted separately in the reader");
            Require(state.Text.Contains(inKorean ? "회신 주소가 다릅니다" : "Reply-To address changed", StringComparison.Ordinal), "change status");
            evidence.IsExpanded = true;
            await Task.Delay(550);
            await Idle();
            Capture(window, output, capture);
            if (!openEarlier) return;
            var referenceText = ((StackPanel)evidence.Content).Children.OfType<TextBox>()
                .First(box => box.Text.StartsWith("References:", StringComparison.Ordinal));
            await UseEvidenceMenu(referenceText, "OpenConversationReference");
            await Idle();
            Require(details.SelectedItem == Field<TabItem>(window, "RawTab") && raw.ByteCount > 0,
                "reference header opens its original byte range");
            Click(window, "RawBackButton");
            await Idle();
            Require(details.SelectedItem == relatedTab && evidence.IsExpanded && ConversationList() == related,
                "return to the same inline reference selection");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, open));
        }

        static InvestigationObservation Observation(object row) =>
            (InvestigationObservation)row.GetType().GetProperty("Observation")!.GetValue(row)!;
    }

    private static async Task VerifyConversationCandidates(string[] inputs, string output, string? theme)
    {
        string copy = Path.Combine(output, "copy-of-original.eml");
        File.Copy(inputs[0], copy);
        string isolated = Path.Combine(output, "unresolved-reference.eml");
        File.WriteAllText(isolated, "From: analyst@example.test\r\nSubject: Standalone fixture\r\nMessage-ID: <standalone@example.test>\r\nReferences: <uncollected@example.test>\r\n\r\nNo related messages are in the loaded sources.\r\n");
        var window = new MainWindow([.. inputs, copy, isolated], Path.Combine(output, "candidate-marks")) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        Click(window, "AnalyzeButton");
        var results = Field<DataGrid>(window, "ResultsGrid");
        await WaitFor(() => results.Items.Count == 5 && !Field<Button>(window, "CancelWorkButton").IsEnabled, "duplicate and unresolved ID samples");
        var details = Field<TabControl>(window, "DetailTabs");
        var relatedTab = Field<TabItem>(window, "ConversationTab");
        results.SelectedIndex = Enumerable.Range(0, 5).Single(index => RecordAt(results, index).SourcePath == inputs[1]);
        details.SelectedItem = relatedTab;
        await Idle();
        Require(Equals(relatedTab.Header, "관련 메일 (3건)"), "each open evidence record is counted once, including duplicate-ID candidates");
        results.SelectedIndex = Enumerable.Range(0, 5).Single(index => RecordAt(results, index).SourcePath == isolated);
        await Idle();
        Require(!relatedTab.IsVisible && details.SelectedItem == Field<TabItem>(window, "BodyTab"),
            "a message with only an unresolved reference does not show the related tab");
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        Require(Field<Button>(window, "RawHeaderCopyButton").Content as string == "복사" &&
            Field<Button>(window, "RawHeaderBytesButton").Content as string == "HEX", "raw-header actions use short labels");
        Capture(window, output, "conversation-08-zero-count-raw-header.png");
        results.SelectedIndex = Enumerable.Range(0, 5).Single(index => RecordAt(results, index).SourcePath == inputs[1]);
        details.SelectedItem = relatedTab;
        await Idle();
        var list = FindVisualDescendant<ListBox>(details, "ConversationMessages")!;
        var open = FindVisualDescendant<Button>(details, "OpenConversationMessage")!;
        Require(list.Items.Count == 2 && (string)open.Content == "후보 보기", "same Message-ID sources grouped as candidates");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, open));
        var headers = FindVisualDescendant<Expander>(details, "ConversationReferenceHeader")!;
        var candidates = ((StackPanel)headers.Content).Children.OfType<Button>()
            .Where(button => AutomationProperties.GetAutomationId(button) == "OpenConversationCandidate").ToArray();
        Require(headers.IsExpanded && candidates.Length == 2, "both source copies are separately available");
        await Task.Delay(250);
        Capture(window, output, "conversation-07-duplicate-candidates.png");
        var candidate = candidates.Single(button => (string)button.ToolTip == copy);
        candidate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, candidate));
        await WaitFor(() => results.Items.Count == 1 && RecordAt(results, 0).SourcePath == copy, "selected original by record identity");
        window.Close();
    }
}

