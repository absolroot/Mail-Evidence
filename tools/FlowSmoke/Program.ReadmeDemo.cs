using JournalDesk;
using MailViewer.App;
using MailViewer.Core;
using MimeKit;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunReadmeDemo(string workspace, string output)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var root = Path.Combine(workspace, "Mail-Viewer");
        if (!Directory.Exists(root)) root = workspace;
        var paths = PrepareReadmeMail(root, output);
        var window = new MainWindow(paths, Path.Combine(output, "marks"))
        {
            Width = 1280,
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

            var settings = Field<TabItem>(window, "SourcesTab");
            workspaceTabs.SelectedItem = settings;
            var analyze = Field<Button>(window, "AnalyzeButton");
            await WaitFor(() => analyze.IsVisible && analyze.IsEnabled, "README source selection");
            Click(window, "AnalyzeButton");
            await WaitFor(() => list.Items.Count == 3 && CanRead(), "README mail list");
            list.SelectedItem = list.Items.Cast<object>().Single(row => RecordFromRow(row).Subject == "Attachment structure review");
            details.SelectedItem = body;
            await WaitFor(() => Field<TextBlock>(window, "BodyAttachmentPreviewText").Text.Contains("Image set A", StringComparison.Ordinal),
                "README message preview");
            var addressLabels = Field<Grid>(window, "ReaderAddressSummary").Children.OfType<TextBlock>().Select(label => label.Text);
            Require(addressLabels.SequenceEqual(["From", "To", "Date"]), "README message labels must be in English.");
            Capture(window, output, "01-mail.png");

            search.Text = "structure";
            await WaitFor(() => list.Items.Count == 1 && CanRead() &&
                Field<TextBlock>(window, "BodyAttachmentPreviewText").Text.Contains("Image set A", StringComparison.Ordinal),
                "README live search");
            Capture(window, output, "02-search.png");

            details.SelectedItem = Field<TabItem>(window, "AttachmentsTab");
            var attachments = Field<DataGrid>(window, "AttachmentsGrid");
            await WaitFor(() => attachments.Items.Count == 6, "README attachments");
            attachments.SelectedItem = attachments.Items.Cast<MailAttachment>().Single(attachment => attachment.Name == "inspection-notes.txt");
            await WaitFor(() => Field<TextBox>(window, "AttachmentPreviewTextBox").Text.Contains("Image set A", StringComparison.Ordinal),
                "README attachment preview");
            Capture(window, output, "03-attachment.png");

            search.Clear();
            await WaitFor(() => list.Items.Count == 3 && CanRead(), "README search reset");
            workspaceTabs.SelectedItem = mailboxAnomalies;
            await WaitFor(() => list.Items.Count == 2 && CanRead(), "README anomalies mailbox");
            list.SelectedItem = list.Items.Cast<object>().Single(row => RecordFromRow(row).Subject == "Attachment structure review");
            details.SelectedItem = messageAnomalies;

            var findings = Field<DataGrid>(window, "InvestigationFindings");
            await WaitFor(() => findings.Items.Count == 4, "README anomaly evidence");
            findings.SelectedItem = findings.Items.Cast<object>().Single(finding =>
                Equals(finding.GetType().GetProperty("Target")!.GetValue(finding), "invalid-idat.png"));
            await WaitFor(() => Field<TextBlock>(window, "FindingOutcome").Text.Contains("declared length of IDAT", StringComparison.Ordinal)
                && Field<TextBlock>(window, "FindingBytePosition").Text.Contains("0x21", StringComparison.Ordinal),
                "README English PNG comparison");
            Capture(window, output, "04-anomaly.png");

            Click(window, "OpenFindingSourceButton");
            var raw = Field<HexEvidenceView>(window, "RawBytesBox");
            await WaitFor(() => raw.SelectedField is { IsWarning: true } mark && raw.BaseOffset + mark.Start == 0x21,
                "README marked attachment bytes");
            Capture(window, output, "05-hex.png");

            Click(window, "RawBackButton");
            await Idle();
            Require(details.SelectedItem == messageAnomalies &&
                Field<TextBlock>(window, "FindingOutcome").Text.Contains("IDAT", StringComparison.Ordinal),
                "README return to selected anomaly");
            Capture(window, output, "06-return.png");

            File.WriteAllText(Path.Combine(output, "flow-result.json"),
                JsonSerializer.Serialize(new { Language = CultureInfo.CurrentUICulture.Name, Findings = findings.Items.Count, Offset = "0x21" }));
        }
        finally
        {
            window.Close();
        }
    }

    private static string[] PrepareReadmeMail(string root, string output)
    {
        var fixtures = Path.Combine(root, "tests", "fixtures", "attachment-boundaries");
        var samples = Path.Combine(output, "demo-mail");
        Directory.CreateDirectory(samples);
        (string Source, string Name, string Subject, int Day)[] inputs =
        [
            ("01-normal.eml", "image-reference.eml", "Reference image set", 22),
            ("02-mixed.eml", "attachment-review.eml", "Attachment structure review", 24),
            ("03-container-boundaries.eml", "container-review.eml", "PDF and ZIP examples", 23)
        ];
        return inputs.Select(input =>
        {
            var mail = MimeMessage.Load(Path.Combine(fixtures, input.Source));
            mail.Subject = input.Subject;
            mail.Date = new DateTimeOffset(2024, 10, input.Day, 9, 0, 0, TimeSpan.Zero);
            if (input.Source == "02-mixed.eml" && mail.Body is Multipart parts)
            {
                var notes = new MimePart("text", "plain")
                {
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                    FileName = "inspection-notes.txt",
                    Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(
                        "Image set A\r\n\r\nCompare the attachment name, its file structure,\r\nand the bytes at the reported offset.\r\n")))
                };
                parts.Insert(1, notes);
            }
            var path = Path.Combine(samples, input.Name);
            mail.WriteTo(path);
            return path;
        }).ToArray();
    }
}
