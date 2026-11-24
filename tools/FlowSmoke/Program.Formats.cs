using JournalDesk;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunFormats(string output, string? theme)
    {
        string input = Path.Combine(output, "input");
        Directory.CreateDirectory(input);
        string mime = "From: reader@example.test\r\nTo: lab@example.test\r\nDate: Mon, 23 Nov 2026 10:00:00 +0000\r\n"
            + "Subject: ZIP marker 9A08\r\nMessage-ID: <archive@example.test>\r\n"
            + "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=sample\r\n\r\n"
            + "--sample\r\nContent-Type: text/plain\r\n\r\narchive body marker\r\n"
            + "--sample\r\nContent-Type: text/plain\r\nContent-Disposition: attachment; filename=archive.txt\r\n\r\n"
            + "archive attachment\r\n--sample--\r\n";
        string zip = Path.Combine(input, "two-folders.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (path, body) in new[] { ("Inbox/message.eml", mime), ("Other/other.eml", mime.Replace("9A08", "other")) })
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(body);
            }
        }
        string emlx = Path.Combine(input, "one.emlx");
        string apple = mime.Replace("ZIP marker 9A08", "EMLX marker 79E9").Replace("archive@example.test", "apple@example.test");
        File.WriteAllText(emlx, $"{Encoding.UTF8.GetByteCount(apple)}\n{apple}<plist/>", new UTF8Encoding(false));
        var window = new MainWindow([zip, emlx]) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        Click(window, "AnalyzeButton");

        var grid = Field<DataGrid>(window, "ResultsGrid");
        var details = Field<TabControl>(window, "DetailTabs");
        var raw = Field<HexEvidenceView>(window, "RawBytesBox");
        await WaitFor(() => grid.Items.Count == 3 && Field<TabItem>(window, "InvestigationTab").IsVisible, "ZIP and EMLX intake");
        await Search(window, SearchField.Subject, "ZIP marker 9A08", 1);
        var record = RecordAt(grid, 0);
        Require(record.LogicalFolder is { Path: "/Inbox" } && record.ContainerMessage?.Kind == "archive_member", "archive member scope");
        var attachment = record.Attachments.Single();
        details.SelectedItem = Field<TabItem>(window, "HeadersTab");
        await Idle();
        Require(RawHeaderNames(window).Contains("Subject"), "archive raw header fields");
        SelectRawHeader(window, record, record.Headers.Single(item => item.Name == "Subject"));
        Click(window, "RawHeaderBytesButton");
        Require(raw.ByteCount > 4 && raw.AddressLabel.Contains("저장 항목", StringComparison.Ordinal), "archive header uses evidence-relative coordinates");
        Capture(window, output, "01-archive-header.png");
        Click(window, "RawBackButton");
        Require(details.SelectedItem == Field<TabItem>(window, "HeadersTab"), "return from archive header bytes");

        details.SelectedItem = Field<TabItem>(window, "AttachmentsTab");
        await Idle();
        Require(EvidenceFields(window, "AttachmentLocationFields").Any(field => field.Value.Contains("Inbox/message.eml", StringComparison.Ordinal)),
            "attachment source identifies the archive member");
        var package = await Export(window, "ExportAttachmentButton", output);
        string exported = Directory.GetFiles(package, "*archive.txt").Single();
        Require(Hash(exported) == attachment.Sha256, "archive attachment export matches inspected bytes");

        await Search(window, SearchField.Subject, "EMLX marker 79E9", 1);
        var emlxRecord = RecordAt(grid, 0);
        Require(emlxRecord.Format == MailFormat.Emlx && emlxRecord.MessageRange.Offset > 0, "EMLX excludes length prefix");
        details.SelectedItem = Field<TabItem>(window, "RawTab");
        await Idle();
        Require(raw.BaseOffset == emlxRecord.MessageRange.Offset, "EMLX uses physical file offsets");
        Capture(window, output, "02-emlx-message.png");

        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            Read = 3, ArchiveMember = record.LogicalFolder?.Path, AttachmentSha256 = attachment.Sha256,
            EmlxStart = emlxRecord.MessageRange.Offset, Theme = theme
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }
}
