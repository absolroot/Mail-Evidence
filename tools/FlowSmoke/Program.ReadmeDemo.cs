using JournalDesk;
using MailViewer.App;
using MailViewer.Core;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunReadmeDemo(string workspace, string output)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var root = Path.Combine(workspace, "Mail-Viewer");
        if (!Directory.Exists(root)) root = workspace;
        const int messageCount = 24;
        const string featureSubject = "Harbor Market poster and booth map";
        const string anomalySubject = "Updated Harbor Market artwork";
        const string posterName = "market-poster.png";
        const string mapName = "market-stall-map.png";
        var fixtures = Path.Combine(root, "tests", "fixtures", "readme-demo");
        string[] paths = [Path.Combine(fixtures, "Inbox.mbox"), Path.Combine(fixtures, "Sent.mbox")];
        var window = new MainWindow(paths, Path.Combine(output, "marks"))
        {
            Width = 1440,
            Height = 770,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Idle();
            ClickMenu(window, "EnglishLanguageMenuItem");
            ClickMenu(window, "DarkThemeMenuItem");
            var list = Field<DataGrid>(window, "ResultsGrid");
            var workspaceTabs = Field<TabControl>(window, "WorkspaceTabs");
            var details = Field<TabControl>(window, "DetailTabs");
            var search = Field<TextBox>(window, "SearchQueryBox");
            var body = Field<TabItem>(window, "BodyTab");
            var mailboxAnomalies = Field<TabItem>(window, "AnomalyMailboxTab");
            var messageAnomalies = Field<TabItem>(window, "InvestigationCluesTab");
            bool CanRead() => !Field<Button>(window, "CancelWorkButton").IsEnabled;

            var clicks = new Dictionary<string, Point>();
            var surface = (FrameworkElement)window.Content;
            void Mark(string name, FrameworkElement control, double? x = null, double? y = null)
            {
                surface.UpdateLayout();
                Require(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0, $"Missing GIF target: {name}");
                var point = control.TranslatePoint(new Point(x ?? control.ActualWidth / 2, y ?? control.ActualHeight / 2), surface);
                Require(point.X >= 0 && point.Y >= 0 && point.X < surface.ActualWidth && point.Y < surface.ActualHeight,
                    $"GIF target outside the captured window: {name}");
                clicks.Add(name, new Point(point.X / surface.ActualWidth, point.Y / surface.ActualHeight));
            }

            var settings = Field<TabItem>(window, "SourcesTab");
            workspaceTabs.SelectedItem = settings;
            var analyze = Field<Button>(window, "AnalyzeButton");
            await WaitFor(() => analyze.IsVisible && analyze.IsEnabled, "README source selection");
            Click(window, "AnalyzeButton");
            await WaitFor(() => list.Items.Count == messageCount && CanRead(), "README mail list");
            list.SelectedItem = list.Items.Cast<object>().Single(row => RecordFromRow(row).Subject == featureSubject);
            details.SelectedItem = body;
            var bodyImage = Field<Image>(window, "BodyAttachmentPreviewImage");
            bool BodyPreviewReady() => ShowsImage(bodyImage, posterName, 960, 485);
            await WaitFor(BodyPreviewReady, "README message preview");
            var addressLabels = Field<Grid>(window, "ReaderAddressSummary").Children.OfType<TextBlock>().Select(label => label.Text);
            Require(addressLabels.SequenceEqual(["From", "To", "Date"]), "README message labels must be in English.");
            Capture(window, output, "01-mail.png");

            Mark("search", search, 34);
            search.Focus();
            search.Text = "pos";
            search.CaretIndex = search.Text.Length;
            await Idle();
            Capture(window, output, "02-typing.png");
            search.Text = "poster";
            search.CaretIndex = search.Text.Length;
            await WaitFor(() => list.Items.Count is > 1 and < messageCount && CanRead() && BodyPreviewReady(), "README live search");
            Capture(window, output, "03-search.png");

            var attachmentTab = Field<TabItem>(window, "AttachmentsTab");
            Mark("attachments", attachmentTab);
            details.SelectedItem = attachmentTab;
            var attachments = Field<DataGrid>(window, "AttachmentsGrid");
            var previewImage = Field<Image>(window, "AttachmentPreviewImage");
            await WaitFor(() => attachments.Items.Count == 3 && ShowsImage(previewImage, posterName, 960, 485),
                "README attachment poster");
            var map = attachments.Items.Cast<MailAttachment>().Single(attachment => attachment.Name == mapName);
            Mark("preview", await ReadmeDemoRow(attachments, map, "README booth map row"), 55);
            Capture(window, output, "04-attachments.png");
            attachments.SelectedItem = map;
            bool MapReady() => ShowsImage(previewImage, mapName, 960, 545);
            await WaitFor(MapReady, "README booth map preview");
            Capture(window, output, "05-preview.png");

            double dockedImageWidth = previewImage.ActualWidth;
            var expand = Field<Button>(window, "AttachmentPreviewExpandButton");
            var resultsPane = Field<Grid>(window, "MailboxResultsPane");
            Mark("expand", expand);
            Click(window, "AttachmentPreviewExpandButton");
            await WaitFor(() => !resultsPane.IsVisible && MapReady(), "README expanded image preview");
            Capture(window, output, "06-expanded.png");

            var fileInfo = Field<Expander>(window, "AttachmentInfoToggle");
            Mark("file-info", fileInfo, fileInfo.ActualWidth - 22, 21);
            fileInfo.IsExpanded = false;
            await WaitFor(() => previewImage.ActualWidth > dockedImageWidth * 1.25, "README larger image preview");
            Capture(window, output, "06b-image.png");

            Mark("restore", expand);
            Click(window, "AttachmentPreviewExpandButton");
            await WaitFor(() => resultsPane.IsVisible && MapReady(), "README restore attachment view");
            Capture(window, output, "07-restored.png");

            Mark("reset", search, search.ActualWidth - 18);
            search.Clear();
            await WaitFor(() => list.Items.Count == messageCount && CanRead(), "README search reset");
            Capture(window, output, "08-all.png");
            Mark("anomaly-mailbox", mailboxAnomalies);
            workspaceTabs.SelectedItem = mailboxAnomalies;
            var findings = Field<DataGrid>(window, "InvestigationFindings");
            await WaitFor(() => list.Items.Count == 2 && CanRead() && details.SelectedItem == messageAnomalies &&
                list.SelectedItem is { } row && RecordFromRow(row).Subject == anomalySubject && findings.Items.Count == 4,
                "README anomaly evidence");
            Capture(window, output, "09-anomaly-mailbox.png");

            var pngFinding = findings.Items.Cast<object>().Single(finding =>
                Equals(finding.GetType().GetProperty("Target")!.GetValue(finding), "invalid-idat.png"));
            Mark("png-length", await ReadmeDemoRow(findings, pngFinding, "README PNG finding row"), 72);
            findings.SelectedItem = pngFinding;
            await WaitFor(() => Field<TextBlock>(window, "FindingOutcome").Text.Contains("declared length of IDAT", StringComparison.Ordinal)
                && Field<TextBlock>(window, "FindingBytePosition").Text.Contains("0x21", StringComparison.Ordinal),
                "README English PNG comparison");
            Capture(window, output, "11-anomaly.png");

            Mark("hex", Field<Button>(window, "OpenFindingSourceButton"));
            Click(window, "OpenFindingSourceButton");
            var raw = Field<HexEvidenceView>(window, "RawBytesBox");
            await WaitFor(() => raw.SelectedField is { IsWarning: true } mark && raw.BaseOffset + mark.Start == 0x21,
                "README marked attachment bytes");
            Capture(window, output, "12-hex.png");

            Mark("back", Field<Button>(window, "RawBackButton"));
            Click(window, "RawBackButton");
            await Idle();
            Require(details.SelectedItem == messageAnomalies && ReferenceEquals(findings.SelectedItem, pngFinding),
                "README return to selected anomaly");
            Capture(window, output, "13-return.png");

            File.WriteAllText(Path.Combine(output, "clicks.json"), JsonSerializer.Serialize(clicks));
            File.WriteAllText(Path.Combine(output, "flow-result.json"),
                JsonSerializer.Serialize(new
                {
                    Language = CultureInfo.CurrentUICulture.Name, Messages = messageCount,
                    Preview = mapName, Findings = findings.Items.Count, Offset = "0x21"
                }));
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<DataGridRow> ReadmeDemoRow(DataGrid grid, object item, string description)
    {
        await WaitFor(() => grid.IsVisible && grid.ActualWidth > 0, description + " grid");
        grid.ScrollIntoView(item);
        DataGridRow? row = null;
        await WaitFor(() => (row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow)
            is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }, description);
        return row!;
    }

    private static bool ShowsImage(Image view, string name, int width, int height) =>
        view.IsVisible &&
        view.Source is BitmapSource image && image.PixelWidth == width && image.PixelHeight == height &&
        AutomationProperties.GetName(view) == name;
}
