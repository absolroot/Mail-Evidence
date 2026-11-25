using JournalDesk;
using System.IO;
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
    private static async Task RunMailFields(string workspace, string output, string? theme)
    {
        string first = Path.Combine(output, "address-fields.eml");
        string second = Path.Combine(output, "related-address.eml");
        string name = Convert.ToBase64String(Encoding.UTF8.GetBytes("회계 팀"));
        File.WriteAllText(first, $"""
            From: =?UTF-8?B?{name}?= <Accounts+Review@example.test>
            To: "Park, Analyst" <Analyst+tag@EXAMPLE.TEST>, second@example.test
            Cc: undisclosed recipients:;
            Reply-To: Desk <reply@example.test>
            Subject: Case fields
            Message-ID: <case.fields@example.test>
            Date: Sat, 21 Nov 2026 09:00:00 +0900

            Evidence fields verification.
            """.ReplaceLineEndings("\r\n"));
        File.WriteAllText(second, """
            From: Analyst+tag@example.test
            To: Accounts+Review@example.test
            Subject: Related address
            Message-ID: <case.fields@example.test>
            Date: Sat, 21 Nov 2026 09:05:00 +0900

            Related evidence fields verification.
            """.ReplaceLineEndings("\r\n"));

        var msg = Path.Combine(workspace, "Mail-Viewer-Research", "evidence", "followup", "inputs", "poi-attachment-msg-pdf.msg");
        var ost = Path.Combine(workspace, "Mail-Viewer", "tests", "fixtures", "ost", "pstextractortest.ost");
        string[] paths = [first, second, msg, ost];
        string[] hashes = paths.Select(Hash).ToArray();
        var window = new MainWindow(paths) { ShowInTaskbar = false, Width = 1170, Height = 730 };
        window.Show();
        await Idle();
        ClickMenu(window, "KoreanLanguageMenuItem");
        await Idle();
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        Click(window, "AnalyzeButton");
        var list = Field<DataGrid>(window, "ResultsGrid");
        await WaitFor(() => list.Items.Count >= paths.Length && !Field<Button>(window, "CancelWorkButton").IsEnabled, "mail fields fixture");
        int originalCount = list.Items.Count;
        var tabs = Field<TabControl>(window, "DetailTabs");
        var info = tabs.Items.OfType<TabItem>().Single(tab => AutomationProperties.GetAutomationId(tab) == "MailInfoTab");
        var fields = Field<DataGrid>(window, "MailFieldsGrid");
        var sourceFields = Field<DataGrid>(window, "OverviewFields");
        var original = list.Items.Cast<object>().Single(row => RecordFromRow(row).SourcePath == first);
        list.SelectedItem = original;
        tabs.SelectedItem = info;
        await Idle();
        var firstRows = fields.Items.Cast<object>().ToArray();
        Require(firstRows.Take(6).Select(row => MailFieldText(row, "Name")).SequenceEqual(
            ["작성자", "작성자 이메일", "받는 사람", "받는 사람 이메일", "받는 사람", "받는 사람 이메일"]),
            "Name and address rows must stay adjacent for each mailbox.");
        Require(firstRows.Take(6).Select(row => MailFieldText(row, "Value")).SequenceEqual(
            ["회계 팀", "Accounts+Review@example.test", "Park, Analyst", "Analyst+tag@EXAMPLE.TEST", "", "second@example.test"]),
            "Name decoding, email spelling, order or empty name changed.");
        Require(MailFieldText(firstRows[2], "Source") == MailFieldText(firstRows[3], "Source") &&
            MailFieldText(firstRows[3], "Source") != MailFieldText(firstRows[5], "Source"),
            "The sender/recipient fields do not identify the specific position within the same header.");
        Require(firstRows.Any(row => MailFieldText(row, "Name") == "참조 원문" &&
            MailFieldText(row, "Value") == "undisclosed recipients:;"), "An empty group must stay in the raw field.");

        fields.SelectedItem = firstRows[3];
        await VerifyCopy(fields, firstRows[3], "Analyst+tag@EXAMPLE.TEST");
        Capture(window, output, "01-address-fields.png");
        await UseEvidenceMenu(fields, "OpenMailFieldSource");
        var rawHeader = Field<TextBox>(window, "RawHeaderText");
        Require(rawHeader.SelectedText.StartsWith("To:", StringComparison.Ordinal), "Address must point to its To header.");
        Click(window, "RawHeaderBackButton");
        Require(tabs.SelectedItem == info && Equals(fields.SelectedItem, firstRows[3]),
            "Returning from the header should preserve the selected email row.");

        await UseEvidenceMenu(fields, "FindMailFieldValue");
        await WaitFor(() => list.Items.Count == 2 && !Field<Button>(window, "CancelWorkButton").IsEnabled,
            "exact address search across original domains");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => list.Items.Count == originalCount && tabs.SelectedItem == info && Equals(fields.SelectedItem, firstRows[3]),
            "return to the same email field after exact search");

        fields.SelectedItem = fields.Items.Cast<object>().Single(row => MailFieldText(row, "Name") == "Message-ID");
        await UseEvidenceMenu(fields, "FindMailFieldValue");
        await WaitFor(() => list.Items.Count == 2 && !Field<Button>(window, "CancelWorkButton").IsEnabled, "exact Message-ID search");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => list.Items.Count == originalCount && !Field<Button>(window, "CancelWorkButton").IsEnabled, "return after exact Message-ID search");

        var hashField = sourceFields.Items.Cast<object>().Single(row =>
            MailFieldText(row, "Value")?.Equals(hashes[0], StringComparison.OrdinalIgnoreCase) == true);
        await VerifyCopy(sourceFields, hashField, MailFieldText(hashField, "Value")!);

        var msgRow = list.Items.Cast<object>().Single(row =>
            RecordFromRow(row) is { Subject: "test email" } record && record.SourcePath == msg);
        string msgId = RecordFromRow(msgRow).Id;
        list.SelectedItem = msgRow;
        tabs.SelectedItem = info;
        await Idle();
        var smtp = fields.Items.Cast<object>().Single(row =>
            MailFieldText(row, "Name") == "받는 사람 이메일" && MailFieldText(row, "Source")!.StartsWith("MAPI 0x39FE", StringComparison.Ordinal));
        Require(MailFieldText(smtp, "Value") == "nick.booth@pof.com.au", "MSG reader should expose the typed SMTP property.");
        fields.SelectedItem = smtp;
        await UseEvidenceMenu(fields, "FindMailFieldValue");
        await WaitFor(() => Field<Button>(window, "InvestigationSearchBack").IsVisible &&
            !Field<Button>(window, "CancelWorkButton").IsEnabled, "MSG recipient SMTP search");
        Require(list.Items.Cast<object>().Select(RecordFromRow).Any(record => record.Id == msgId),
            "The exact SMTP search did not return the top-level MSG message.");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => list.Items.Count == originalCount && !Field<Button>(window, "CancelWorkButton").IsEnabled, "return from MSG exact search");

        var withoutHeader = list.Items.Cast<object>().Single(row => RecordFromRow(row).SourcePath == ost &&
            RecordFromRow(row).TransportHeaderState == HeaderEvidenceState.Missing &&
            RecordFromRow(row).StoredFields.Any(field => field.Name == "Sent representing SMTP address"));
        list.SelectedItem = withoutHeader;
        tabs.SelectedItem = info;
        await Idle();
        var stored = fields.Items.Cast<object>().ToArray();
        Require(stored.Take(2).Select(row => MailFieldText(row, "Value"))
                .SequenceEqual(["Outlook Team", "no-reply@microsoft.com"]) &&
            MailFieldText(stored[1], "Source") == "MAPI 0x5D02001F", "OST fallback must preserve SMTP property provenance.");
        fields.SelectedItem = stored[1];
        await VerifyCopy(fields, stored[1], "no-reply@microsoft.com");
        await UseEvidenceMenu(fields, "OpenMailFieldSource");
        Require(tabs.SelectedItem == Field<TabItem>(window, "RawTab") &&
            Field<HexEvidenceView>(window, "RawBytesBox").ByteCount > 0, "Stored address must lead to property bytes.");
        Click(window, "RawBackButton");
        Require(tabs.SelectedItem == info, "Return from the OST property bytes.");
        Capture(window, output, "02-ost-property.png");

        window.Width = 760;
        window.Height = 590;
        await Idle();
        list.SelectedItem = list.Items.Cast<object>().Single(row => RecordFromRow(row).SourcePath == first);
        await Idle();
        var narrowOpen = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(list), Environment.TickCount, System.Windows.Input.Key.Enter)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        list.RaiseEvent(narrowOpen);
        tabs.SelectedItem = info;
        ClickMenu(window, "EnglishLanguageMenuItem");
        await Idle();
        var narrow = fields.Items.Cast<object>().First(row => MailFieldText(row, "Name") == "From email");
        await VerifyCopy(fields, narrow, "Accounts+Review@example.test");
        Capture(window, output, "03-narrow-english.png");
        Require(paths.Select(Hash).SequenceEqual(hashes), "The sample inputs were modified.");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            SplitNamesAndAddresses = true, CopyValues = true, RawLocation = true,
            ExactAddressAndId = true, MsgRecipient = true, OstWithoutHeaders = true, NarrowEnglish = true
        }));
        window.Close();

        async Task VerifyCopy(DataGrid grid, object item, string expected)
        {
            grid.SelectedItem = item;
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            await Idle();
            var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item)!;
            var value = FindVisualDescendant<CopyText>(container, "MailFieldValue")
                ?? throw new InvalidOperationException("The copyable value cell was not rendered.");
            value.ApplyTemplate();
            var copy = (Button)value.Template.FindName("PART_Copy", value);
            string? captured = null;
            DataObjectCopyingEventHandler capture = (_, args) =>
            {
                captured = args.DataObject.GetData(DataFormats.UnicodeText) as string;
                args.CancelCommand();
            };
            DataObject.AddCopyingHandler(value, capture);
            value.Focus();
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, copy));
            DataObject.RemoveCopyingHandler(value, capture);
            Require(value.Text == expected && captured == expected, "The copied value must contain only the content of its row.");
        }
    }
}
