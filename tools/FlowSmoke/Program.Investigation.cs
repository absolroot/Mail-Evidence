using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MailViewer.App;
using MailViewer.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunInvestigation(string output, string? theme)
    {
        string current = Path.Combine(output, "current.eml");
        string related = Path.Combine(output, "related.eml");
        File.WriteAllText(current, """
            From: Billing <billing@example.com>
            Reply-To: replies@example.net
            Message-ID: <current@example.com>
            In-Reply-To: <related@example.com>
            References: <related@example.com>
            Subject: Current invoice
            Content-Type: text/html; charset=utf-8

            <a href="https://bob.github.io/pay">https://alice.github.io/pay</a>
            """.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
        File.WriteAllText(related, """
            From: replies@example.net
            Message-ID: <related@example.com>
            Subject: Earlier invoice
            Content-Type: text/plain

            Earlier message.
            """.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
        var window = new MainWindow([current, related], Path.Combine(output, "marks")) { ShowInTaskbar = false };
        window.Show();
        await Idle();
        if (theme is not null) ClickMenu(window, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
        Click(window, "AnalyzeButton");
        var list = Field<DataGrid>(window, "ResultsGrid");
        await WaitFor(() => list.Items.Count == 2 && !Field<Button>(window, "CancelWorkButton").IsEnabled, "initial mail list");
        var workspaceTabs = Field<TabControl>(window, "WorkspaceTabs");
        var anomalyTab = Field<TabItem>(window, "AnomalyMailboxTab");
        await WaitFor(() => (string)anomalyTab.Header == "이상 징후 (1건)", "workspace count is per mail");
        await Idle();
        foreach (object row in list.Items)
        {
            list.ScrollIntoView(row);
            list.UpdateLayout();
            var container = (DataGridRow)list.ItemContainerGenerator.ContainerFromItem(row);
            var marker = FindVisualDescendant<Border>(container, "MailboxAnomalyMarker");
            Require(marker is not null && marker.IsVisible == (RecordFromRow(row).SourcePath == current),
                "Only messages with findings should show a leading marker.");
        }
        Capture(window, output, "anomaly-marker.png");
        list.SelectedItem = list.Items.Cast<object>().Single(row => RecordFromRow(row).SourcePath == current);
        await VerifyBodyLink();
        workspaceTabs.SelectedItem = anomalyTab;
        await WaitFor(() => list.Items.Count == 1 && list.IsVisible &&
            !Field<Button>(window, "CancelWorkButton").IsEnabled, "anomaly mailbox");
        Require(RecordAt(list, 0).SourcePath == current, "clean mail excluded from anomaly tab");
        Capture(window, output, "00-anomaly-mailbox.png");
        var anomalyEvidence = Field<DataGrid>(window, "InvestigationGrid");
        anomalyEvidence.SelectedItem = anomalyEvidence.Items.Cast<object>().First(item =>
            Observation(item).Field == "Reply-To");
        await Idle();
        await UseEvidenceMenu(anomalyEvidence, "SearchEvidenceValue");
        await WaitFor(() => workspaceTabs.SelectedItem == Field<TabItem>(window, "InvestigationTab") &&
            list.Items.Count == 2 && Field<Button>(window, "InvestigationSearchBack").IsVisible, "cross-tab exact lookup");
        Require(Field<TextBlock>(window, "ExactSearchLabel").Text == "같은 메일 주소" &&
            Field<TextBox>(window, "ExactSearchValue").Text == "replies@example.net",
            "exact search makes the looked-up address visible");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => workspaceTabs.SelectedItem == anomalyTab && list.Items.Count == 1, "return to anomalies");
        workspaceTabs.SelectedItem = Field<TabItem>(window, "InvestigationTab");
        await WaitFor(() => list.Items.Count == 2 && list.IsVisible &&
            !Field<Button>(window, "CancelWorkButton").IsEnabled, "all mail restored");
        InFilterDialog(window, dialog =>
        {
            var field = Field<ComboBox>(window, "SearchFieldBox");
            object originalField = field.SelectedItem;
            field.IsDropDownOpen = true;
            dialog.UpdateLayout();
            var search = (TextBox)field.Template.FindName("PART_Search", field);
            Require(search.IsVisible, "A dropdown with several options must expose a filter.");
            search.Text = "__no_matching_option__";
            Require((field.Template.FindName("EmptyOptions", field) as TextBlock)?.Visibility == Visibility.Visible &&
                Equals(field.SelectedItem, originalField), "Filtering should preserve the selected option.");
            search.Text = "제목";
            var popup = (Popup)field.Template.FindName("PART_Popup", field);
            var dropdown = (FrameworkElement)popup.Child;
            CaptureSurface(dropdown, output, "dropdown-search.png");
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search), Environment.TickCount, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            search.RaiseEvent(key);
            Require(key.Handled && !field.IsDropDownOpen && field.SelectedItem.ToString() == "제목",
                $"Enter should select the filtered option. Handled={key.Handled}, Open={field.IsDropDownOpen}, Selected={field.SelectedItem}");
            field.SelectedItem = originalField;
            Require(Field<Button>(window, "ResetFilterButton").Content as string == "초기화", "The reset command should use the short label.");
            Field<Expander>(window, "InvestigationFilters").IsExpanded = true;
            var query = Field<TextBox>(window, "InvestigationFilterQuery");
            query.Text = "회신 도메인";
            var choices = Field<StackPanel>(window, "InvestigationFilterChoices").Children.OfType<CheckBox>().ToArray();
            var selectedClue = choices
                .Single(choice => System.Windows.Automation.AutomationProperties.GetAutomationId(choice)
                    == "ClueReplyDomainMismatch");
            Require(choices.Count(choice => choice.Visibility == Visibility.Visible) == 1 &&
                selectedClue.Visibility == Visibility.Visible, "Anomaly-type search must narrow the visible values.");
            selectedClue.IsChecked = true;
            query.Text = "경유";
            Require(selectedClue.Visibility == Visibility.Collapsed && selectedClue.IsChecked == true,
                "Hidden anomaly types must keep their selection.");
            query.Clear();
            Capture(dialog, output, "01-investigation-filter.png");
        });
        await WaitFor(() => list.Items.Count == 1 && !Field<Button>(window, "CancelWorkButton").IsEnabled, "reply domain filter");
        list.SelectedIndex = 0;
        var details = Field<TabControl>(window, "DetailTabs");
        var clues = Field<TabItem>(window, "InvestigationCluesTab");
        details.SelectedItem = clues;
        await Idle();
        var grid = Field<DataGrid>(window, "InvestigationGrid");
        var reply = grid.Items.Cast<object>().First(item => Observation(item) is
            { Field: "Reply-To", Clue: InvestigationClue.ReplyDomainMismatch });
        grid.SelectedItem = reply;
        Capture(window, output, "02-reply-domain-evidence.png");
        details.SelectedItem = Field<TabItem>(window, "RawTab");
        await Idle();
        var raw = Field<JournalDesk.HexEvidenceView>(window, "RawBytesBox");
        Require(details.SelectedItem == Field<TabItem>(window, "RawTab") &&
                raw.ByteCount == Observation(reply).Location!.Range.Length, "Reply-To original bytes");
        Capture(window, output, "03-reply-domain-source.png");
        Click(window, "RawBackButton");
        await Idle();
        Require(details.SelectedItem == clues && grid.SelectedItem == reply, "evidence return");
        await UseEvidenceMenu(grid, "SearchEvidenceValue");
        await WaitFor(() => list.Items.Count == 2 && Field<Button>(window, "InvestigationSearchBack").IsVisible &&
            !Field<Button>(window, "CancelWorkButton").IsEnabled, "matching reply address");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => list.Items.Count == 1 && grid.SelectedItem is { } item && Observation(item) == Observation(reply)
            && !Field<Button>(window, "CancelWorkButton").IsEnabled, "prior query and clue selection");
        await FindReference();
        await WaitFor(() => list.Items.Count == 1 && RecordAt(list, 0).SourcePath == related &&
            Field<Button>(window, "InvestigationSearchBack").IsVisible && !Field<Button>(window, "CancelWorkButton").IsEnabled,
            "reference lookup");
        details.SelectedItem = clues;
        await Idle();
        Require((string)clues.Header == "이상 징후 (0)" && !grid.IsVisible &&
            !Field<TextBlock>(window, "FindingDescription").IsVisible, "zero anomalies show no evidence rows");
        Capture(window, output, "04-no-anomalies.png");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => list.Items.Count == 1 && RecordAt(list, 0).SourcePath == current &&
            !Field<Button>(window, "CancelWorkButton").IsEnabled, "reference return");
        ClickMenu(window, "EnglishLanguageMenuItem");
        window.Width = window.MinWidth;
        await Idle();
        Require((string)clues.Header == "Anomalies (2)", "English anomaly count");
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await Idle();
        details.SelectedItem = clues;
        await Idle();
        Require(grid.IsVisible, "keyboard opens clues in the narrow reader");
        var choices = Field<DataGrid>(window, "InvestigationFindings");
        var first = (DataGridRow)choices.ItemContainerGenerator.ContainerFromIndex(0);
        var second = (DataGridRow)choices.ItemContainerGenerator.ContainerFromIndex(1);
        var description = Field<TextBlock>(window, "FindingDescription");
        string firstDescription = description.Text;
        Require(first.IsVisible && second.IsVisible && first.ActualWidth > 100 && second.ActualWidth > 100 &&
            first.TranslatePoint(new Point(), window).Y < second.TranslatePoint(new Point(), window).Y &&
            Equals(choices.Columns[1].Header, "Affected item"),
            "both findings have visible, distinct choice targets");
        Capture(window, output, "05-investigation-narrow-en.png");
        var peer = new System.Windows.Automation.Peers.DataGridItemAutomationPeer(choices.Items[1],
            new System.Windows.Automation.Peers.DataGridAutomationPeer(choices));
        ((System.Windows.Automation.Provider.ISelectionItemProvider)peer.GetPattern(
            System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
        await Idle();
        Require(second.IsSelected && description.Text != firstDescription &&
            grid.Items.Cast<object>().All(item => Observation(item).Clue == InvestigationClue.LinkDomainMismatch),
            "selecting the second finding changes the explanation and evidence");
        Capture(window, output, "06-investigation-second-choice.png");
        choices.SelectedIndex = 0;
        await FindReference();
        await WaitFor(() => list.IsVisible && RecordAt(list, 0).SourcePath == related &&
            Field<Button>(window, "InvestigationSearchBack").IsVisible, "narrow related results");
        Click(window, "InvestigationSearchBack");
        await WaitFor(() => Field<TabControl>(window, "DetailTabs").IsVisible &&
            RecordAt(list, 0).SourcePath == current, "narrow reader restored");
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            theme, filterCount = list.Items.Count, matchingAddress = 2, reference = related,
            source = Observation(reply).Location, htmlLinkWarning = true, leadingMarker = true, searchableFilters = true
        }));
        window.Close();

        async Task VerifyBodyLink()
        {
            var host = Field<Border>(window, "BodyHtmlHost");
            await WaitFor(() => host.Child is WebView2 { CoreWebView2: not null } &&
                Field<TextBox>(window, "BodyBox").Visibility == Visibility.Collapsed, "HTML body is ready");
            var view = ((WebView2)host.Child).CoreWebView2;
            using var evaluated = JsonDocument.Parse(await view.CallDevToolsProtocolMethodAsync("Runtime.evaluate", """
                {"expression":"document.querySelector('a').href","returnByValue":true}
                """));
            string? internalUri = evaluated.RootElement.GetProperty("result").GetProperty("value").GetString();
            Require(internalUri?.StartsWith("https://mail-preview.invalid/", StringComparison.Ordinal) == true,
                "The mail URL must be represented by a local preview token.");
            bool contactedTarget = false;
            view.WebResourceRequested += (_, args) => contactedTarget |= args.Request.Uri.Contains("bob.github.io", StringComparison.Ordinal);
            var navigation = new List<string>();
            view.NavigationStarting += (_, args) => navigation.Add($"{args.Uri} User={args.IsUserInitiated} Cancel={args.Cancel}");
            string originalPage = view.Source;
            var activated = view.CallDevToolsProtocolMethodAsync("Runtime.evaluate", """
                {"expression":"document.querySelector('a').click()","userGesture":true}
                """);
            Window? warning = null;
            try
            {
                await WaitFor(() => (warning = window.OwnedWindows.OfType<Window>()
                    .FirstOrDefault(item => item.IsVisible && item.Title == "메일 링크 주의")) is not null, "link warning");
                Require(FindVisualDescendant<TextBox>(warning!, "MailLinkAddress")?.Text == "https://bob.github.io/pay" &&
                    !contactedTarget && view.Source == originalPage, "The link must show the original address without navigation.");
                Capture(warning!, output, "html-link-warning.png");
            }
            finally
            {
                warning?.Close();
                if (warning is null)
                    File.WriteAllText(Path.Combine(output, "link-events.txt"), $"{internalUri}\n{string.Join("\n", navigation)}");
            }
            await activated;
        }

        async Task FindReference()
        {
            details.SelectedItem = details.Items.Cast<TabItem>().Single(tab =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(tab) == "MailInfoTab");
            await Idle();
            Exception? error = null;
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = window.OwnedWindows.OfType<Window>().Single(item => item.IsVisible);
                try
                {
                    var values = FindVisualDescendant<ListBox>(dialog, "RelatedMailValues")!;
                    values.SelectedItem = values.Items.Cast<object>()
                        .First(item => item.GetType().GetProperty("Label")!.GetValue(item) is string label &&
                            label.StartsWith("In-Reply-To", StringComparison.Ordinal));
                    var buttons = ((DockPanel)dialog.Content).Children.OfType<StackPanel>().Single();
                    var find = buttons.Children.OfType<Button>().Single(button => button.IsDefault);
                    find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, find));
                }
                catch (Exception ex)
                {
                    error = ex;
                    dialog.Close();
                }
            }));
            ClickMenu(window, "FindRelatedMailMenuItem");
            if (error is not null) throw error;
        }

        static InvestigationObservation Observation(object row) =>
            (InvestigationObservation)row.GetType().GetProperty("Observation")!.GetValue(row)!;
    }
}
