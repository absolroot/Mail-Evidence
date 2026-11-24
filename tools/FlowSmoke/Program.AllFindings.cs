using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunAllFindings(string workspace, string output, string? theme)
    {
        string folder = Path.Combine(workspace, "tests", "fixtures", "anomaly-ux-complete");
        string[] inputs = Directory.GetFiles(folder, "*.eml");
        Require(inputs.Length == 10, "Expected ten reusable synthetic UX samples");
        var window = new MainWindow(inputs) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        Click(window, "AnalyzeButton");
        var results = Field<DataGrid>(window, "ResultsGrid");
        await WaitFor(() => results.Items.Count == 10 && Ready(), "ten synthetic samples");
        var workspaceTabs = Field<TabControl>(window, "WorkspaceTabs");
        var anomalyTab = Field<TabItem>(window, "AnomalyMailboxTab");
        var findingsTab = Field<TabItem>(window, "InvestigationCluesTab");
        var detail = Field<TabControl>(window, "DetailTabs");
        var evidence = Field<DataGrid>(window, "InvestigationGrid");
        await WaitFor(() => (string)anomalyTab.Header == "이상 징후 (8건)", "Eight anomalous messages across all sources");
        workspaceTabs.SelectedItem = anomalyTab;
        await WaitFor(() => results.Items.Count == 8 && Ready(), "eight anomalous messages");

        var found = new HashSet<InvestigationClue>();
        for (int i = 0; i < results.Items.Count; i++)
        {
            results.SelectedIndex = i;
            detail.SelectedItem = findingsTab;
            await Idle();
            Require((string)findingsTab.Header == "이상 징후 (1)", "One finding in each individual sample");
            var observations = evidence.Items.Cast<object>().Select(row =>
                (InvestigationObservation)row.GetType().GetProperty("Observation")!.GetValue(row)!).ToArray();
            Require(observations.Length > 0 && observations.All(row => row.Finding > 0), "Evidence contains findings only");
            found.Add(observations[0].Clue);
            if (observations[0].Clue == InvestigationClue.LinkDomainMismatch)
            {
                var target = observations.Single(row => row.Field == "Link target");
                Require(target.Key?.Value == target.Value && !string.IsNullOrEmpty(target.SourceNote),
                    "Link value stays separate from source coordinates");
            }
            var facts = Field<ItemsControl>(window, "FindingFacts").Items.Cast<object>().Select(item =>
                (string)item.GetType().GetProperty("Value")!.GetValue(item)!).ToArray();
            if (observations[0].Field == "Double extension")
                Require(facts.Length == 3 && facts[0] == observations[0].Value &&
                    facts[0].EndsWith(facts[1] + facts[2], StringComparison.OrdinalIgnoreCase) && !evidence.IsVisible &&
                    Equals(Field<Button>(window, "OpenFindingSourceButton").Content, "파일명 원본 보기"),
                    "An attachment with two extensions shows its recorded name and both suffixes directly.");
            if (observations[0].Field == "Direction controls")
                Require(facts.Length == 2 && facts[1].Contains("U+202E") && facts[0].Contains(@"\u202E") && !evidence.IsVisible,
                    "The name and character position expose the actual direction control.");
            string sample = Path.GetFileNameWithoutExtension(RecordAt(results, i).SourcePath);
            Capture(window, output, "sample-" + sample + ".png");
            Require(!string.IsNullOrWhiteSpace(Field<TextBlock>(window, "FindingDescription").Text),
                "selected anomaly has a case-specific explanation");
            Click(window, "OpenFindingSourceButton");
            var raw = Field<JournalDesk.HexEvidenceView>(window, "RawBytesBox");
            await WaitFor(() => raw.ByteCount > 0 && !Field<Button>(window, "CancelWorkButton").IsEnabled, "source bytes opened");
            Require(raw.ByteCount > 0 && detail.SelectedItem == Field<TabItem>(window, "RawTab"), $"Source bytes for {sample}");
            if (observations[0].Clue == InvestigationClue.AttachmentPeMismatch)
                Require(raw.AddressLabel.Contains("디코딩") && raw.SelectedHex == "50" &&
                    raw.SelectedField is { IsWarning: true } selected &&
                    raw.BaseOffset + selected.Start == observations[1].DecodedPeHeader!.Value.Offset &&
                    observations[1].Value.Contains("50 45 00 00"),
                    "PE evidence links directly to the validated signature in the decoded attachment.");
            Click(window, "RawBackButton");
            await Idle();
            Require(detail.SelectedItem == findingsTab && evidence.SelectedIndex == 0, $"Return to evidence for {sample}");
        }
        EqualCoverage(found);

        InFilterDialog(window, dialog =>
        {
            var conditions = Field<Expander>(window, "InvestigationFilters");
            var field = Field<ComboBox>(window, "SearchFieldBox");
            var conditionPosition = conditions.TranslatePoint(new Point(), dialog);
            var fieldPosition = field.TranslatePoint(new Point(), dialog);
            Require(!conditions.IsExpanded && conditionPosition.Y > fieldPosition.Y &&
                Math.Abs(conditionPosition.X - fieldPosition.X) < 1,
                "Anomaly types should use the same aligned filter column.");
            Capture(dialog, output, "anomaly-filter-first.png");
            conditions.IsExpanded = true;
            Field<StackPanel>(window, "InvestigationFilterChoices").Children.OfType<CheckBox>()
                .Single(choice => AutomationProperties.GetAutomationId(choice) == "ClueDisguisedAttachmentName").IsChecked = true;
        });
        await WaitFor(() => results.Items.Count == 2 && Ready(), "two attachment-name findings");
        Require((string)anomalyTab.Header == "이상 징후 (8건)", "Tab count preserves the total across open sources");
        var package = await Export(window, "ExportSearchButton", output, ExportKind.Csv);
        Require(File.ReadAllLines(Path.Combine(package, "messages.csv")).Length == 3, "Export includes the two filtered mails");
        await WaitFor(() => workspaceTabs.SelectedItem == anomalyTab && results.Items.Count == 2, "return from search export");
        await Idle();
        var allTab = Field<TabItem>(window, "InvestigationTab");
        workspaceTabs.SelectedItem = allTab;
        await WaitFor(() => workspaceTabs.SelectedItem == allTab && results.Items.Count == 10 && Ready(), "all samples restored");
        int ordinary = FindSample("08-");
        results.SelectedIndex = ordinary;
        var mailInfoTab = detail.Items.Cast<TabItem>().Single(item => AutomationProperties.GetAutomationId(item) == "MailInfoTab");
        detail.SelectedItem = mailInfoTab;
        await Idle();
        var mailFields = Field<DataGrid>(window, "MailFieldsGrid");
        var authentication = mailFields.Items.Cast<object>().Where(row => MailFieldText(row, "Name") == "인증 기록")
            .Select(row => MailFieldText(row, "Source")).ToArray();
        mailFields.SelectedItem = mailFields.Items.Cast<object>().Last(row => MailFieldText(row, "Name") == "인증 기록");
        await Idle();
        Require(Field<TextBox>(window, "SelectedMailFieldValue").Text.EndsWith("smtp.mailfrom=example.net", StringComparison.Ordinal),
            "full selected authentication value is visible and selectable");
        Capture(window, output, "sample-08-authentication.png");
        Require(authentication.Length == 2 && authentication.All(source => source!.Contains("신뢰 미확인", StringComparison.Ordinal)),
            "Both recorded results show the unverified status");

        results.SelectedIndex = FindSample("10-");
        await Idle();
        Exception? pickerFailure = null;
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var picker = window.OwnedWindows.OfType<Window>().Single(item => item.IsVisible);
            try
            {
                var values = FindVisualDescendant<ListBox>(picker, "RelatedMailValues")!;
                values.SelectedItem = values.Items.Cast<object>().Single(item =>
                    item.GetType().GetProperty("Label")!.GetValue(item) is string label &&
                    label.StartsWith("SHA-256", StringComparison.Ordinal));
                var command = ((DockPanel)picker.Content).Children.OfType<StackPanel>().Single()
                    .Children.OfType<Button>().Single(button => button.IsDefault);
                command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, command));
            }
            catch (Exception error)
            {
                pickerFailure = error;
                picker.Close();
            }
        }));
        ClickMenu(window, "FindRelatedMailMenuItem");
        if (pickerFailure is not null) throw pickerFailure;
        await WaitFor(() => results.Items.Count == 2 && Ready() &&
            Field<Button>(window, "InvestigationSearchBack").IsVisible, "same attachment SHA-256");
        Require(FindSample("07-") >= 0 && FindSample("10-") >= 0, "same attachment under different filenames");
        if (SystemParameters.HighContrast)
        {
            var folderTree = Field<TreeView>(window, "PstFolderTree");
            var root = folderTree.Items.OfType<TreeViewItem>().First();
            Require(root.IsSelected && root.Header is TextBlock
                { Foreground: System.Windows.Media.SolidColorBrush foreground } &&
                foreground.Color == SystemColors.HighlightTextColor, "selected folder uses Windows highlight text");
        }
        Require(Field<TextBlock>(window, "ExactSearchLabel").Text == "동일 첨부 SHA-256" &&
            Field<TextBox>(window, "ExactSearchValue").Text.Length == 64, "complete attachment digest is visible");
        Capture(window, output, "sample-10-same-attachment.png");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => results.Items.Count == 10 && Ready(), "return from attachment hash lookup");

        for (int i = 0; i < results.Items.Count; i++)
        {
            if (!Path.GetFileName(RecordAt(results, i).SourcePath).StartsWith("09-", StringComparison.Ordinal)) continue;
            results.SelectedIndex = i;
            detail.SelectedItem = findingsTab;
            await Idle();
            Require((string)findingsTab.Header == "이상 징후 (1)" &&
                Field<TextBlock>(window, "ClueStatus").Text.Contains("일부 항목", StringComparison.Ordinal), "Unreadable clue coverage");
            Capture(window, output, "sample-09-unavailable.png");
            break;
        }
        File.WriteAllText(Path.Combine(output, "all-findings-result.json"), JsonSerializer.Serialize(new
        {
            samples = inputs.Length, anomalousMessages = 8, kinds = found.Select(clue => clue.ToString()),
            exportRows = 2
        }));
        window.Close();

        int FindSample(string prefix) => Enumerable.Range(0, results.Items.Count).Single(index =>
            Path.GetFileName(RecordAt(results, index).SourcePath).StartsWith(prefix, StringComparison.Ordinal));

        bool Ready() => !Field<Button>(window, "CancelWorkButton").IsEnabled;

        static void EqualCoverage(HashSet<InvestigationClue> actual)
        {
            Require(actual.SetEquals(Enum.GetValues<InvestigationClue>().Where(clue => clue is not (InvestigationClue.None or InvestigationClue.ReplyAddressChanged))),
                "Every anomaly rule has a visible sample");
        }
    }
}
