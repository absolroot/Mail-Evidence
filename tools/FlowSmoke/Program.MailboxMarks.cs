using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using MailViewer.App;
using MailViewer.Core;

namespace MailViewer.FlowSmoke;

public static partial class Program
{
    private static async Task RunMailboxMarks(string output, string? theme)
    {
        string marks = Path.Combine(output, "marks");
        string mail = Path.Combine(output, "messages");
        Directory.CreateDirectory(mail);
        DateTime today = DateTime.Today;
        var specimens = new[]
        {
            ("Today report", today.AddHours(10)),
            ("Yesterday artwork", today.AddDays(-1).AddHours(11)),
            ("Current month", today.Day > 2 ? new DateTime(today.Year,today.Month,1,9,0,0) : today.AddHours(4)),
            ("Previous month", today.AddMonths(-1).AddHours(8))
        };
        byte[] pixels = new byte[120 * 64 * 4];
        for (int y = 0; y < 64; ++y)
            for (int x = 0; x < 120; ++x)
            {
                int i = (y * 120 + x) * 4;
                pixels[i] = x > 70 ? (byte)100 : (byte)232;
                pixels[i + 1] = y > 30 ? (byte)174 : (byte)82;
                pixels[i + 2] = x > 70 ? (byte)247 : (byte)41;
                pixels[i + 3] = 255;
            }
        var bitmap = BitmapSource.Create(120, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 480);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        string[] inputs = specimens.Select((_, i) => Path.Combine(mail, $"message-{i + 1}.eml")).ToArray();
        for (int i = 0; i < inputs.Length; i++)
        {
            var fixture = specimens[i];
            string date = new DateTimeOffset(fixture.Item2).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
            string headers = $"From: investigator@example.test\r\nTo: analyst@example.test\r\nSubject: {fixture.Item1}\r\nDate: {date}\r\nMIME-Version: 1.0\r\n";
            string message = i != 1 ? headers + "\r\nInspectable message." : headers + $$"""
                Content-Type: multipart/mixed; boundary="mail-test"

                --mail-test
                Content-Type: text/html; charset=utf-8

                <p>Report with an image</p><img alt="Local evidence image" src="data:image/png;base64,{{Convert.ToBase64String(png.ToArray())}}">
                --mail-test
                Content-Type: text/plain
                Content-Disposition: attachment; filename="investigation-notes.txt"

                Provided document.
                --mail-test
                Content-Type: text/plain
                Content-Disposition: attachment; filename="second-document.txt"

                The second attachment.
                --mail-test--
                """.ReplaceLineEndings("\r\n");
            File.WriteAllText(inputs[i], message);
        }
        string[] hashes = inputs.Select(Hash).ToArray();

        var window = await Open();
        var grid = Field<DataGrid>(window, "ResultsGrid");
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        var headings = view.Groups!.OfType<CollectionViewGroup>().Select(group => group.Name?.ToString()).ToArray();
        Require(headings.Contains("오늘") && headings.Contains("어제") && headings.Count() >= 3 &&
            (today.Day <= 2 || headings.Contains("이번 달")), "Calendar sections are incomplete.");
        object row = grid.Items.Cast<object>().Single(item => RecordFromRow(item).Subject == "Yesterday artwork");
        grid.SelectedItem = row;
        grid.ScrollIntoView(row);
        await Idle();
        var itemContainer = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
        var title = FindVisualDescendant<TextBlock>(itemContainer, "MailboxSubject")!;
        Require(title.FontWeight == FontWeights.SemiBold && title.Foreground == window.FindResource("Viewer.Accent"),
            "The subject should still indicate unread before the body is explicitly opened.");
        var card = FindVisualDescendant<Button>(itemContainer, "MailboxAttachmentCard");
        var thumbnail = FindVisualDescendant<Button>(itemContainer, "MailboxImageThumbnail");
        Require(card is { IsVisible: true }, "A named attachment card is unavailable.");
        await WaitFor(() => thumbnail is { IsVisible: true } && thumbnail.Content is Image { Source: BitmapSource },
            "visible row's in-message image preview");
        var bitmapSource = (BitmapSource)((Image)thumbnail!.Content).Source;
        Require(bitmapSource.IsFrozen && bitmapSource.PixelWidth <= 168, "The thumbnail has no bounded bitmap.");
        var detailTabs = Field<TabControl>(window, "DetailTabs");
        card!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, card));
        await Idle();
        Require(detailTabs.SelectedItem == Field<TabItem>(window, "AttachmentsTab") &&
            Field<DataGrid>(window, "AttachmentsGrid").SelectedItem is MailAttachment { Name: "investigation-notes.txt" },
            "The attachment card did not open the matching attachment.");
        thumbnail.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, thumbnail));
        Require(detailTabs.SelectedItem == Field<TabItem>(window, "BodyTab"), "The inline thumbnail did not open the body.");
        Require(title.FontWeight == FontWeights.Normal &&
            Directory.GetFiles(Path.Combine(marks, "Viewed"), "*.mark").Length == 1,
            "Explicitly opening the body must update the title and save one read mark.");

        var readerFlag = Field<Button>(window, "ReaderBookmarkButton");
        var readerPin = Field<Button>(window, "ReaderPinButton");
        Require(readerFlag.IsVisible && readerPin.IsVisible &&
            readerFlag.TranslatePoint(new Point(), window).X > Field<FrameworkElement>(window, "ReaderSubject").TranslatePoint(new Point(), window).X,
            "Reader actions must appear to the right of the subject.");
        Click(window, "ReaderBookmarkButton");
        Click(window, "ReaderPinButton");
        await WaitFor(() => ReferenceEquals(grid.Items[0], row) && ReferenceEquals(grid.SelectedItem, row),
            "pin is ordered before date sections and keeps selection");
        var bookmarks = Field<TabItem>(window, "BookmarksMailboxTab");
        Require(bookmarks.Header as string == "북마크 (1건)" &&
            ((CollectionViewGroup)view.Groups![0]!).Name?.ToString() == "고정됨",
            "The pinned group or bookmark count did not update.");
        Require(readerFlag.Tag == window.FindResource("Lucide.BookmarkCheck") &&
            readerPin.Tag == window.FindResource("Lucide.Pin") && MainWindow.GetFillCommandIcon(readerPin) &&
            readerFlag.Content as string == "북마크 해제" && readerPin.Content as string == "상단 고정 해제",
            "The reader controls must reflect the saved mark states.");
        var noAttachments = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1);
        Require(FindVisualDescendant<Button>(noAttachments, "MailboxAttachmentCard") is { IsVisible: false },
            "A message without attachments has an empty attachment card.");
        await Idle();
        Capture(window, output, "calendar-and-cards.png");
        Capture(window, output, "reader-actions.png");

        var tabs = Field<TabControl>(window, "WorkspaceTabs");
        tabs.SelectedItem = bookmarks;
        await WaitFor(() => grid.Items.Count == 1 && !Field<Button>(window, "CancelWorkButton").IsEnabled, "bookmark tab result");
        Require(RecordAt(grid,0).Subject == "Yesterday artwork", "The wrong record appears in Bookmarks.");
        Capture(window, output, "bookmarks.png");
        var exported = await Export(window, "ExportSearchButton", output, ExportKind.Csv);
        Require(File.ReadAllLines(Path.Combine(exported, "messages.csv")).Length == 2, "Bookmark export contains another mail.");
        window.Close();

        window = await Open();
        grid = Field<DataGrid>(window, "ResultsGrid");
        Require(RecordAt(grid,0).Subject == "Yesterday artwork", "The pin was not restored on reopening.");
        var restored = grid.Items[0];
        grid.ScrollIntoView(restored);
        await Idle();
        var restoredRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(restored);
        Require(FindVisualDescendant<TextBlock>(restoredRow, "MailboxSubject")?.FontWeight == FontWeights.Normal,
            "The saved read state was lost on reopening.");
        bookmarks = Field<TabItem>(window, "BookmarksMailboxTab");
        Require(bookmarks.Header as string == "북마크 (1건)", "The bookmark was not restored on reopening.");
        tabs = Field<TabControl>(window, "WorkspaceTabs");
        tabs.SelectedItem = bookmarks;
        await WaitFor(() => grid.Items.Count == 1 && !Field<Button>(window, "CancelWorkButton").IsEnabled,
            "bookmarks reloaded");
        grid.UpdateLayout();
        var bookmarkedRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        var flag = FindVisualDescendant<Button>(bookmarkedRow, "MailboxBookmarkButton")!;
        flag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, flag));
        await WaitFor(() => grid.Items.Count == 0 && bookmarks.Header as string == "북마크 (0건)", "unbookmark removes only that row");

        tabs.SelectedItem = Field<TabItem>(window, "InvestigationTab");
        var busy = Field<Button>(window, "CancelWorkButton");
        await WaitFor(() => grid.Items.Count == 4 && !busy.IsEnabled, "return to all mail");
        var selection = Field<ToggleButton>(window, "SelectionModeButton");
        var bulk = Field<Button>(window, "BookmarkCheckedButton");
        selection.IsChecked = true;
        selection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, selection));
        Require(!bulk.IsVisible, "The bulk command is present with no checked mail.");
        Check(0);
        Check(1);
        Require(bulk.IsVisible && bulk.Content as string == "북마크" && Field<Button>(window, "ExportCheckedButton").IsVisible,
            "Both actions are needed when two messages are checked.");
        Capture(window, output, "bulk-bookmark.png");
        Click(window, "BookmarkCheckedButton");
        await WaitFor(() => bookmarks.Header as string == "북마크 (2건)" && !busy.IsEnabled &&
            bulk.Content as string == "북마크 해제", "the checked messages were bookmarked");
        Require(Directory.GetFiles(Path.Combine(marks, "Bookmarks"), "*.mark").Length == 2,
            "Both bookmarks must be written.");
        tabs.SelectedItem = bookmarks;
        await WaitFor(() => grid.Items.Count == 2 && bulk.IsEnabled && !busy.IsEnabled, "two checked bookmarks are retained in the tab");
        Click(window, "BookmarkCheckedButton");
        await WaitFor(() => grid.Items.Count == 0 && bookmarks.Header as string == "북마크 (0건)" && !busy.IsEnabled,
            "removing checked bookmarks updates the active list");
        Require(selection.IsEnabled && !bulk.IsVisible && Directory.GetFiles(Path.Combine(marks, "Bookmarks"), "*.mark").Length == 0,
            "The empty list must allow exiting selection mode after removal.");
        Require(inputs.Select(Hash).SequenceEqual(hashes), "The marking or preview operation modified source mail.");
        window.Close();
        File.WriteAllText(Path.Combine(output, "flow-result.json"), JsonSerializer.Serialize(new
        {
            Groups = headings, ThumbnailWidth = bitmapSource.PixelWidth, Persisted = true, BookmarkExport = true,
            BulkBookmarkAndRemove = true, HeaderIconStates = true, ReadStateRestored = true
        }));

        void Check(int index)
        {
            grid.SelectedIndex = index;
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid),
                Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            grid.RaiseEvent(key);
            Require(key.Handled, "Space must check a selected message in selection mode.");
        }

        async Task<MainWindow> Open()
        {
            var instance = new MainWindow(inputs, marks) { ShowInTaskbar = false, Width = 1190, Height = 795 };
            instance.Show();
            await Idle();
            if (theme is not null) ClickMenu(instance, theme == "--dark" ? "DarkThemeMenuItem" : "LightThemeMenuItem");
            Click(instance, "AnalyzeButton");
            await WaitFor(() => Field<DataGrid>(instance, "ResultsGrid").Items.Count == 4 &&
                !Field<Button>(instance, "CancelWorkButton").IsEnabled, "all four fixture emails");
            return instance;
        }
    }
}

